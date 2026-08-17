using InterviewProject.Data;
using InterviewProject.Models;
using InterviewProject.Services;
using Microsoft.AspNetCore.Http; // 確保有引入 Session 所需的命名空間
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace InterviewProject.Controllers
{
    public class AdminApplicationController : Controller
    {
        private readonly AppDbContext _db;
        private readonly AutoInterviewSchedulingService _scheduler;

        public AdminApplicationController(AppDbContext db, AutoInterviewSchedulingService scheduler)
        {
            _db = db;
            _scheduler = scheduler;
        }

        private bool IsEmployee()
        {
            var role = HttpContext.Session.GetString("MemberRole")?.ToLower();
            return role == "hr" || role == "manager" || role == "director" || role == "employee";
        }

        // 🎯 取得目前登入者角色
        private string GetCurrentRole()
        {
            return HttpContext.Session.GetString("MemberRole")?.ToLower() ?? "";
        }

        // 🎯 取得目前登入者所屬部門
        private async Task<string?> GetCurrentEmployeeDepartment()
        {
            var memberId = HttpContext.Session.GetInt32("MemberId");

            if (!memberId.HasValue)
            {
                return null;
            }

            var employee = await _db.Employees.FirstOrDefaultAsync(e => e.Id == memberId.Value);

            return employee?.Department;
        }

        // 🎯 判斷目前使用者是否有權限查看該職缺
        private async Task<bool> CanAccessJob(Job job)
        {
            var role = GetCurrentRole();

            // HR 可以查看全部職缺與全部應徵情況
            if (role == "hr")
            {
                return true;
            }

            // Manager / Director 只能查看自己所屬部門的應徵情況
            if (role == "manager" || role == "director" || role == "employee")
            {
                var department = await GetCurrentEmployeeDepartment();

                if (string.IsNullOrEmpty(department))
                {
                    return false;
                }

                return job.Department == department;
            }

            return false;
        }

        // 1. 職缺的應徵履歷名單列表頁
        // GET: AdminApplication/Index?jobId=5&statusFilter=全部
        public async Task<IActionResult> Index(int? jobId, string statusFilter = "全部")
        {
            if (!IsEmployee()) return RedirectToAction("Index", "Login");

            var role = GetCurrentRole();
            var department = await GetCurrentEmployeeDepartment();

            ViewBag.JobId = jobId;
            ViewBag.CurrentFilter = statusFilter;

            // 🎯 1. 撈出選單用的職缺清單 (依照角色權限過濾)
            var jobsQuery = _db.Jobs.AsQueryable();
            if (role == "manager" || role == "director" || role == "employee")
            {
                if (string.IsNullOrEmpty(department)) return Forbid();
                jobsQuery = jobsQuery.Where(j => j.Department == department);
            }

            var jobList = await jobsQuery
                .OrderByDescending(j => j.CreatedAt)
                .Select(j => new SelectListItem
                {
                    Value = j.Id.ToString(),
                    Text = j.Title,
                    Selected = jobId.HasValue && j.Id == jobId.Value
                })
                .ToListAsync();

            ViewBag.JobList = jobList;

            // 🎯 2. 設定頁面標題
            if (jobId.HasValue)
            {
                var job = await _db.Jobs.FindAsync(jobId.Value);
                if (job == null) return NotFound();

                // 🎯 Manager / Director 只能查看自己部門的職缺
                if (!await CanAccessJob(job))
                {
                    return Forbid();
                }

                ViewBag.JobTitle = job.Title;
            }
            else
            {
                // 🎯 如果沒有帶 jobId，HR 看全部；Manager / Director 看自己部門
                ViewBag.JobTitle = role == "hr"
                    ? "全部應徵履歷"
                    : $"{department} 部門應徵履歷";
            }

            // 🎯 3. 最上游 LINQ：排除 Status == "暫存" 的履歷
            var query = from r in _db.Resumes
                        join m in _db.Members on r.MembersId equals m.Id
                        join j in _db.Jobs on r.JobsId equals j.Id
                        where (!jobId.HasValue || r.JobsId == jobId.Value) && r.Status != "暫存"
                        select new
                        {
                            Resume = r,
                            Member = m,
                            Job = j
                        };

            // 🎯 Manager / Director 只能看到自己所屬部門的履歷
            if (role == "manager" || role == "director" || role == "employee")
            {
                query = query.Where(x => x.Job.Department == department);
            }

            // 🎯 4. 狀態篩選 (未處理 / 已處理)
            if (statusFilter == "未處理")
            {
                query = query.Where(x => x.Resume.Status == "待審核");
            }
            else if (statusFilter == "已處理")
            {
                query = query.Where(x => x.Resume.Status != "待審核");
            }

            // 🎯 5. 一次性查出所需完整資料列（避免多餘的資料庫來回查詢）
            var rawList = await query
                .OrderByDescending(x => x.Resume.ResumeTime)
                .ToListAsync();

            // 🎯 6. 記憶體中整理字典 (效能最佳，只需 1 次 SQL 查詢)
            ViewBag.MemberNames = rawList.ToDictionary(x => x.Resume.Id, x => x.Member.Name);
            ViewBag.MemberPhones = rawList.ToDictionary(x => x.Resume.Id, x => x.Member.Phone);
            ViewBag.JobTitles = rawList.ToDictionary(x => x.Resume.Id, x => x.Job.Title);

            // 🎯 7. 組成主模型 ResumesList
            var resumesList = rawList.Select(x => new InterviewProject.Models.Resume
            {
                Id = x.Resume.Id,
                ResumeTime = x.Resume.ResumeTime,
                WorkExperienceYears = x.Resume.WorkExperienceYears,
                Status = x.Resume.Status,
                JobsId = x.Resume.JobsId,
                AiScore = x.Resume.AiScore,
                AiComment = x.Resume.AiComment
            }).ToList();

            // 🎯 8. 學歷、工作經歷子表查出並掛回 Resume 物件
            var resumeIds = resumesList.Select(r => r.Id).ToList();

            if (resumeIds.Any())
            {
                var allEducations = await _db.Educations
                    .Where(e => resumeIds.Contains(e.ResumeId))
                    .OrderBy(e => e.SortOrder)
                    .ToListAsync();
                var eduLookup = allEducations.GroupBy(e => e.ResumeId).ToDictionary(g => g.Key, g => g.ToList());

                var allWorkExperiences = await _db.WorkExperiences
                    .Where(w => resumeIds.Contains(w.ResumeId))
                    .OrderBy(w => w.SortOrder)
                    .ToListAsync();
                var workLookup = allWorkExperiences.GroupBy(w => w.ResumeId).ToDictionary(g => g.Key, g => g.ToList());

                foreach (var r in resumesList)
                {
                    if (eduLookup.TryGetValue(r.Id, out var edus))
                    {
                        r.Educations = edus;
                    }
                    if (workLookup.TryGetValue(r.Id, out var works))
                    {
                        r.WorkExperiences = works;
                    }
                }
            }

            // 🎯 9. AI 評分與評語字典組裝
            var aiScores = new Dictionary<int, int>();
            var aiComments = new Dictionary<int, string>();

            foreach (var r in resumesList)
            {
                aiScores[r.Id] = r.AiScore ?? 0;
                aiComments[r.Id] = !string.IsNullOrEmpty(r.AiComment) ? r.AiComment : "暫無初審評語。";
            }

            ViewBag.AiScores = aiScores;
            ViewBag.AiComments = aiComments;

            return View("~/Views/AdminApplication/Index.cshtml", resumesList);
        }

        // 2. HR 點擊整列或「審查履歷」按鈕進入細節頁面
        // GET: AdminApplication/Details/5
        public async Task<IActionResult> Details(int id)
        {
            if (!IsEmployee()) return RedirectToAction("Index", "Login");

            var resume = await _db.Resumes
                .Include(r => r.Job)
                 .Include(r => r.Educations) // 🎯 讀取詳細內容時要一併帶出學歷子表，不然唯讀畫面學歷區塊會是空的
                 .Include(r => r.WorkExperiences) // 🎯 同樣要帶出工作經歷子表
                 .Include(r => r.Portfolios) // 🎯 修正：補上作品集子表的 Include，不然唯讀畫面作品集區塊會是空的
                .FirstOrDefaultAsync(r => r.Id == id);

            if (resume == null) return NotFound();

            // 🎯 Manager / Director 只能查看自己部門的履歷詳細資料
            if (resume.Job != null && !await CanAccessJob(resume.Job))
            {
                return Forbid();
            }

            var dblangs = await _db.LanguageProficiency.Where(l => l.ResumeId == resume.Id).ToListAsync();
            var dbLicenses = await _db.DriverLicense.Where(d => d.ResumeId == resume.Id).ToListAsync();
            var dbCompSkills = await _db.ComputerSkills.Where(s => s.ResumeId == resume.Id).ToListAsync();
            var dbSpecs = await _db.Specialties.Where(s => s.ResumeId == resume.Id).OrderBy(s => s.SortOrder).ToListAsync();
            var dbCertificates = await _db.Certificates.Where(s => s.ResumeId == resume.Id).ToListAsync();

            resume.LanguageSkills = FormatLanguageString(dblangs);
            resume.DriverLicense = FormatDriverLicenseString(dbLicenses);
            resume.ComputerSkills = FormatComputerSkillString(dbCompSkills);
            resume.Specialty = FormatSpecialtyString(dbSpecs);
            resume.Certificates = FormatCertificatesString(dbCertificates);

            var member = await _db.Members.FindAsync(resume.MembersId);
            if (member != null)
            {
                ViewBag.UserName = member.Name;
                ViewBag.UserGender = member.Gender;
                ViewBag.UserIdNumber = member.IdNumber;
                ViewBag.UserBirthday = member.Birthday.ToString("yyyy/MM/dd");
                ViewBag.UserAddress = member.Address;
                ViewBag.UserEmail = member.Email;
                ViewBag.UserPhone = member.Phone; // 🎯 新增：跟 ResumeController 的 PopulateViewBagData 一致
                ViewBag.UserPhotoBase64 = member.ProfileImagePath;
            }

            ViewBag.IsReadOnly = true;
            return View("~/Views/Resume/Resume.cshtml", resume);
        }

        // 3. 🎯 新增：接收前端 AJAX 變更選單狀態，並儲存回資料庫
        // POST: AdminApplication/UpdateStatus
        [HttpPost]
        public async Task<IActionResult> UpdateStatus([FromBody] StatusUpdateModel model)
        {
            // 權限判定
            if (!IsEmployee())
            {
                return Json(new { success = false, message = "權限不足或登入已逾期。" });
            }

            // 參數內容防呆
            if (model == null || model.Id <= 0 || string.IsNullOrEmpty(model.Status))
            {
                return Json(new { success = false, message = "傳遞的參數欄位不正確。" });
            }

            // 驗證狀態是否屬於限制的三個合法值
            var validStatuses = new[] { "待審核", "已通過", "未通過" };
            if (!validStatuses.Contains(model.Status))
            {
                return Json(new { success = false, message = "傳入的不合法履歷狀態選項。" });
            }

            try
            {
                // 從資料庫找出該筆履歷紀錄
                var resume = await _db.Resumes
                    .Include(r => r.Job)
                    .FirstOrDefaultAsync(r => r.Id == model.Id);

                if (resume == null)
                {
                    return Json(new { success = false, message = "找不到對應的履歷紀錄。" });
                }

                // 🎯 Manager / Director 只能更新自己部門的履歷狀態
                if (resume.Job != null && !await CanAccessJob(resume.Job))
                {
                    return Json(new { success = false, message = "您沒有權限修改其他部門的履歷狀態。" });
                }

                // 修改狀態並更新資料庫
                resume.Status = model.Status;

                // 🎯 履歷審核就被刷掉的人，直接視為「未錄取」，並將面試狀態清空
                if (resume.Status == "未通過")
                {
                    resume.AdmissionResult = AdmissionResultValues.Rejected;
                    resume.InterviewStatus = null; // 👈 新增：清空面試狀態
                }

                _db.Entry(resume).State = EntityState.Modified;
                await _db.SaveChangesAsync();

                // 🎯 Step B：這筆履歷審完了，順便檢查這個職缺是否全部審核完畢、可以自動安排面試
                await _scheduler.TryAutoScheduleAsync(resume.JobsId);

                // 👈 建議在 JSON 回傳當前最新的 InterviewStatus，方便前端更新 UI
                return Json(new
                {
                    success = true,
                    message = "履歷狀態已成功存入資料庫。",
                    interviewStatus = resume.InterviewStatus
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "資料庫儲存失敗，錯誤原因：" + ex.Message });
            }
        }

        // ─── 字串格式轉換家族方法 ───
        private string FormatLanguageString(List<LanguageProficiency> langs)
        {
            if (langs == null || !langs.Any()) return "";
            return string.Join(", ", langs.Select(l =>
                l.Language == "不具外文能力" ? l.Language : $"{l.Language}({l.Degree})"));
        }

        private string FormatDriverLicenseString(List<DriverLicense> licenses)
        {
            if (licenses == null || !licenses.Any()) return "";
            var result = new List<string>();
            var grouped = licenses.Where(l => l.Driver != "汽(機)車").GroupBy(l => l.Driver);
            foreach (var g in grouped)
            {
                result.Add($"{g.Key}({string.Join("/", g.Select(x => x.Type))})");
            }
            var status = licenses.Where(l => l.Driver == "汽(機)車").Select(x => x.Type);
            if (status.Any())
            {
                result.Add(string.Join("/", status));
            }
            return string.Join(", ", result);
        }

        private string FormatComputerSkillString(List<ComputerSkills> skills)
        {
            if (skills == null || !skills.Any()) return "";
            return string.Join(", ", skills.Select(s => s.ComputerSkill));
        }

        private string FormatSpecialtyString(List<Specialties> specs)
        {
            if (specs == null || !specs.Any()) return "";
            return string.Join("; ", specs.OrderBy(s => s.SortOrder).Select(s => s.Specialty));
        }
        private string FormatCertificatesString(List<InterviewProject.Models.Certificates> dbCerts)
        {
            if (dbCerts == null || !dbCerts.Any()) return "";

            // 將每筆資料組合成 "證照名稱(級別)"，如果沒級別就只留 "證照名稱"
            var certStrings = dbCerts.Select(c =>
                !string.IsNullOrEmpty(c.Levels) ? $"{c.CName.Trim()}({c.Levels.Trim()})" : c.CName.Trim()
            );

            return string.Join(", ", certStrings);
        }
    }

    // 🎯 專門用來安全對接 JSON Body 參數的資料模型DTO 
    public class StatusUpdateModel
    {
        public int Id { get; set; }
        public string Status { get; set; } = "";
    }
}