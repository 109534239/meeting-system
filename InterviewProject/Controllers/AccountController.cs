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
            // 在 Log 噴出收到的帳密，確認網頁有沒有傳過來
            Console.WriteLine($"登入嘗試: 帳號={account}, 密碼={password}");

            var user = _context.Users
                .FirstOrDefault(x => x.Account == account && x.Password == password);

            if (user == null)
            {
                // 如果失敗，噴出目前資料庫到底有誰
                var existingUsers = _context.Users.Select(u => u.Account).ToList();
                Console.WriteLine("登入失敗。目前資料庫的人員有: " + string.Join(", ", existingUsers));

                ViewBag.Error = "帳號或密碼錯誤";
                return View();
            }

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