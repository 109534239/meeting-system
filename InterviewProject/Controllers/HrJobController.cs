using InterviewProject.Data;
using InterviewProject.Models;
using InterviewProject.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace InterviewProject.Controllers
{
    public class HrJobController : Controller
    {
        private readonly AppDbContext _db;
        private readonly AutoInterviewSchedulingService _scheduler;

        public HrJobController(AppDbContext db, AutoInterviewSchedulingService scheduler)
        {
            _db = db;
            _scheduler = scheduler;
        }

        // 權限檢查 helper
        private bool IsEmployee()
        {
            var role = HttpContext.Session.GetString("MemberRole")?.ToLower();
            return role == "hr" || role == "manager" || role == "employee";
        }

        // GET: 職缺列表
        //    🎯 篩選條件：部門、上架狀態、日期（落在上架~截止之間）、職缺名稱關鍵字
        public async Task<IActionResult> Index(string department, string status, string date, string keyword)
        {
            if (!IsEmployee()) return RedirectToAction("Index", "Login");

            var query = _db.Jobs
                .Include(j => j.Manager)    // 🎯 部門主管 (原本的 ManagerName 改成關聯到 Employee)
                .AsQueryable();

            // 部門：直接用資料庫裡實際存在的部門名稱做精確比對（下拉選單也是從同一份資料算出來的）
            if (!string.IsNullOrEmpty(department))
            {
                query = query.Where(j => j.Department == department);
            }

            // 上架狀態（HR 專用）
            if (status == "active") query = query.Where(j => j.IsActive);
            else if (status == "inactive") query = query.Where(j => !j.IsActive);

            // 日期：篩選「該日期落在上架日期～截止日期之間」的職缺
            if (!string.IsNullOrEmpty(date) && DateTime.TryParse(date, out var selectedDate))
            {
                var d = selectedDate.Date;
                query = query.Where(j => j.CreatedAt.Date <= d && j.Deadline.Date >= d);
            }

            // 職缺名稱關鍵字（模糊搜尋，%keyword%）
            if (!string.IsNullOrEmpty(keyword))
            {
                query = query.Where(j => j.Title.Contains(keyword));
            }

            // 1. 依篩選條件撈出職缺，依截止日期由近到遠排序：越接近到期日的職缺排在越前面
            var jobs = await query
                .OrderBy(j => j.Deadline)
                .ToListAsync();

            // 2. ✨ 高效能動態統計：精確分類「待審核」與「錄取」
            var statsDict = new Dictionary<int, JobStatsViewModel>();

            foreach (var job in jobs)
            {
                // 撈出該職缺的所有履歷狀態列表
                var statuses = await _db.Resumes
                    .Where(r => r.JobsId == job.Id)
                    .Select(r => r.Status)
                    .ToListAsync();

                statsDict[job.Id] = new JobStatsViewModel
                {
                    UnhandledCount = statuses.Count(s => s == "待審核"), // 🎯 確保對應資料庫的「待審核」
                    HiredCount = statuses.Count(s => s == "錄取"),       // 🎯 為未來的「錄取」狀態做準備
                    TotalCount = statuses.Count
                };
            }

            // 將統計資料透過 ViewBag 傳遞給前端 View
            ViewBag.JobStats = statsDict;

            // 🎯 部門下拉選單：從 Jobs 表撈出目前實際存在的部門，每種只出現一次
            ViewBag.DepartmentOptions = await _db.Jobs
                .Where(j => !string.IsNullOrEmpty(j.Department))
                .Select(j => j.Department)
                .Distinct()
                .OrderBy(d => d)
                .ToListAsync();

            // 把目前的篩選條件存起來，讓前端可以「保留選取狀態」（跟 Job_search 一致）
            ViewBag.SelectedDepartment = department;
            ViewBag.SelectedStatus = status;
            ViewBag.SelectedDate = date;
            ViewBag.Keyword = keyword;

            return View("~/Views/Job_hr/Index.cshtml", jobs);
        }

        // GET: 新增職缺
        public async Task<IActionResult> Create()
        {
            if (!IsEmployee()) return RedirectToAction("Index", "Login");

            // 🎯 給「部門主管」下拉選單用（只列出 Role 為 director 的員工）
            ViewBag.Employees = await _db.Employees
                .Where(e => e.Role == "director")
                .OrderBy(e => e.Name)
                .ToListAsync();

            return View("~/Views/Job_hr/Create.cshtml");
        }

        // POST: 新增職缺
        [HttpPost]
        public async Task<IActionResult> Create(Job job, List<string>? MajorRequiredList, List<string>? LanguageList, List<string>? DegreeList, List<string>? SkillTagsList)
        {
            if (!IsEmployee()) return RedirectToAction("Index", "Login");

            // 🎯 選填欄位若留空，套用預設值（與前端 JS 邏輯一致）
            //    即使前端驗證被繞過（例如停用 JS），後端也不會因空值出錯或存入不一致的資料
            NormalizeOptionalDefaults(job);

            // 🎯 新增的職缺一律視為上架中：表單已經拿掉「立即上架」勾選框，
            //    這裡明確寫死 true，不依賴 model binding 對未送出欄位的預設值行為。
            //    必須放在 GetMissingRequiredFields 之前，該檢查才能正確套用「上架中必須晚於今天」的規則。
            job.IsActive = true;

            // 🎯 後端防呆：即使前端驗證被繞過（JS 出錯、被停用、或有人直接打 API），
            //    必填欄位缺漏也只會友善地退回表單重填，不會讓資料庫噴出未處理例外
            var missingFields = GetMissingRequiredFields(job);
            if (missingFields.Count > 0)
            {
                TempData["Error"] = "請完成以下必填欄位：" + string.Join("、", missingFields);
                ViewBag.Employees = await _db.Employees
                    .Where(e => e.Role == "director")
                    .OrderBy(e => e.Name)
                    .ToListAsync();
                return View("~/Views/Job_hr/Create.cshtml");
            }

            // 使用 DateTime.Now 寫入本地時間
            job.CreatedAt = DateTime.Now;
            job.UpdatedAt = DateTime.Now;

            _db.Jobs.Add(job);
            await _db.SaveChangesAsync(); // 先存 Job，才能拿到 job.Id 給子表用

            AddChildRecords(job.Id, MajorRequiredList, LanguageList, DegreeList, SkillTagsList);
            await _db.SaveChangesAsync();

            TempData["Success"] = "職缺已新增";
            return RedirectToAction("Index");
        }

        // GET: 修改職缺
        public async Task<IActionResult> Edit(int id)
        {
            if (!IsEmployee()) return RedirectToAction("Index", "Login");

            // 🎯 帶出正規化後的三張子表，讓表單能還原原本的資料
            var job = await _db.Jobs
                .Include(j => j.MajorRequirements)
                .Include(j => j.LanguageRequirements)
                .Include(j => j.SkillTags)
                .FirstOrDefaultAsync(j => j.Id == id);

            if (job == null) return NotFound();

            // 🎯 給「部門主管」下拉選單用（只列出 Role 為 director 的員工）
            ViewBag.Employees = await _db.Employees
                .Where(e => e.Role == "director")
                .OrderBy(e => e.Name)
                .ToListAsync();

            return View("~/Views/Job_hr/Edit.cshtml", job);
        }

        // POST: 修改職缺
        [HttpPost]
        public async Task<IActionResult> Edit(Job job, List<string>? MajorRequiredList, List<string>? LanguageList, List<string>? DegreeList, List<string>? SkillTagsList)
        {
            if (!IsEmployee()) return RedirectToAction("Index", "Login");

            var existing = await _db.Jobs
               .Include(j => j.MajorRequirements)
               .Include(j => j.LanguageRequirements)
               .Include(j => j.SkillTags)
               .FirstOrDefaultAsync(j => j.Id == job.Id);

            if (existing == null) return NotFound();

            // 🎯 記住修改前的上架狀態，等等存檔後要判斷是否剛從「上架中」變成「已下架」
            var wasActive = existing.IsActive;

            // 🎯 選填欄位若留空，套用預設值（與 Create 一致）
            NormalizeOptionalDefaults(job);

            var missingFields = GetMissingRequiredFields(job);

            // 🎯 當天上架的職缺不能當天下架，要隔天才能操作：擋下這次操作，維持原本上架中的狀態
            if (wasActive && !job.IsActive && existing.CreatedAt.Date == DateTime.Now.Date)
            {
                missingFields.Add("上架狀態（當天上架的職缺不能當天下架，請隔天再操作）");
                job.IsActive = true;
            }
            // 🎯 資料一致性防呆（跟前端 onIsActiveChange() 邏輯一致）：
            //    已下架的職缺，截止日期不應該還停留在未來，避免「已下架但還沒到截止日」的矛盾狀態。
            //    拉回的目標日期不能早於或等於上架日期（理論上走到這裡時已經不會是當天上架又下架的情況了）。
            //    重新勾選上架不會自動延長截止日期，那個需要人工決定新的日期。
            else if (!job.IsActive && job.Deadline.Date > DateTime.Now.Date)
            {
                var target = DateTime.Now.Date;
                if (target <= existing.CreatedAt.Date)
                {
                    target = existing.CreatedAt.Date.AddDays(1);
                }
                job.Deadline = target;
            }

            // 🎯 不管上架或下架都要成立的鐵律：截止日期不能早於或等於上架日期（CreatedAt）
            //    這裡用 existing.CreatedAt，因為表單沒有送出 CreatedAt，job.CreatedAt 綁定出來的值不可信
            if (job.Deadline.Date <= existing.CreatedAt.Date)
            {
                missingFields.Add("截止日期（不能早於或等於上架日期）");
            }

            // 🎯 後端防呆：即使前端驗證被繞過，必填欄位缺漏也只會友善地退回表單重填，
            //    不會讓資料庫噴出未處理例外。先檢查使用者這次送出的資料，尚未覆蓋 existing。
            if (missingFields.Count > 0)
            {
                TempData["Error"] = "請完成以下必填欄位：" + string.Join("、", missingFields);
                ViewBag.Employees = await _db.Employees
                    .Where(e => e.Role == "director")
                    .OrderBy(e => e.Name)
                    .ToListAsync();

                // 把使用者這次輸入的內容帶回表單（除了子表，子表維持資料庫原本的內容）
                existing.Title = job.Title;
                existing.Department = job.Department;
                existing.Location = job.Location;
                existing.JobType = job.JobType;
                existing.WorkShift = job.WorkShift;
                existing.LeavePolicy = job.LeavePolicy;
                existing.HeadCount = job.HeadCount;
                existing.Description = job.Description;
                existing.ExperienceRequired = job.ExperienceRequired;
                existing.EducationRequired = job.EducationRequired;
                existing.IndustryExperience = job.IndustryExperience;
                existing.CertRequired = job.CertRequired;
                existing.OtherRequirements = job.OtherRequirements;
                existing.SalaryMin = job.SalaryMin;
                existing.SalaryMax = job.SalaryMax;
                existing.EmployeesName = job.EmployeesName;
                existing.Deadline = job.Deadline;
                existing.IsActive = job.IsActive;

                return View("~/Views/Job_hr/Edit.cshtml", existing);
            }

            existing.Title = job.Title;
            existing.Department = job.Department;
            existing.Location = job.Location;
            existing.JobType = job.JobType;
            existing.WorkShift = job.WorkShift;
            existing.LeavePolicy = job.LeavePolicy;
            existing.HeadCount = job.HeadCount;
            existing.Description = job.Description;
            existing.ExperienceRequired = job.ExperienceRequired;
            existing.EducationRequired = job.EducationRequired;
            existing.IndustryExperience = job.IndustryExperience;
            existing.CertRequired = job.CertRequired;
            existing.OtherRequirements = job.OtherRequirements;
            existing.SkillTags = job.SkillTags;
            existing.SalaryMin = job.SalaryMin;
            existing.SalaryMax = job.SalaryMax;
            existing.EmployeesName = job.EmployeesName;   // 🎯 部門主管改成外鍵（指向 Employees.Name）
            existing.Deadline = job.Deadline;
            existing.IsActive = job.IsActive;

            // 使用 DateTime.Now 更新時間
            existing.UpdatedAt = DateTime.Now;

            // 🎯 正規化子表：簡單作法 = 全部刪掉、依表單重新寫入
            //    （職缺條件筆數不多，這種做法比逐筆比對新增/刪除簡單很多，也不容易出錯）
            _db.MajorRequired.RemoveRange(existing.MajorRequirements);
            _db.LanguageRequired.RemoveRange(existing.LanguageRequirements);
            _db.SkillTags.RemoveRange(existing.SkillTags);

            AddChildRecords(existing.Id, MajorRequiredList, LanguageList, DegreeList, SkillTagsList);

            await _db.SaveChangesAsync();

            // 🎯 狀態切換現在只能透過這個編輯頁面進行（列表頁的點擊切換已移除），
            //    所以「從上架中變成已下架」時，原本掛在 ToggleActive 上的自動安排面試也要搬過來，
            //    否則這個副作用就再也不會被觸發
            if (wasActive && !existing.IsActive)
            {
                await _scheduler.TryAutoScheduleAsync(existing.Id);
            }

            TempData["Success"] = "職缺已更新";
            return RedirectToAction("Index");
        }

        // POST: 刪除職缺
        [HttpPost]
        public async Task<IActionResult> Delete(int id)
        {
            if (!IsEmployee()) return RedirectToAction("Index", "Login");

            var job = await _db.Jobs.FindAsync(id);
            if (job == null) return NotFound();

            _db.Jobs.Remove(job);
            await _db.SaveChangesAsync();

            TempData["Success"] = "職缺已刪除";
            return RedirectToAction("Index");
        }

        // POST: 切換上下架
        [HttpPost]
        public async Task<IActionResult> ToggleActive(int id)
        {
            if (!IsEmployee()) return RedirectToAction("Index", "Login");

            var job = await _db.Jobs.FindAsync(id);
            if (job == null) return NotFound();

            job.IsActive = !job.IsActive;

            // 使用 DateTime.Now 更新時間
            job.UpdatedAt = DateTime.Now;
            await _db.SaveChangesAsync();

            // 🎯 Step B：若這次是下架，順便檢查是否能自動安排面試
            if (!job.IsActive)
            {
                await _scheduler.TryAutoScheduleAsync(job.Id);
            }

            return RedirectToAction("Index");
        }

        // 🎯 後端必填欄位檢查（與前端 validateJobForm() 規則一致）
        //    薪資因為是 int，「留空」在模型繫結後已無法區分（會變成 0），交由前端把關即可，
        //    這裡專注在字串／日期類欄位，避免真正會導致資料庫噴例外的空值
        private List<string> GetMissingRequiredFields(Job job)
        {
            var missing = new List<string>();

            if (string.IsNullOrWhiteSpace(job.Title)) missing.Add("職缺名稱");
            if (string.IsNullOrWhiteSpace(job.Department)) missing.Add("職缺類別（部門）");
            if (string.IsNullOrWhiteSpace(job.Location)) missing.Add("工作地點");
            if (string.IsNullOrWhiteSpace(job.Description)) missing.Add("職缺描述");
            if (string.IsNullOrWhiteSpace(job.EmployeesName)) missing.Add("最高主管");
            if (job.Deadline == default) missing.Add("截止日期");

            return missing;
        }

        // 🎯 選填欄位留空時套用預設值，確保資料一致並避免因空值造成錯誤
        //    （Create / Edit 共用，規則需與 Create.cshtml / Edit.cshtml 的前端 JS 保持一致）
        //    薪資範圍為必填欄位，這裡僅做負數防呆，不做「留空補預設值」的處理
        private void NormalizeOptionalDefaults(Job job)
        {
            if (string.IsNullOrWhiteSpace(job.HeadCount)) job.HeadCount = "不限";
            if (string.IsNullOrWhiteSpace(job.WorkShift)) job.WorkShift = "day";
            if (string.IsNullOrWhiteSpace(job.LeavePolicy)) job.LeavePolicy = "twodays";
            if (job.SalaryMin < 0) job.SalaryMin = 0;
            if (job.SalaryMax < 0) job.SalaryMax = 0;
            if (job.Deadline == default) job.Deadline = DateTime.UtcNow.AddDays(30);

            if (string.IsNullOrWhiteSpace(job.ExperienceRequired)) job.ExperienceRequired = "不限";
            if (string.IsNullOrWhiteSpace(job.EducationRequired)) job.EducationRequired = "不限";
            if (string.IsNullOrWhiteSpace(job.IndustryExperience)) job.IndustryExperience = "不限";
            if (string.IsNullOrWhiteSpace(job.CertRequired)) job.CertRequired = "不限";
            if (string.IsNullOrWhiteSpace(job.OtherRequirements)) job.OtherRequirements = "不限";
        }

        // 🎯 共用小工具：把表單送來的清單轉成子表 Entity 並加入 DbContext
        //    （Create/Edit 都會用到，避免重複程式碼）
        private void AddChildRecords(int jobId, List<string>? majorList, List<string>? languageList, List<string>? degreeList, List<string>? skillTagsList)
        {
            // 科系需求：一筆一個值，過濾空白
            if (majorList != null)
            {
                foreach (var major in majorList.Where(m => !string.IsNullOrWhiteSpace(m)))
                {
                    _db.MajorRequired.Add(new MajorRequired { JobsId = jobId, Major = major.Trim() });
                }
            }

            // 語文條件：Language / Degree 兩個平行陣列，用索引配對
            if (languageList != null && degreeList != null)
            {
                var count = Math.Min(languageList.Count, degreeList.Count);
                for (int i = 0; i < count; i++)
                {
                    if (string.IsNullOrWhiteSpace(languageList[i])) continue;

                    _db.LanguageRequired.Add(new LanguageRequired
                    {
                        JobsId = jobId,
                        Language = languageList[i].Trim(),
                        Degree = string.IsNullOrWhiteSpace(degreeList[i]) ? "不限" : degreeList[i].Trim()
                    });
                }
            }

            // 技能標籤：一筆一個標籤，過濾空白
            if (skillTagsList != null)
            {
                foreach (var tag in skillTagsList.Where(t => !string.IsNullOrWhiteSpace(t)))
                {
                    _db.SkillTags.Add(new SkillTag { JobsId = jobId, Tag = tag.Trim() });
                }
            }
        }
    }

    // 💡 用於前端綁定的強型別統計模型
    public class JobStatsViewModel
    {
        public int UnhandledCount { get; set; }
        public int HiredCount { get; set; }
        public int TotalCount { get; set; }
    }
}