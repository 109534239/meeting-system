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

            // 🎯 修正關鍵：將舊有的 .Include(s => s.Member) 改為經由 Resume 的點出關聯
            var query = _db.InterviewSchedules
                .Include(s => s.Room)
                .Include(s => s.ScheduledByEmployee)
                .Include(s => s.Resume)
                    .ThenInclude(r => r.Job)
                .Include(s => s.Resume)
                    .ThenInclude(r => r.Member) // 🎯 補上這行，確保前端 item.Resume.Member 有資料！
                .AsQueryable();

            if (role == "manager")
                query = query.Where(s => s.ScheduledByEmployeeId == empId);

            // 🎯 修正關鍵：排序由舊欄位 ScheduledAt 改用房間的 StartAt
            var schedules = await query
                .OrderByDescending(s => s.Room != null ? s.Room.StartAt : s.CreatedAt)
                .ToListAsync();

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

            var pendingResumes = await _db.Resumes
                .Include(r => r.Job)
                .Where(r => r.Status == "待審核" || r.Status == "通過" || r.Status == "已安排面試")
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

        // ── HR/主管：新增排程 POST ──
        [HttpPost]
        public async Task<IActionResult> Create(List<int> resumeIds, int? roomId)
        {
            if (!IsEmployee()) return RedirectToAction("Index", "Login");

            if (resumeIds == null || !resumeIds.Any())
            {
                TempData["Error"] = "請至少選擇一位求職者";
                return RedirectToAction("Create");
            }

            int targetRoomId;
            if (roomId == null)
            {
                var newRoom = new Room
                {
                    RoomName = $"面試-{DateTime.Now:yyyyMMdd-HHmm}",
                    CreatedTime = DateTime.Now,
                    JitsiRoomName = Guid.NewGuid().ToString("N")[..10],
                    StartAt = DateTime.Now.AddDays(1),
                    EndAt = DateTime.Now.AddDays(1).AddHours(1),
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

                // 🎯 修正關鍵：建立排程物件時，移除已不存在的欄位，僅傳入核心資料
                var schedule = new InterviewSchedule
                {
                    ResumeId = resume.Id,
                    ScheduledByEmployeeId = empId,
                    RoomId = targetRoomId,
                    CreatedAt = DateTime.Now
                };

                // 將狀態更動直接寫在 Resume 表的 Status 上
                resume.Status = "已安排面試";

                _db.InterviewSchedules.Add(schedule);
                addedCount++;
            }

            await _db.SaveChangesAsync();
            TempData["Success"] = $"已成功排程 {addedCount} 位求職者！";
            return RedirectToAction("Index");
        }

        // ── HR/主管：編輯排程 GET ──
        [HttpGet]
        public async Task<IActionResult> Edit(int id)
        {
            if (!IsEmployee()) return RedirectToAction("Index", "Login");

            var schedule = await _db.InterviewSchedules
                .Include(s => s.Room)
                .Include(s => s.Resume)
                    .ThenInclude(r => r.Member)
                .Include(s => s.Resume)
                    .ThenInclude(r => r.Job)
                .FirstOrDefaultAsync(s => s.Id == id);

            if (schedule == null) return NotFound();

            ViewBag.Rooms = await _db.Rooms.OrderByDescending(r => r.CreatedTime).ToListAsync();
            return View("~/Views/Interview/Edit.cshtml", schedule);
        }

        // ── HR/主管：編輯排程 POST ──
        [HttpPost]
        public async Task<IActionResult> Edit(int id, int? roomId, string? resultNote, int? resultScore, string? resumeStatus)
        {
            if (!IsEmployee()) return RedirectToAction("Index", "Login");

            var existing = await _db.InterviewSchedules
                .Include(s => s.Resume)
                .FirstOrDefaultAsync(s => s.Id == id);

            if (existing == null) return NotFound();

            existing.ResultNote = resultNote;
            existing.ResultScore = resultScore;

            if (roomId.HasValue) existing.RoomId = roomId;

            if (existing.Resume != null && !string.IsNullOrEmpty(resumeStatus))
            {
                existing.Resume.Status = resumeStatus;
            }

            await _db.SaveChangesAsync();
            TempData["Success"] = "排程已更新";
            return RedirectToAction("Index");
        }

        // ── HR/主管：取消排程 ──
        [HttpPost]
        public async Task<IActionResult> Cancel(int id)
        {
            if (!IsEmployee()) return RedirectToAction("Index", "Login");

            var s = await _db.InterviewSchedules
                .Include(x => x.Resume)
                .FirstOrDefaultAsync(x => x.Id == id);

            if (s == null) return NotFound();

            if (s.Resume != null)
            {
                s.Resume.Status = "不通過";
            }

            await _db.SaveChangesAsync();
            TempData["Success"] = "面試排程已取消，求職者狀態更新為不通過";
            return RedirectToAction("Index");
        }

        // ── HR/主管：標記完成 ──
        [HttpPost]
        public async Task<IActionResult> Complete(int id, string? resultNote, int resultScore, string nextStatus = "面試結束")
        {
            if (!IsEmployee()) return RedirectToAction("Index", "Login");

            var s = await _db.InterviewSchedules
                .Include(x => x.Resume)
                .FirstOrDefaultAsync(x => x.Id == id);

            if (s == null) return NotFound();

            s.ResultNote = resultNote;
            s.ResultScore = resultScore;

            if (s.Resume != null)
            {
                s.Resume.Status = nextStatus;
            }

            await _db.SaveChangesAsync();
            TempData["Success"] = "面試已標記為完成，分數與評語已儲存";
            return RedirectToAction("Index");
        }

        // ── 求職者：我的面試通知 ──
        public async Task<IActionResult> MyInterviews()
        {
            int memberId = GetMemberId();
            if (memberId == 0) return RedirectToAction("Index", "Login");

            // 🎯 修正關鍵：全面補齊關聯鏈，特別是 Resume -> Member 絕對不能漏
            var schedules = await _db.InterviewSchedules
                .Include(s => s.Room)
                .Include(s => s.ScheduledByEmployee)
                .Include(s => s.Resume)
                    .ThenInclude(r => r.Job)
                .Include(s => s.Resume)
                    .ThenInclude(r => r.Member) // 🎯 核心補強：把 Resume 表裡的 Member 物件一併載入！
                .Where(s => s.Resume != null && s.Resume.MembersId == memberId)
                .OrderByDescending(s => s.Room != null ? s.Room.StartAt : DateTime.MinValue)
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