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
            var role = HttpContext.Session.GetString("MemberRole");
            return role == "employee";
        }

        // GET: 職缺列表
        public async Task<IActionResult> Index()
        {
            if (!IsEmployee()) return RedirectToAction("Index", "Login");

            var jobs = await _db.Jobs
                .Include(j => j.Creator)
                .OrderByDescending(j => j.CreatedAt)
                .ToListAsync();

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
            job.CreatedAt = DateTime.UtcNow;
            job.UpdatedAt = DateTime.UtcNow;

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
            existing.Description = job.Description;
            existing.Requirements = job.Requirements;
            existing.Salary = job.Salary;
            existing.IsActive = job.IsActive;
            existing.UpdatedAt = DateTime.UtcNow;

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
            job.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            return RedirectToAction("Index");
        }
    }
}