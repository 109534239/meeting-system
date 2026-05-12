using Microsoft.AspNetCore.Mvc;
using InterviewProject.Data;

namespace InterviewProject.Controllers
{
    public class AccountController : Controller
    {
        private readonly AppDbContext _context;

        public AccountController(AppDbContext context)
        {
            _context = context;
        }

        // 登入頁
        public IActionResult Login()
        {
            return View();
        }

        // 登入處理
        [HttpPost]
        public IActionResult Login(string account, string password)
        {
            var user = _context.Users
                .FirstOrDefault(x => x.Account == account && x.Password == password);

            if (user == null)
            {
                ViewBag.Error = "帳號或密碼錯誤";
                return View();
            }

            // 存 Session
            HttpContext.Session.SetInt32("UserId", user.Id);
            HttpContext.Session.SetString("UserName", user.Account);

            return RedirectToAction("Index", "Room");
        }

        // 登出
        public IActionResult Logout()
        {
            HttpContext.Session.Clear();
            return RedirectToAction("Login");
        }
    }
}