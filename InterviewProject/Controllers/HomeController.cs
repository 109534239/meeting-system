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

        // 🌟 核心修正：將 Index 改為 async Task，並從資料庫撈出前 4 筆最新職缺送給 View
        public async Task<IActionResult> Index()
        {
            // 🔍 撈取資料庫中開放中 (IsActive == true) 並且依時間倒序排列的前 4 筆職缺
            var latestJobs = await _db.Jobs
                .Where(j => j.IsActive)
                .OrderByDescending(j => j.CreatedAt)
                .Take(4)
                .ToListAsync();

            // 🎯 關鍵：必須把最新職缺變數丟進 View() 括號裡，前端的 @model 才能接收到資料！
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