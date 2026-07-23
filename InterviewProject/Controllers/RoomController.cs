using Microsoft.AspNetCore.Mvc;
using InterviewProject.Data;
using InterviewProject.Models;
using InterviewProject.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace InterviewProject.Controllers
{
    public class RoomController : Controller
    {
        private readonly AppDbContext _context;
        private readonly JitsiBotService _botService;
        private readonly IWebHostEnvironment _env;

        public RoomController(AppDbContext context, JitsiBotService botService, IWebHostEnvironment env)
        {
            _context = context;
            _botService = botService;
            _env = env;
        }

        private bool IsEmployee()
        {
            var role = HttpContext.Session.GetString("MemberRole")?.ToLower();
            return role == "hr" || role == "manager" || role == "director";
        }

        // 🎯 修正：將同步改成非同步，提升高並發時的效能
        public async Task<IActionResult> Index()
        {
            if (!IsEmployee()) return RedirectToAction("Index", "Login");

            var rooms = await _context.Rooms.OrderByDescending(r => r.CreatedTime).ToListAsync();
            return View(rooms);
        }

        [HttpGet]
        public IActionResult Create()
        {
            if (!IsEmployee()) return RedirectToAction("Index", "Login");
            return View();
        }

        [HttpPost]
        public async Task<IActionResult> Create(string roomName, DateTime? startAt, DateTime? endAt,
                                                int maxParticipants = 20, string? description = null)
        {
            if (!IsEmployee()) return RedirectToAction("Index", "Login");
            if (string.IsNullOrWhiteSpace(roomName)) { ModelState.AddModelError("", "房間名稱不能為空"); return View(); }

            // 🎯 防呆機制：防止排程時間前後顛倒
            if (startAt.HasValue && endAt.HasValue && endAt.Value <= startAt.Value)
            {
                ModelState.AddModelError("", "面試結束時間必須晚於開始時間！");
                return View();
            }

            var room = new Room
            {
                RoomName = roomName,
                CreatedTime = DateTime.Now,
                JitsiRoomName = Guid.NewGuid().ToString("N")[..10],
                StartAt = startAt,
                EndAt = endAt,
                IsActive = true
            };

            _context.Rooms.Add(room);
            await _context.SaveChangesAsync();
            TempData["Success"] = $"房間「{roomName}」已建立，代碼：{room.JitsiRoomName}";
            return RedirectToAction("Index");
        }

        [HttpGet]
        public async Task<IActionResult> Edit(int id)
        {
            if (!IsEmployee()) return RedirectToAction("Index", "Login");
            var room = await _context.Rooms.FindAsync(id);
            if (room == null) return NotFound();
            return View(room);
        }

        [HttpPost]
        public async Task<IActionResult> Edit(int id, string roomName, DateTime? startAt, DateTime? endAt,
                                             int maxParticipants, bool isActive, string? description)
        {
            if (!IsEmployee()) return RedirectToAction("Index", "Login");

            if (string.IsNullOrWhiteSpace(roomName)) { ModelState.AddModelError("", "房間名稱不能為空"); return View(); }

            // 🎯 防呆機制：編輯時也要防止時間顛倒
            if (startAt.HasValue && endAt.HasValue && endAt.Value <= startAt.Value)
            {
                ModelState.AddModelError("", "面試結束時間必須晚於開始時間！");
                var currentRoom = await _context.Rooms.FindAsync(id);
                return View(currentRoom);
            }

            var room = await _context.Rooms.FindAsync(id);
            if (room == null) return NotFound();

            room.RoomName = roomName;
            room.StartAt = startAt;
            room.EndAt = endAt;
            room.IsActive = isActive;

            await _context.SaveChangesAsync();
            TempData["Success"] = "房間設定已更新";
            return RedirectToAction("Index");
        }

        [HttpGet]
        public IActionResult EnterCode() => View();

        // 🎯 修正：輸入房間代碼也改為非同步查詢，避免點擊加入時網頁卡死
        [HttpPost]
        public async Task<IActionResult> EnterCode(string roomCode)
        {
            if (string.IsNullOrWhiteSpace(roomCode)) { ViewBag.ErrorMessage = "請輸入房間代碼"; return View(); }

            // 🎯 Step C：必須先登入（求職者或員工皆可），才能查代碼
            var sessionMemberId = HttpContext.Session.GetInt32("MemberId");
            var sessionRole = HttpContext.Session.GetString("MemberRole")?.ToLower();
            if (sessionMemberId == null || string.IsNullOrEmpty(sessionRole))
            {
                return RedirectToAction("Index", "Login");
            }

            var room = await _context.Rooms.FirstOrDefaultAsync(x => x.JitsiRoomName == roomCode.Trim());
            if (room == null) { ViewBag.ErrorMessage = "找不到此房間代碼"; return View(); }

            if (!room.CanEnter())
            {
                ViewBag.Room = room;
                ViewBag.StatusText = room.StatusText();
                return View("RoomNotAvailable");
            }

            // 🎯 Step C：白名單檢查——只有 RoomParticipants 裡受邀的人才能進
            //    （沒有受邀名單的房間，代表是舊有/手動建立的一般房間，維持原本「登入即可進」的行為）
            var hasParticipantList = await _context.RoomParticipants.AnyAsync(p => p.RoomId == room.Id);
            if (hasParticipantList)
            {
                var participant = await FindParticipantAsync(room, sessionMemberId.Value, sessionRole);
                if (participant == null)
                {
                    ViewBag.ErrorMessage = "您不是這場會議受邀的人員，無法進入";
                    return View();
                }

                // 🎯 求職者一定要先完成適性測驗，才能真的進入會議室（就算已經受邀、知道代碼也一樣）
                if (participant.Role == ParticipantRole.Jobseeker)
                {
                    var hasTest = await _context.AptitudeTestResults.AnyAsync(t => t.ResumeId == participant.ResumeId);
                    if (!hasTest)
                    {
                        ViewBag.ErrorMessage = "請先完成適性測驗，才能進入面試";
                        return View();
                    }
                }
            }

            return RedirectToAction("Join", new { code = room.JitsiRoomName });
        }

        // 🎯 修正：加入安全性阻擋與非同步優化
        public async Task<IActionResult> Join(string code)
        {
            // 安全限制：至少必須是登入的使用者（求職者或員工皆可）才可以進去
            var sessionMemberId = HttpContext.Session.GetInt32("MemberId");
            var sessionRole = HttpContext.Session.GetString("MemberRole")?.ToLower();
            if (sessionMemberId == null || string.IsNullOrEmpty(sessionRole))
                return RedirectToAction("Index", "Login");

            var room = await _context.Rooms.FirstOrDefaultAsync(x => x.JitsiRoomName == code);
            if (room == null) return Content("房間不存在");

            if (!room.CanEnter())
            {
                ViewBag.Room = room;
                ViewBag.StatusText = room.StatusText();
                return View("RoomNotAvailable");
            }

            // 🎯 Step C：白名單檢查，並記錄受邀者的進場狀態
            var hasParticipantList = await _context.RoomParticipants.AnyAsync(p => p.RoomId == room.Id);
            if (hasParticipantList)
            {
                var participant = await FindParticipantAsync(room, sessionMemberId.Value, sessionRole);
                if (participant == null)
                {
                    ViewBag.Room = room;
                    ViewBag.ErrorMessage = "您不是這場會議受邀的人員，無法進入";
                    return View("RoomNotAvailable");
                }

                participant.Status = ParticipantStatus.Admitted;
                participant.JoinedAt = DateTime.Now;

                // 🎯 求職者一定要先完成適性測驗，才能真的進入會議室（就算已經受邀、知道代碼也一樣）
                if (participant.Role == ParticipantRole.Jobseeker)
                {
                    var hasTest = await _context.AptitudeTestResults.AnyAsync(t => t.ResumeId == participant.ResumeId);
                    if (!hasTest)
                    {
                        ViewBag.Room = room;
                        ViewBag.ErrorMessage = "請先完成適性測驗，才能進入面試";
                        return View("RoomNotAvailable");
                    }
                }

                // 🎯 注意：這裡只做資格檢查，不寫入資料庫。
                //    Status=Admitted、JoinedAt 要等使用者在 Jitsi 畫面真的按下「加入會議」才算數，
                //    由前端 videoConferenceJoined 事件呼叫 /Room/MarkJoined 來記錄（見下方 MarkJoined action）。

                ViewBag.ParticipantRole = participant.Role;
            }

            ViewBag.Room = room;
            return View();
        }

        // 🎯 只有前端在 Jitsi 真的觸發 videoConferenceJoined（使用者按下「加入會議」）時才會呼叫這裡，
        //    這樣 RoomParticipants.JoinedAt 記錄的才是「真的進了視訊會議」的時間，不是「打開這個頁面」的時間。
        [HttpPost]
        public async Task<IActionResult> MarkJoined(string code)
        {
            var sessionMemberId = HttpContext.Session.GetInt32("MemberId");
            var sessionRole = HttpContext.Session.GetString("MemberRole")?.ToLower();
            if (sessionMemberId == null || string.IsNullOrEmpty(sessionRole))
                return Json(new { success = false });

            var room = await _context.Rooms.FirstOrDefaultAsync(x => x.JitsiRoomName == code);
            if (room == null) return Json(new { success = false });

            var participant = await FindParticipantAsync(room, sessionMemberId.Value, sessionRole);
            if (participant == null) return Json(new { success = false });

            participant.Status = ParticipantStatus.Admitted;
            participant.JoinedAt = DateTime.Now;
            await _context.SaveChangesAsync();

            return Json(new { success = true });
        }

        // 🎯 Step C：依目前登入者身分，查出他在這個房間的受邀紀錄
        //    求職者：session 存的是 Member.Id，要透過 Resume 反查
        //    員工（manager / director / hr）：session 存的是 Employee.Id，直接比對
        private async Task<RoomParticipant?> FindParticipantAsync(Room room, int sessionMemberId, string role)
        {
            if (role == "jobseeker")
            {
                return await _context.RoomParticipants
                    .Include(p => p.Resume)
                    .FirstOrDefaultAsync(p => p.RoomId == room.Id
                        && p.Role == ParticipantRole.Jobseeker
                        && p.Resume != null
                        && p.Resume.MembersId == sessionMemberId);
            }

            return await _context.RoomParticipants
                .FirstOrDefaultAsync(p => p.RoomId == room.Id && p.EmployeeId == sessionMemberId);
        }

        [HttpGet]
        public async Task<IActionResult> RoomStatus(string code)
        {
            var room = await _context.Rooms.FirstOrDefaultAsync(x => x.JitsiRoomName == code);
            if (room == null) return Json(new { found = false });

            return Json(new
            {
                found = true,
                canEnter = room.CanEnter(),
                statusText = room.StatusText(),
                startAt = room.StartAt?.ToString("yyyy-MM-ddTHH:mm:ss"),
                endAt = room.EndAt?.ToString("yyyy-MM-ddTHH:mm:ss")
            });
        }
    }
}