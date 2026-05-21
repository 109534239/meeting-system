using InterviewProject.Data;
using InterviewProject.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;

namespace InterviewProject.Controllers
{
    public class LoginController : Controller
    {
        private readonly AppDbContext _db;

        public LoginController(AppDbContext db)
        {
            _db = db;
        }

        // GET: 登入頁
        public IActionResult Index() => View();

        // GET: 註冊頁
        public IActionResult Register() => View();

        // POST: 註冊
        [HttpPost]
        public async Task<IActionResult> Register(string name, string email, string phone, string password, string role = "jobseeker")
        {
            // 檢查 Email 是否已註冊
            if (await _db.Members.AnyAsync(m => m.Email == email))
            {
                TempData["RegisterError"] = "此 Email 已被註冊";
                return RedirectToAction("Register");
            }

            var member = new Member
            {
                Name = name,
                Email = email,
                Phone = phone,
                PasswordHash = HashPassword(password),
                Role = role
            };

            _db.Members.Add(member);
            await _db.SaveChangesAsync();

            return RedirectToAction("Index"); // 導向登入頁
        }

        // POST: 登入
        [HttpPost]
        public async Task<IActionResult> Login(string account, string password, string role)
        {
            var member = await _db.Members
                .FirstOrDefaultAsync(m => m.Email == account && m.Role == role);

            if (member == null || member.PasswordHash != HashPassword(password))
            {
                TempData["LoginError"] = "帳號或密碼錯誤";
                return RedirectToAction("Index");
            }

            // 存入 Session
            HttpContext.Session.SetInt32("MemberId", member.Id);
            HttpContext.Session.SetString("MemberName", member.Name);
            HttpContext.Session.SetString("MemberRole", member.Role);

            return RedirectToAction("Index", "Home");
        }

        // 登出
        public IActionResult Logout()
        {
            HttpContext.Session.Clear();
            return RedirectToAction("Index");
        }

        private static string HashPassword(string password)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(password));
            return Convert.ToHexString(bytes);
        }
    }
}