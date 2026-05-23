using Microsoft.AspNetCore.Mvc;
using InterviewProject.Data;
using InterviewProject.Models;
using InterviewProject.Services;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System;

namespace InterviewProject.Controllers
{
    public class RoomController : Controller
    {
        private readonly AppDbContext _context;
        private readonly JitsiBotService _botService;

        public RoomController(AppDbContext context, JitsiBotService botService)
        {
            _context = context;
            _botService = botService;
        }

        public IActionResult Index()
        {
            var rooms = _context.Rooms.ToList();
            return View(rooms);
        }

        public IActionResult Create()
        {
            return View();
        }

        // 🚀 建立房間：存入資料庫後，立刻讓 AI 面試官先進房占位
        [HttpPost]
        public async Task<IActionResult> Create(string roomName)
        {
            if (string.IsNullOrEmpty(roomName))
            {
                ModelState.AddModelError("", "房間名稱不能為空");
                return View();
            }

            var room = new Room
            {
                RoomName = roomName,
                CreatedTime = DateTime.UtcNow,
                JitsiRoomName = Guid.NewGuid().ToString("N")[..10]
            };

            _context.Rooms.Add(room);
            await _context.SaveChangesAsync();

            // 🌟 關鍵調整：在使用者加入前，先派 AI 面試官進去循環播放
            string mockVideoPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "video", "男性面試官.y4m");

            // 使用 Fire-and-Forget (不 await 阻塞)，讓背景線程去啟動 Playwright
            _ = Task.Run(async () =>
            {
                try
                {
                    Console.WriteLine($"[預先部署] 房間建立成功，AI 面試官正在先行進入房間: {room.JitsiRoomName}");
                    await _botService.JoinRoomAsync(room.JitsiRoomName, mockVideoPath);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[預先部署 Error] AI 面試官預先導航失敗: {ex.Message}");
                }
            });

            return RedirectToAction("Index");
        }

        public IActionResult Join(string code)
        {
            var room = _context.Rooms.FirstOrDefault(x => x.JitsiRoomName == code);
            if (room == null) return Content("房間不存在");

            ViewBag.Room = room;
            return View();
        }
    }
}