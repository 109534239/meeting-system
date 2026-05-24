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


        // 忘記密碼頁面
        public IActionResult Forgetpassword() => View();

        // 1. 發送驗證碼 (測試用：直接回傳驗證碼)
        [HttpPost]
        public async Task<IActionResult> SendVerificationCode(string email)
        {
            // 1. 檢查會員是否存在
            var member = await _db.Members.FirstOrDefaultAsync(m => m.Email == email);
            if (member == null) return Json(new { success = false, message = "找不到此 Email" });

            // 2. 產生 6 位數隨機碼
            string code = new Random().Next(100000, 999999).ToString();
            // 使用 Local Time (在地時間) 比較直觀，避免 Utc 造成的時間差誤解
            DateTime now = DateTime.Now;
            DateTime expire = now.AddMinutes(10);

            // 3. 【核心修改】：嘗試抓取該會員是否已有存在的驗證碼紀錄
            var existingCode = await _db.VerificationCodes
                .FirstOrDefaultAsync(v => v.MemberId == member.Id);

            if (existingCode != null)
            {
                // 強制更新所有屬性，確保 EF 標記為 Modified
                existingCode.Code = code;
                existingCode.ExpireTime = expire; // 更新時效
                existingCode.IsUsed = false;

                _db.Entry(existingCode).State = EntityState.Modified; // 強制標記為已修改
            }
            else
            {
                var vCode = new VerificationCode
                {
                    MemberId = member.Id,
                    Code = code,
                    ExpireTime = expire,
                    IsUsed = false
                };
                _db.VerificationCodes.Add(vCode);
            }

            // 4. 儲存並回傳
            await _db.SaveChangesAsync();

            return Json(new
            {
                success = true,
                message = $"驗證碼已發送：{code}",
                code = code,
                // 新增：回傳格式化後的時間（例如 14:30:05）
                expiryTime = expire.ToString("HH:mm:ss")
            });
        }

        // 2. 驗證並重設密碼
        [HttpPost]
        public async Task<IActionResult> ResetPassword(string email, string code, string newPassword)
        {
            var member = await _db.Members.FirstOrDefaultAsync(m => m.Email == email);
            if (member == null) return Json(new { success = false, message = "用戶不存在" });

            var vEntry = await _db.VerificationCodes
                .Where(v => v.MemberId == member.Id && v.Code == code && !v.IsUsed && v.ExpireTime > DateTime.UtcNow)
                .OrderByDescending(v => v.Id)
                .FirstOrDefaultAsync();

            if (vEntry == null) return Json(new { success = false, message = "驗證碼錯誤或已過期" });

            // 更新密碼 (使用你原本的 Hash 方法)
            member.PasswordHash = HashPassword(newPassword);
            vEntry.IsUsed = true; // 標記驗證碼已使用

            await _db.SaveChangesAsync();
            return Json(new { success = true, message = "密碼重設成功，請重新登入" });
        }

        // 3. 供前端步驟跳轉用的單獨驗證 (選用，若你的前端 checkCode 需要)
        [HttpPost]
        public async Task<IActionResult> VerifyCodeOnly(string email, string code)
        {
            var member = await _db.Members.FirstOrDefaultAsync(m => m.Email == email);
            if (member == null) return Json(new { success = false, message = "用戶不存在" });

            var vEntry = await _db.VerificationCodes
                .Where(v => v.MemberId == member.Id && v.Code == code && !v.IsUsed && v.ExpireTime > DateTime.UtcNow)
                .OrderByDescending(v => v.Id)
                .FirstOrDefaultAsync();

            if (vEntry == null) return Json(new { success = false, message = "驗證碼錯誤或已過期" });

            return Json(new { success = true });
        }

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