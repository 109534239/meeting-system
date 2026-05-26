using InterviewProject.Data;
using Microsoft.AspNetCore.Mvc;

namespace InterviewProject.Controllers
{
    public class AdminApplicationController : Controller
    {
        private readonly AppDbContext _db;

        public AdminApplicationController(AppDbContext db)
        {
            _db = db;
        }

        public IActionResult Index()
        {
            // 驗證登入狀態
            var memberId = HttpContext.Session.GetInt32("MemberId");
            if (memberId == null)
                return RedirectToAction("Index", "Login");

            // 驗證角色：只有 hr、manager、director 可以進入
            var role = HttpContext.Session.GetString("MemberRole")?.ToLower();
            if (role != "hr" && role != "manager" && role != "director")
                return RedirectToAction("Index", "Home");

            // 💡 傳入角色供 View 判斷（之後做功能時用得到）
            ViewBag.MemberRole = role;
            ViewBag.MemberId   = memberId;

            return View();
        }
    }
}