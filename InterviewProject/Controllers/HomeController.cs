using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore; // 🌟 記得引用這個，才能使用 ToListAsync()
using InterviewProject.Data;       // 🌟 記得引用你的 DbContext 所在的命名空間
using InterviewProject.Models;

namespace InterviewProject.Controllers
{
    public class HomeController : Controller
    {
        private readonly ILogger<HomeController> _logger;
        private readonly AppDbContext _db; // 🌟 新增：資料庫連線物件

        // 🌟 修正：在建構子同時注入 Logger 與 AppDbContext
        public HomeController(ILogger<HomeController> logger, AppDbContext db)
        {
            _logger = logger;
            _db = db;
        }

        public async Task<IActionResult> Index()
        {
            // 最新職缺
            var latestJobs = await _db.Jobs
                .Where(j => j.IsActive)
                .OrderByDescending(j => j.CreatedAt)
                .Take(4)
                .ToListAsync();

            // 💡 撈取啟用中的公告
            var announcementsData = await _db.Announcements
                .Where(a => a.IsActive)
                .OrderByDescending(a => a.Date)
                .Take(5)
                .ToListAsync();

            // 🎯 在 Controller 計算好分類對應的 Badge Class
            var announcements = announcementsData.Select(a => new
            {
                a.Id,
                a.Date,
                a.Category,
                a.Title,
                a.Content,
                // 根據 Category 欄位決定對應的 CSS Class 名稱
                BadgeClass = a.Category switch
                {
                    "最新" => "badge-最新",
                    "公告" => "badge-公告",
                    "活動" => "badge-活動",
                    _ => "badge-預設" // 其他分類時的預設樣式
                }
            }).ToList();

            ViewBag.Announcements = announcements;

            return View(latestJobs);
        }

        public IActionResult Privacy()
        {
            return View();
        }

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
        }
    }
}