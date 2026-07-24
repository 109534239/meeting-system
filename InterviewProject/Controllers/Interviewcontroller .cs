using InterviewProject.Data;
using InterviewProject.Models;
using InterviewProject.Hubs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
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
        private readonly IHubContext<MeetingHub> _meetingHub;

        public InterviewController(AppDbContext db, IHubContext<MeetingHub> meetingHub)
        {
            _db = db;
            _meetingHub = meetingHub;
        }

        private bool IsEmployee()
        {
            var role = HttpContext.Session.GetString("MemberRole")?.ToLower();
            return role == "hr" || role == "manager" || role == "director";
        }

        private int GetEmployeeId() => HttpContext.Session.GetInt32("MemberId") ?? 0;
        private int GetMemberId() => HttpContext.Session.GetInt32("MemberId") ?? 0;

        // ── 主管/最高主管專用：面試房間列表（新版，資料來自 Rooms + RoomParticipants） ──
        //    🎯 HR 不參與會議，不開放這個頁面（即使直接打網址也一樣擋）
        public async Task<IActionResult> Index()
        {
            if (!IsEmployee()) return RedirectToAction("Index", "Login");

            var role = HttpContext.Session.GetString("MemberRole")?.ToLower();
            if (role != "manager" && role != "director")
            {
                TempData["Error"] = "此頁面僅供部門主管與最高主管使用";
                return RedirectToAction("Dashboard", "AdminHome");
            }

            int empId = GetEmployeeId();

            // 🎯 只列「新系統」自動建立的面試房間（有掛 JobsId 的），舊的手動測試房間不顯示
            var query = _db.Rooms
                .Include(r => r.Job)
                .Include(r => r.Participants)
                    .ThenInclude(p => p.Resume)
                        .ThenInclude(res => res!.Member)
                .Where(r => r.JobsId != null)
                // 主管/最高主管：只看自己被邀請（RoomParticipants.EmployeeId）的房間
                .Where(r => r.Participants.Any(p => p.EmployeeId == empId));

            var rooms = await query
               .OrderByDescending(r => r.ScheduledAt)
               .ToListAsync();

            ViewBag.CurrentRole = role;
            return View("~/Views/Interview/Index.cshtml", rooms);
        }

        // 🎯 只有最高主管能改自己主持的房間的預計面試時間，會議還沒開始才能改
        [HttpPost]
        public async Task<IActionResult> UpdateScheduledAt(int roomId, DateTime newTime)
        {
            var role = HttpContext.Session.GetString("MemberRole")?.ToLower();
            if (role != "director")
                return Json(new { success = false, message = "只有最高主管可以修改面試時間" });

            int empId = GetEmployeeId();

            var room = await _db.Rooms.FirstOrDefaultAsync(r => r.Id == roomId);
            if (room == null)
                return Json(new { success = false, message = "找不到這個房間" });

            var isDirectorOfThisRoom = await _db.RoomParticipants.AnyAsync(p =>
                p.RoomId == roomId && p.Role == ParticipantRole.Director && p.EmployeeId == empId);
            if (!isDirectorOfThisRoom)
                return Json(new { success = false, message = "你不是這場面試的主持人" });

            if (room.MeetingStatus != "NotStarted")
                return Json(new { success = false, message = "會議已經開始或結束，無法再改時間" });

            if (newTime <= DateTime.Now)
                return Json(new { success = false, message = "時間只能選現在之後" });

            room.ScheduledAt = newTime;
            await _db.SaveChangesAsync();

            // 🎯 即時通知：正在這個房間頁面（等待畫面）的人會立刻收到最新時間
            //    不在頁面上的人（例如還沒打開應徵管理／面試管理），下次打開頁面時本來就會看到資料庫裡的新時間
            await _meetingHub.Clients.Group(room.JitsiRoomName).SendAsync(
                "ScheduledAtChanged",
                newTime.ToString("yyyy-MM-dd HH:mm:ss"));

            return Json(new { success = true, newTime = newTime.ToString("yyyy-MM-dd HH:mm:ss") });
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