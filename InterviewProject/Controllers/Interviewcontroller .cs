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

        private bool IsEmployee()
        {
            var role = HttpContext.Session.GetString("MemberRole")?.ToLower();
            return role == "hr" || role == "manager" || role == "director";
        }

        private int GetEmployeeId() => HttpContext.Session.GetInt32("MemberId") ?? 0;
        private int GetMemberId() => HttpContext.Session.GetInt32("MemberId") ?? 0;

        // ── HR/主管：面試排程列表 ──
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

            var schedules = await query.OrderByDescending(s => s.ScheduledAt).ToListAsync();
            return View("~/Views/Interview/Index.cshtml", schedules);
        }

        // ── HR/主管：新增排程 GET ──
        [HttpGet]
        public async Task<IActionResult> Create(int? resumeId)
        {
            if (!IsEmployee()) return RedirectToAction("Index", "Login");

            ViewBag.Rooms = await _db.Rooms
                .Where(r => r.IsActive)
                .OrderByDescending(r => r.CreatedTime)
                .ToListAsync();

            if (resumeId.HasValue)
            {
                var resume = await _db.Resumes
                    .Include(r => r.Job)
                    .FirstOrDefaultAsync(r => r.Id == resumeId);

                if (resume != null)
                {
                    var member = await _db.Members.FindAsync(resume.MembersId);
                    ViewBag.PrefilledResumeId = resume.Id;
                    ViewBag.PrefilledMember = member;
                    ViewBag.PrefilledJob = resume.Job?.Title;
                }
            }

            // 可排面試的履歷
            var pendingResumes = await _db.Resumes
                .Include(r => r.Job)
                .Where(r => r.Status == "待審核" || r.Status == "書審通過" || r.Status == "通過")
                .OrderByDescending(r => r.ResumeTime)
                .ToListAsync();

            var memberIds = pendingResumes.Select(r => r.MembersId).Distinct().ToList();
            var members = await _db.Members
                .Where(m => memberIds.Contains(m.Id))
                .ToDictionaryAsync(m => m.Id, m => m.Name);

            ViewBag.PendingResumes = pendingResumes;
            ViewBag.MemberNames = members;

            return View("~/Views/Interview/Create.cshtml");
        }

        // ── HR/主管：新增排程 POST（支援多位求職者）──
        [HttpPost]
        public async Task<IActionResult> Create(
            List<int> resumeIds,        // 複選：多位求職者的履歷 ID
            DateTime scheduledAt,
            int? roomId,
            string? notes)
        {
            if (!IsEmployee()) return RedirectToAction("Index", "Login");

            if (resumeIds == null || !resumeIds.Any())
            {
                TempData["Error"] = "請至少選擇一位求職者";
                return RedirectToAction("Create");
            }

            // 若未選擇房間則統一建立一個新房間（所有求職者共用同一房間）
            int targetRoomId;
            if (roomId == null)
            {
                var newRoom = new Room
                {
                    RoomName = $"面試-{DateTime.Now:yyyyMMdd-HHmm}",
                    CreatedTime = DateTime.Now,
                    JitsiRoomName = Guid.NewGuid().ToString("N")[..10],
                    IsActive = true
                };
                _db.Rooms.Add(newRoom);
                await _db.SaveChangesAsync();
                targetRoomId = newRoom.Id;
            }
            else
            {
                targetRoomId = roomId.Value;
            }

            int empId = GetEmployeeId();
            int addedCount = 0;

            foreach (var rid in resumeIds.Distinct())
            {
                var resume = await _db.Resumes.FindAsync(rid);
                if (resume == null) continue;

                var schedule = new InterviewSchedule
                {
                    MemberId = resume.MembersId,
                    ResumeId = resume.Id,
                    JobId = resume.JobsId,
                    ScheduledByEmployeeId = empId,
                    ScheduledAt = scheduledAt,
                    Notes = notes,
                    RoomId = targetRoomId,
                    Status = "待確認",
                    CreatedAt = DateTime.Now
                };

                resume.Status = "已排面試";
                _db.InterviewSchedules.Add(schedule);
                addedCount++;
            }

            await _db.SaveChangesAsync();

            TempData["Success"] = $"已成功排程 {addedCount} 位求職者的面試！";
            return RedirectToAction("Index");
        }

        // ── HR/主管：編輯排程 GET ──
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

        // ── HR/主管：編輯排程 POST ──
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
            if (model.RoomId.HasValue) existing.RoomId = model.RoomId;

            await _db.SaveChangesAsync();
            TempData["Success"] = "排程已更新";
            return RedirectToAction("Index");
        }

        // ── HR/主管：取消排程 ──
        [HttpPost]
        public async Task<IActionResult> Cancel(int id)
        {
            if (!IsEmployee()) return RedirectToAction("Index", "Login");
            var s = await _db.InterviewSchedules.FindAsync(id);
            if (s == null) return NotFound();
            s.Status = "已取消";
            await _db.SaveChangesAsync();
            TempData["Success"] = "面試排程已取消";
            return RedirectToAction("Index");
        }

        // ── HR/主管：標記完成 ──
        [HttpPost]
        public async Task<IActionResult> Complete(int id, string? resultNote)
        {
            if (!IsEmployee()) return RedirectToAction("Index", "Login");
            var s = await _db.InterviewSchedules.FindAsync(id);
            if (s == null) return NotFound();
            s.Status = "已完成";
            s.ResultNote = resultNote;
            await _db.SaveChangesAsync();
            TempData["Success"] = "面試已標記為完成";
            return RedirectToAction("Index");
        }

        // ── 求職者：我的面試通知 ──
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

        // ── AJAX：依 resumeId 取得求職者資訊 ──
        [HttpGet]
        public async Task<IActionResult> GetResumeInfo(int resumeId)
        {
            var resume = await _db.Resumes.Include(r => r.Job).FirstOrDefaultAsync(r => r.Id == resumeId);
            if (resume == null) return Json(new { success = false });

            var member = await _db.Members.FindAsync(resume.MembersId);
            return Json(new
            {
                success = true,
                memberName = member?.Name ?? "未知",
                jobTitle = resume.Job?.Title ?? "未知"
            });
        }
    }
}
