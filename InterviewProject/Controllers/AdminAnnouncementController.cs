using InterviewProject.Data;
using Microsoft.AspNetCore.Mvc;

namespace InterviewProject.Controllers
{
    public class AdminAnnouncementController : Controller
    {
        private readonly AppDbContext _db;

        public AdminAnnouncementController(AppDbContext db)
        {
            _db = db;
        }

        public IActionResult Index()
        {
            // 驗證登入狀態
            var memberId = HttpContext.Session.GetInt32("MemberId");
            if (memberId == null)
                return RedirectToAction("Index", "Login");

            // 驗證角色：只有 hr 可以進入
            var role = HttpContext.Session.GetString("MemberRole")?.ToLower();
            if (role != "hr")
                return RedirectToAction("Index", "Home");

            ViewBag.MemberRole = role;
            ViewBag.MemberId   = memberId;

            return View();
        }
    }
}