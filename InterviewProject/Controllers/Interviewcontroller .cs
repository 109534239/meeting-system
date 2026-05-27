using InterviewProject.Data;
using InterviewProject.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace InterviewProject.Controllers
{
    public class InterviewController : Controller
    {
        private readonly AppDbContext _db;

        public InterviewController(AppDbContext db)
        {
            _db = db;
        }

        // ─────────────────────────────────────────────
        //  共用：權限判斷
        // ─────────────────────────────────────────────
        private bool IsEmployee()
        {
            var role = HttpContext.Session.GetString("MemberRole")?.ToLower();
            return role == "hr" || role == "manager" || role == "director";
        }

        private int GetEmployeeId() =>
            HttpContext.Session.GetInt32("MemberId") ?? 0;

        private int GetMemberId() =>
            HttpContext.Session.GetInt32("MemberId") ?? 0;

        // ─────────────────────────────────────────────
        //  【HR / 主管】面試排程管理列表
        // ─────────────────────────────────────────────
        public async Task<IActionResult> Index()
        {
            if (!IsEmployee()) return RedirectToAction("Index", "Login");

            var role = HttpContext.Session.GetString("MemberRole")?.ToLower();
            int empId = GetEmployeeId();

            var query = _db.InterviewSchedules
                .Include(s => s.Member)
                .Include(s => s.Job)
                .Include(s => s.Room)
                .Include(s => s.ScheduledByEmployee)
                .AsQueryable();

            if (role == "manager")
                query = query.Where(s => s.ScheduledByEmployeeId == empId);

            var schedules = await query
                .OrderByDescending(s => s.ScheduledAt)
                .ToListAsync();

            return View("~/Views/Interview/Index.cshtml", schedules);
        }

        // ─────────────────────────────────────────────
        //  【HR / 主管】新增排程 GET
        // ─────────────────────────────────────────────
        [HttpGet]
        public async Task<IActionResult> Create(int? resumeId)
        {
            if (!IsEmployee()) return RedirectToAction("Index", "Login");

            ViewBag.Rooms = await _db.Rooms.OrderByDescending(r => r.CreatedTime).ToListAsync();

            if (resumeId.HasValue)
            {
                // ✅ 修正：Resume 沒有 Member navigation，只 Include Job 即可
                var resume = await _db.Resumes
                    .Include(r => r.Job)
                    .FirstOrDefaultAsync(r => r.Id == resumeId);

                if (resume != null)
                {
                    var member = await _db.Members.FindAsync(resume.UserId);
                    ViewBag.PrefilledResume = resume;
                    ViewBag.PrefilledMember = member;
                }
            }

            var pendingResumes = await _db.Resumes
                .Include(r => r.Job)
                .Where(r => r.Status == "待審核" || r.Status == "書審通過")
                .OrderByDescending(r => r.ResumeTime)
                .ToListAsync();

            var memberIds = pendingResumes.Select(r => r.UserId).Distinct().ToList();
            var members = await _db.Members
                .Where(m => memberIds.Contains(m.Id))
                .ToDictionaryAsync(m => m.Id, m => m.Name);

            ViewBag.PendingResumes = pendingResumes;
            ViewBag.MemberNames = members;

            return View("~/Views/Interview/Create.cshtml");
        }

        // ─────────────────────────────────────────────
        //  【HR / 主管】新增排程 POST
        // ─────────────────────────────────────────────
        [HttpPost]
        public async Task<IActionResult> Create(InterviewSchedule schedule)
        {
            if (!IsEmployee()) return RedirectToAction("Index", "Login");

            var resume = await _db.Resumes.FindAsync(schedule.ResumeId);
            if (resume == null)
            {
                TempData["Error"] = "找不到對應的履歷";
                return RedirectToAction("Create");
            }

            schedule.MemberId = resume.UserId;
            schedule.JobId = resume.Position;
            schedule.ScheduledByEmployeeId = GetEmployeeId();
            schedule.CreatedAt = DateTime.Now;
            schedule.Status = "待確認";

            if (schedule.RoomId == null)
            {
                var newRoom = new Room
                {
                    RoomName = $"面試-{DateTime.Now:yyyyMMdd-HHmm}",
                    CreatedTime = DateTime.Now,
                    JitsiRoomName = Guid.NewGuid().ToString("N")[..10]
                };
                _db.Rooms.Add(newRoom);
                await _db.SaveChangesAsync();
                schedule.RoomId = newRoom.Id;
            }

            resume.Status = "已排面試";
            _db.InterviewSchedules.Add(schedule);
            await _db.SaveChangesAsync();

            TempData["Success"] = "面試已成功排程！";
            return RedirectToAction("Index");
        }

        // ─────────────────────────────────────────────
        //  【HR / 主管】編輯排程 GET
        // ─────────────────────────────────────────────
        [HttpGet]
        public async Task<IActionResult> Edit(int id)
        {
            if (!IsEmployee()) return RedirectToAction("Index", "Login");

            var schedule = await _db.InterviewSchedules
                .Include(s => s.Member)
                .Include(s => s.Job)
                .Include(s => s.Room)
                .FirstOrDefaultAsync(s => s.Id == id);

            if (schedule == null) return NotFound();

            ViewBag.Rooms = await _db.Rooms.OrderByDescending(r => r.CreatedTime).ToListAsync();
            return View("~/Views/Interview/Edit.cshtml", schedule);
        }

        // ─────────────────────────────────────────────
        //  【HR / 主管】編輯排程 POST
        // ─────────────────────────────────────────────
        [HttpPost]
        public async Task<IActionResult> Edit(InterviewSchedule model)
        {
            if (!IsEmployee()) return RedirectToAction("Index", "Login");

            var existing = await _db.InterviewSchedules.FindAsync(model.Id);
            if (existing == null) return NotFound();

            existing.ScheduledAt = model.ScheduledAt;
            existing.Notes = model.Notes;
            existing.Status = model.Status;
            existing.ResultNote = model.ResultNote;

            if (model.RoomId.HasValue)
                existing.RoomId = model.RoomId;

            await _db.SaveChangesAsync();

            TempData["Success"] = "排程已更新";
            return RedirectToAction("Index");
        }

        // ─────────────────────────────────────────────
        //  【HR / 主管】取消排程
        // ─────────────────────────────────────────────
        [HttpPost]
        public async Task<IActionResult> Cancel(int id)
        {
            if (!IsEmployee()) return RedirectToAction("Index", "Login");

            var schedule = await _db.InterviewSchedules.FindAsync(id);
            if (schedule == null) return NotFound();

            schedule.Status = "已取消";
            await _db.SaveChangesAsync();

            TempData["Success"] = "面試排程已取消";
            return RedirectToAction("Index");
        }

        // ─────────────────────────────────────────────
        //  【HR / 主管】標記完成
        // ─────────────────────────────────────────────
        [HttpPost]
        public async Task<IActionResult> Complete(int id, string? resultNote)
        {
            if (!IsEmployee()) return RedirectToAction("Index", "Login");

            var schedule = await _db.InterviewSchedules.FindAsync(id);
            if (schedule == null) return NotFound();

            schedule.Status = "已完成";
            schedule.ResultNote = resultNote;
            await _db.SaveChangesAsync();

            TempData["Success"] = "面試已標記為完成";
            return RedirectToAction("Index");
        }

        // ─────────────────────────────────────────────
        //  【求職者】查看自己的面試通知
        // ─────────────────────────────────────────────
        public async Task<IActionResult> MyInterviews()
        {
            int memberId = GetMemberId();
            if (memberId == 0) return RedirectToAction("Index", "Login");

            var schedules = await _db.InterviewSchedules
                .Include(s => s.Job)
                .Include(s => s.Room)
                .Include(s => s.ScheduledByEmployee)
                .Where(s => s.MemberId == memberId)
                .OrderByDescending(s => s.ScheduledAt)
                .ToListAsync();

            return View("~/Views/Interview/MyInterviews.cshtml", schedules);
        }

        // ─────────────────────────────────────────────
        //  【共用】AJAX：根據 resumeId 取得求職者資訊
        // ─────────────────────────────────────────────
        [HttpGet]
        public async Task<IActionResult> GetResumeInfo(int resumeId)
        {
            var resume = await _db.Resumes
                .Include(r => r.Job)
                .FirstOrDefaultAsync(r => r.Id == resumeId);

            if (resume == null)
                return Json(new { success = false });

            var member = await _db.Members.FindAsync(resume.UserId);

            return Json(new
            {
                success = true,
                memberName = member?.Name ?? "未知",
                jobTitle = resume.Job?.Title ?? "未知",
                jobId = resume.Position
            });
        }
    }
}
