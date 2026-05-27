using Microsoft.AspNetCore.Mvc;
using InterviewProject.Data;
using InterviewProject.Models;
using InterviewProject.Services;
using Microsoft.EntityFrameworkCore;

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

        // ── HR/主管：房間列表 ──
        public IActionResult Index()
        {
            var role = HttpContext.Session.GetString("MemberRole")?.ToLower();
            if (role != "hr" && role != "manager" && role != "director")
                return RedirectToAction("Index", "Login");

            var rooms = _context.Rooms.OrderByDescending(r => r.CreatedTime).ToList();
            return View(rooms);
        }

        // ── HR/主管：建立房間 GET ──
        [HttpGet]
        public IActionResult Create()
        {
            var role = HttpContext.Session.GetString("MemberRole")?.ToLower();
            if (role != "hr" && role != "manager" && role != "director")
                return RedirectToAction("Index", "Login");
            return View();
        }

        // ── HR/主管：建立房間 POST ──
        [HttpPost]
        public async Task<IActionResult> Create(string roomName, DateTime? startAt, DateTime? endAt,
                                                 int maxParticipants = 20, string? description = null)
        {
            var role = HttpContext.Session.GetString("MemberRole")?.ToLower();
            if (role != "hr" && role != "manager" && role != "director")
                return RedirectToAction("Index", "Login");

            if (string.IsNullOrWhiteSpace(roomName))
            {
                ModelState.AddModelError("", "房間名稱不能為空");
                return View();
            }

            var room = new Room
            {
                RoomName = roomName,
                CreatedTime = DateTime.Now,
                JitsiRoomName = Guid.NewGuid().ToString("N")[..12],
                StartAt = startAt,
                EndAt = endAt,
                MaxParticipants = maxParticipants,
                Description = description,
                IsActive = true
            };

            _context.Rooms.Add(room);
            await _context.SaveChangesAsync();

            TempData["Success"] = $"房間「{roomName}」已建立，代碼：{room.JitsiRoomName}";
            return RedirectToAction("Index");
        }

        // ── HR/主管：編輯房間 GET ──
        [HttpGet]
        public async Task<IActionResult> Edit(int id)
        {
            var role = HttpContext.Session.GetString("MemberRole")?.ToLower();
            if (role != "hr" && role != "manager" && role != "director")
                return RedirectToAction("Index", "Login");

            var room = await _context.Rooms.FindAsync(id);
            if (room == null) return NotFound();
            return View(room);
        }

        // ── HR/主管：編輯房間 POST ──
        [HttpPost]
        public async Task<IActionResult> Edit(int id, string roomName, DateTime? startAt, DateTime? endAt,
                                               int maxParticipants, bool isActive, string? description)
        {
            var role = HttpContext.Session.GetString("MemberRole")?.ToLower();
            if (role != "hr" && role != "manager" && role != "director")
                return RedirectToAction("Index", "Login");

            var room = await _context.Rooms.FindAsync(id);
            if (room == null) return NotFound();

            room.RoomName = roomName;
            room.StartAt = startAt;
            room.EndAt = endAt;
            room.MaxParticipants = maxParticipants;
            room.IsActive = isActive;
            room.Description = description;

            await _context.SaveChangesAsync();
            TempData["Success"] = "房間設定已更新";
            return RedirectToAction("Index");
        }

        // ── 輸入房間代碼 GET ──
        [HttpGet]
        public IActionResult EnterCode()
        {
            return View();
        }

        // ── 輸入房間代碼 POST ──
        [HttpPost]
        public IActionResult EnterCode(string roomCode)
        {
            if (string.IsNullOrWhiteSpace(roomCode))
            {
                ViewBag.ErrorMessage = "請輸入房間代碼";
                return View();
            }

            var room = _context.Rooms.FirstOrDefault(x => x.JitsiRoomName == roomCode.Trim());

            if (room == null)
            {
                ViewBag.ErrorMessage = "找不到該房間代碼，請確認是否輸入正確。";
                return View();
            }

            // ✅ 時間閘：未到開放時間或已結束，顯示提示頁
            if (!room.CanEnter())
            {
                ViewBag.Room = room;
                ViewBag.StatusText = room.StatusText();
                return View("RoomNotAvailable");
            }

            return RedirectToAction("Join", new { code = room.JitsiRoomName });
        }

        // ── 進入會議室 ──
        public IActionResult Join(string code)
        {
            var room = _context.Rooms.FirstOrDefault(x => x.JitsiRoomName == code);
            if (room == null) return Content("房間不存在");

            // 時間閘
            if (!room.CanEnter())
            {
                ViewBag.Room = room;
                ViewBag.StatusText = room.StatusText();
                return View("RoomNotAvailable");
            }

            ViewBag.Room = room;
            return View();
        }

        // ── API：取得房間狀態（輪詢用）──
        [HttpGet]
        public IActionResult RoomStatus(string code)
        {
            var room = _context.Rooms.FirstOrDefault(x => x.JitsiRoomName == code);
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