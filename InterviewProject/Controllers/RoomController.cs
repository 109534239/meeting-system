using Microsoft.AspNetCore.Mvc;
using InterviewProject.Data;
using InterviewProject.Models;
using InterviewProject.Services;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;

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

        public IActionResult Index()
        {
            var rooms = _context.Rooms.ToList();
            return View(rooms);
        }

        public IActionResult Create()
        {
            return View();
        }

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

            if (_env.IsDevelopment())
            {
                string mockVideoPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "video", "男性面試官.y4m");

                _ = Task.Run(async () =>
                {
                    try
                    {
                        Console.WriteLine($"[預先部署] 本機環境建立成功，AI 面試官正在先行進入房間: {room.JitsiRoomName}");
                        await _botService.JoinRoomAsync(room.JitsiRoomName, mockVideoPath);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[預先部署 Error] AI 面試官預先導航失敗: {ex.Message}");
                    }
                });
            }
            else
            {
                Console.WriteLine($"[預先部署提示] 目前處於雲端環境 ({_env.EnvironmentName})，為免記憶體溢出 (OOM)，已跳過 Playwright 機器人部署。");
            }

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