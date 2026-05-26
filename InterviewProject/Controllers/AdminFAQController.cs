using InterviewProject.Data;
using Microsoft.AspNetCore.Mvc;

namespace InterviewProject.Controllers
{
    public class AdminFAQController : Controller
    {
        private readonly AppDbContext _db;

        public AdminFAQController(AppDbContext db)
        {
            _db = db;
        }

        // ==============================
        // Q&A 後台管理（編輯）
        // 權限：僅 hr
        // ==============================
        public IActionResult Index()
        {
            var memberId = HttpContext.Session.GetInt32("MemberId");
            if (memberId == null)
                return RedirectToAction("Index", "Login");

            var role = HttpContext.Session.GetString("MemberRole")?.ToLower();
            if (role != "hr")
                return RedirectToAction("Index", "Home");

            ViewBag.MemberRole = role;
            ViewBag.MemberId   = memberId;

            return View();
        }

        // ==============================
        // Q&A 回報
        // 權限：hr（全部）/ manager（僅本部門職缺）
        // ==============================
        public IActionResult Report()
        {
            var memberId = HttpContext.Session.GetInt32("MemberId");
            if (memberId == null)
                return RedirectToAction("Index", "Login");

            var role = HttpContext.Session.GetString("MemberRole")?.ToLower();
            if (role != "hr" && role != "manager")
                return RedirectToAction("Index", "Home");

            ViewBag.MemberRole = role;
            ViewBag.MemberId   = memberId;

            // 💡 之後查詢時用這個判斷：
            // if (role == "manager") → 只撈該主管部門的 Q&A
            // if (role == "hr")      → 撈全部 Q&A

            return View();
        }
    }
}