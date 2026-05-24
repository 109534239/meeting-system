using InterviewProject.Data;
using InterviewProject.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Security.Claims;

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

        // POST: 註冊（維持不變，僅針對一般求職者）
        [HttpPost]
        public async Task<IActionResult> Register(string name, string email, string phone, string password)
        {
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
                Role = "jobseeker"
            };

            _db.Members.Add(member);
            await _db.SaveChangesAsync();

            return RedirectToAction("Index");
        }

        // POST: 登入（調整為雙表查詢）
        [HttpPost]
        public async Task<IActionResult> Login(string account, string password)
        {
            Console.WriteLine($"==== 輸入密碼的 Hash 是: {HashPassword(password)} ====");
            
            string hashedPassword = HashPassword(password);

            // 1. 先從求職者資料表 (Members) 尋找
            var member = await _db.Members.FirstOrDefaultAsync(m => m.Email == account);

            if (member != null && member.PasswordHash == hashedPassword)
            {
                // 求職者登入成功，寫入 Session
                HttpContext.Session.SetInt32("MemberId", member.Id);
                HttpContext.Session.SetString("MemberName", member.Name);
                HttpContext.Session.SetString("MemberRole", member.Role); // "jobseeker"

                return RedirectToAction("Index", "Home");
            }

            // 2. 如果求職者找不到或密碼錯誤，改從員工資料表 (Employees) 尋找
            var employee = await _db.Employees.FirstOrDefaultAsync(e => e.Account == account);

            if (employee != null && employee.PasswordHash == hashedPassword)
            {
                // 員工/HR 登入成功，寫入 Session
                HttpContext.Session.SetInt32("MemberId", employee.Id); 
                HttpContext.Session.SetString("MemberName", employee.Name);
                HttpContext.Session.SetString("MemberRole", employee.Role); // 資料庫通常存的是 "hr" 或 "manager"

                // 🌟 自動跳轉：如果是 hr 或 manager，登入成功直接導向 HR 後台的「職缺管理」
                if (employee.Role == "hr" || employee.Role == "manager")
                {
                    return RedirectToAction("Index", "HrJob"); 
                }

                return RedirectToAction("Index", "Home");
            }

            // 3. 兩邊都找不到，才報帳密錯誤
            TempData["LoginError"] = "帳號或密碼錯誤";
            return RedirectToAction("Index");
        }

        // 登出
        public IActionResult Logout()
        {
            HttpContext.Session.Clear();
            
            // 登出後導向登入頁面
            return RedirectToAction("Index");
        }

        private static string HashPassword(string password)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(password));
            return Convert.ToHexString(bytes).ToLower();
        }
    }
}