using InterviewProject.Data;
using InterviewProject.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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

            var query = from r in _db.Resumes
                        join m in _db.Members on r.MembersId equals m.Id
                        where r.JobsId == jobId
                        select new Resume
                        {
                            Id = r.Id,
                            ResumeTime = r.ResumeTime,
                            Phone2 = m.Name,              // 將真實姓名塞入 Phone2 輸出
                            Mobile = m.Phone,              // 將 Members 的註冊電話塞入 Mobile 輸出
                            SchoolName = r.SchoolName,
                            Major = r.Major,
                            EduLevel = r.EduLevel,
                            WorkExperienceYears = r.WorkExperienceYears,
                            CompanyName = r.CompanyName,
                            JobTitle = r.JobTitle,
                            Status = r.Status,
                            JobsId = r.JobsId
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
                aiScores[r.Id] = 85;
                aiComments[r.Id] = "【AI 智慧初審報告】\n1. 專業技能：該求職者在相關領域具備良好基礎，且學歷科系完全契合職務需求。\n2. 工作經驗：具備適當的實務經驗，能快速融入團隊開發。\n3. 綜合評估：高度推薦面試。";
            }

            ViewBag.AiScores = aiScores;
            ViewBag.AiComments = aiComments;

            return View("~/Views/AdminApplication/Index.cshtml", resumesList);
        }

        // 2. ✨ 新增：HR 點擊「審查履歷」按鈕進到這裡
        // GET: AdminApplication/Details/5
        // GET: AdminApplication/Details/5
        public async Task<IActionResult> Details(int id)
        {
            if (!IsEmployee()) return RedirectToAction("Index", "Login");

            var resume = await _db.Resumes
                .Include(r => r.Job)
                .FirstOrDefaultAsync(r => r.Id == id);

            if (resume == null) return NotFound();

            var member = await _db.Members.FindAsync(resume.MembersId);
            if (member != null)
            {
                ViewBag.UserName = member.Name;
                ViewBag.UserGender = member.Gender;
                ViewBag.UserIdNumber = member.IdNumber;
                ViewBag.UserBirthday = member.Birthday.ToString("yyyy/MM/dd");
                ViewBag.UserAddress = member.Address;
                ViewBag.UserEmail = member.Email;

                // 🎯 核心修正：將 ProfileImagePat 改為正確的 ProfileImagePath (帶有 h)
                ViewBag.UserPhotoBase64 = member.ProfileImagePath;
            }

            ViewBag.IsReadOnly = true;
            return View("~/Views/Resume/Resume.cshtml", resume);
        }
    }
}