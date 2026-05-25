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

            // 1. 先撈出所有職缺資訊與建立者員工關聯
            var jobs = await _db.Jobs
                .Include(j => j.Employee)
                .OrderByDescending(j => j.CreatedAt)
                .ToListAsync();

            // 2. ✨ 動態統計：根據 Resume 資料表計算每個職缺的應徵人數 (新/總)
            foreach (var job in jobs)
            {
                // 🎯 核心修正：r.Position 已經是 int，必須與同為 int 的 job.Id 做對比，解決 CS0019 報錯
                // 總應徵人數：計算 Resume 的 Position 等於目前職缺 Id 的總數量
                job.TotalApplicationsCount = await _db.Resumes
                    .CountAsync(r => r.Position == job.Id);

                // 新應徵人數：計算 Position 符合，且 Status 狀態為 "待審核" 的數量
                job.NewApplicationsCount = await _db.Resumes
                    .CountAsync(r => r.Position == job.Id && r.Status == "待審核");
            }

            return View("~/Views/Job_hr/Index.cshtml", jobs);
        }

        // GET: 新增職缺
        public IActionResult Create()
        {
            if (!IsEmployee()) return RedirectToAction("Index", "Login");
            return View("~/Views/Job_hr/Create.cshtml");
        }

        // POST: 新增職缺
        [HttpPost]
        public async Task<IActionResult> Create(Job job)
        {
            if (!IsEmployee()) return RedirectToAction("Index", "Login");

            job.CreatedBy = HttpContext.Session.GetInt32("MemberId") ?? 0;

            // 使用 DateTime.Now 寫入本地時間
            job.CreatedAt = DateTime.Now;
            job.UpdatedAt = DateTime.Now;

            _db.Jobs.Add(job);
            await _db.SaveChangesAsync();

            TempData["Success"] = "職缺已新增";
            return RedirectToAction("Index");
        }

        // GET: 修改職缺
        public async Task<IActionResult> Edit(int id)
        {
            if (!IsEmployee()) return RedirectToAction("Index", "Login");

            var job = await _db.Jobs.FindAsync(id);
            if (job == null) return NotFound();

            return View("~/Views/Job_hr/Edit.cshtml", job);
        }

        // POST: 修改職缺
        [HttpPost]
        public async Task<IActionResult> Edit(Job job)
        {
            if (!IsEmployee()) return RedirectToAction("Index", "Login");

            var existing = await _db.Jobs.FindAsync(job.Id);
            if (existing == null) return NotFound();

            existing.Title = job.Title;
            existing.Department = job.Department;
            existing.Location = job.Location;
            existing.JobType = job.JobType;
            existing.WorkShift = job.WorkShift;
            existing.LeavePolicy = job.LeavePolicy;
            existing.HeadCount = job.HeadCount;
            existing.Description = job.Description;
            existing.Requirements = job.Requirements;
            existing.ExperienceRequired = job.ExperienceRequired;
            existing.EducationRequired = job.EducationRequired;
            existing.IndustryExperience = job.IndustryExperience;
            existing.MajorRequired = job.MajorRequired;
            existing.LanguageRequired = job.LanguageRequired;
            existing.CertRequired = job.CertRequired;
            existing.OtherRequirements = job.OtherRequirements;
            existing.SkillTags = job.SkillTags;
            existing.SalaryMin = job.SalaryMin;
            existing.SalaryMax = job.SalaryMax;
            existing.ManagerName = job.ManagerName;
            existing.ReportToName = job.ReportToName;
            existing.Deadline = job.Deadline;
            existing.IsActive = job.IsActive;

            // 使用 DateTime.Now 更新時間
            existing.UpdatedAt = DateTime.Now;

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
    }
}