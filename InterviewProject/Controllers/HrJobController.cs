using InterviewProject.Data;
using InterviewProject.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace InterviewProject.Controllers
{
    public class HrJobController : Controller
    {
        private readonly AppDbContext _db;

        public HrJobController(AppDbContext db)
        {
            _db = db;
        }

        // 權限檢查 helper
        private bool IsEmployee()
        {
            var role = HttpContext.Session.GetString("MemberRole")?.ToLower();
            return role == "hr" || role == "manager" || role == "employee";
        }

        // GET: 職缺列表
        public async Task<IActionResult> Index()
        {
            if (!IsEmployee()) return RedirectToAction("Index", "Login");

            // 1. 先撈出所有職缺資訊，並帶出建立者與主管的員工關聯
            var jobs = await _db.Jobs
                .Include(j => j.Manager)    // 🎯 部門主管 (原本的 ManagerName 改成關聯到 Employee)
                .OrderByDescending(j => j.CreatedAt)
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

            return RedirectToAction("Index");
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