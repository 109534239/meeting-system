using InterviewProject.Data;
using InterviewProject.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http; // 確保有引入 Session 所需的命名空間

namespace InterviewProject.Controllers
{
    public class AdminApplicationController : Controller
    {
        private readonly AppDbContext _db;

        public AdminApplicationController(AppDbContext db)
        {
            _db = db;
        }

        private bool IsEmployee()
        {
            var role = HttpContext.Session.GetString("MemberRole")?.ToLower();
            return role == "hr" || role == "manager" || role == "employee";
        }

        // 1. 職缺的應徵履歷名單列表頁
        // GET: AdminApplication/Index?jobId=5&statusFilter=全部
        public async Task<IActionResult> Index(int jobId, string statusFilter = "全部")
        {
            if (!IsEmployee()) return RedirectToAction("Index", "Login");

            var job = await _db.Jobs.FindAsync(jobId);
            if (job == null) return NotFound();

            ViewBag.JobTitle = job.Title;
            ViewBag.JobId = jobId;
            ViewBag.CurrentFilter = statusFilter;

            // 🎯 同步把 AiScore 與 AiComment 從資料庫 Resume 表內撈出來
            var query = from r in _db.Resumes
                        join m in _db.Members on r.MembersId equals m.Id
                        where r.JobsId == jobId
                        select new InterviewProject.Models.Resume
                        {
                            Id = r.Id,
                            ResumeTime = r.ResumeTime,
                            Phone2 = m.Name,
                            Mobile = m.Phone,
                            SchoolName = r.SchoolName,
                            Major = r.Major,
                            EduLevel = r.EduLevel,
                            WorkExperienceYears = r.WorkExperienceYears,
                            CompanyName = r.CompanyName,
                            JobTitle = r.JobTitle,
                            Status = r.Status,
                            JobsId = r.JobsId,
                            AiScore = r.AiScore,
                            AiComment = r.AiComment
                        };

            if (statusFilter == "未處理")
            {
                query = query.Where(x => x.Status == "待審核");
            }
            else if (statusFilter == "已處理")
            {
                query = query.Where(x => x.Status != "待審核");
            }

            var resumesList = await query.OrderByDescending(x => x.ResumeTime).ToListAsync();

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
                .FirstOrDefaultAsync(r => r.Id == id);

            if (resume == null) return NotFound();

            var dblangs = await _db.LanguageProficiency.Where(l => l.ResumeId == resume.Id).ToListAsync();
            var dbLicenses = await _db.DriverLicense.Where(d => d.ResumeId == resume.Id).ToListAsync();
            var dbCompSkills = await _db.ComputerSkills.Where(s => s.ResumeId == resume.Id).ToListAsync();
            var dbSpecs = await _db.Specialties.Where(s => s.ResumeId == resume.Id).OrderBy(s => s.SortOrder).ToListAsync();

            resume.LanguageSkills = FormatLanguageString(dblangs);
            resume.DriverLicense = FormatDriverLicenseString(dbLicenses);
            resume.ComputerSkills = FormatComputerSkillString(dbCompSkills);
            resume.Specialty = FormatSpecialtyString(dbSpecs);

            var member = await _db.Members.FindAsync(resume.MembersId);
            if (member != null)
            {
                ViewBag.UserName = member.Name;
                ViewBag.UserGender = member.Gender;
                ViewBag.UserIdNumber = member.IdNumber;
                ViewBag.UserBirthday = member.Birthday.ToString("yyyy/MM/dd");
                ViewBag.UserAddress = member.Address;
                ViewBag.UserEmail = member.Email;
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
                var resume = await _db.Resumes.FindAsync(model.Id);
                if (resume == null)
                {
                    return Json(new { success = false, message = "找不到對應的履歷紀錄。" });
                }

                // 修改狀態並更新資料庫
                resume.Status = model.Status;
                _db.Entry(resume).State = EntityState.Modified;
                await _db.SaveChangesAsync();

                return Json(new { success = true, message = "履歷狀態已成功存入資料庫。" });
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
    }

    // 🎯 專門用來安全對接 JSON Body 參數的資料模型DTO 
    public class StatusUpdateModel
    {
        public int Id { get; set; }
        public string Status { get; set; }
    }
}