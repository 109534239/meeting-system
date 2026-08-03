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
            // 🎯 撈出「所有」開放中的職缺供前端 JS 進行全域排序
            var latestJobs = await _db.Jobs
                .Where(j => j.IsActive)
                .OrderByDescending(j => j.CreatedAt)
                .ToListAsync();

            // 💡 取得今天的日期（不含時間 component，格式為 yyyy-MM-dd 00:00:00）
            var today = DateTime.Today;

            // 🎯 撈取「啟用中」且「在上下架日期區間內」的公告
            var announcementsData = await _db.Announcements
                .Where(a => a.IsActive
                         && a.SDate.Date <= today    // 已到達或過了上架日期
                         && a.CDate.Date >= today)   // 尚未過期（8/3 時今天為 8/3 通過；8/4 時 today 為 8/4 不通過）
                .OrderByDescending(a => a.SDate)     // 建議依上架日期排序
                .Take(5)
                .ToListAsync();

            // 🎯 在 Controller 計算好分類對應的 Badge Class
            var announcements = announcementsData.Select(a => new
            {
                a.Id,
                Date = a.SDate.ToString("yyyy/MM/dd"), // 整理顯示用日期格式
                a.Category,
                a.Title,
                a.Content,
                BadgeClass = a.Category switch
                {
                    "最新" => "badge-最新",
                    "公告" => "badge-公告",
                    "活動" => "badge-活動",
                    _ => "badge-預設"
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