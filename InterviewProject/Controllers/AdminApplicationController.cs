using InterviewProject.Data;
using InterviewProject.Models;
using Microsoft.AspNetCore.Mvc;
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

            // 🎯 這裡同步把 AiScore 與 AiComment 從資料庫 Resume 表內撈出來
            // 🎯 修正後的 LINQ 查詢區塊
            var query = from r in _db.Resumes
                        join m in _db.Members on r.MembersId equals m.Id
                        where r.JobsId == jobId
                        select new InterviewProject.Models.Resume  // 👈 這裡明確指定專案的 Model
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
                            AiScore = r.AiScore,          // 👈 這樣編譯器就能正確認到了
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
                // 🎯 優先使用資料庫內存的 Gemini 真實數據，如果沒有才給予預設值
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

            // 🎯 【關鍵修正】：手動去關聯子資料表把資料撈出來
            var dblangs = await _db.LanguageProficiency.Where(l => l.ResumeId == resume.Id).ToListAsync();
            var dbLicenses = await _db.DriverLicense.Where(d => d.ResumeId == resume.Id).ToListAsync();
            var dbCompSkills = await _db.ComputerSkills.Where(s => s.ResumeId == resume.Id).ToListAsync();
            var dbSpecs = await _db.Specialties.Where(s => s.ResumeId == resume.Id).OrderBy(s => s.SortOrder).ToListAsync();

            // 🎯 將子資料表集合重新壓製回前端 JavaScript 需要解析的 [NotMapped] 長字串中
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

        // ─── 貼心保留：字串格式轉換家族方法 ───
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
            // 依排序撈出 Specialty，並用分號與空格串接 "; "
            return string.Join("; ", specs.OrderBy(s => s.SortOrder).Select(s => s.Specialty));
        }
    }
}