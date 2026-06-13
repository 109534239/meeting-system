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

            var room = await _context.Rooms.FirstOrDefaultAsync(x => x.JitsiRoomName == roomCode.Trim());
            if (room == null) { ViewBag.ErrorMessage = "找不到此房間代碼"; return View(); }

            if (!room.CanEnter())
            {
                ViewBag.Room = room;
                ViewBag.StatusText = room.StatusText();
                return View("RoomNotAvailable");
            }

            return RedirectToAction("Join", new { code = room.JitsiRoomName });
        }

        // 🎯 修正：加入安全性阻擋與非同步優化
        public async Task<IActionResult> Join(string code)
        {
            // 安全限制：至少必須是登入的使用者（求職者或員工皆可）才可以進去
            if (HttpContext.Session.GetInt32("MemberId") == null)
                return RedirectToAction("Index", "Login");

            var room = await _context.Rooms.FirstOrDefaultAsync(x => x.JitsiRoomName == code);
            if (room == null) return Content("房間不存在");

            if (!room.CanEnter())
            {
                ViewBag.Room = room;
                ViewBag.StatusText = room.StatusText();
                return View("RoomNotAvailable");
            }

            ViewBag.Room = room;
            return View();
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