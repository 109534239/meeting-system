using InterviewProject.Data;
using InterviewProject.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Net.Mail;
using System.Security.Claims;
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

            // 🌟 核心修正：強行啟用 PostgreSQL 舊版時間相容開關，允許直接寫入與比對標準 DateTime
            AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
        }

        // GET: 登入頁
        public IActionResult Index() => View();

        // GET: 註冊頁
        public IActionResult Register() => View();


        // 忘記密碼頁面
        public IActionResult Forgetpassword() => View();

        [HttpPost]
        public async Task<IActionResult> SendVerificationCode(string email)
        {
            try
            {
                if (!IsValidAsciiEmail(email))
                {
                    return Json(new { success = false, message = "Email 格式不符或包含非 ASCII 字元" });
                }

                var member = await _db.Members.FirstOrDefaultAsync(m => m.Email == email);
                if (member == null)
                    return Json(new { success = false, message = "找不到此 Email" });

                string code = new Random().Next(100000, 999999).ToString();
                DateTime now = DateTime.Now;
                DateTime expire = now.AddMinutes(10);

                var existingCode = await _db.VerificationCodes.FirstOrDefaultAsync(v => v.MemberId == member.Id);
                if (existingCode != null)
                {
                    existingCode.Code = code;
                    existingCode.ExpireTime = expire;
                    existingCode.IsUsed = false;
                    _db.Entry(existingCode).State = EntityState.Modified;
                }
                else
                {
                    _db.VerificationCodes.Add(new VerificationCode
                    {
                        MemberId = member.Id,
                        Code = code,
                        ExpireTime = expire,
                        IsUsed = false
                    });
                }

                await _db.SaveChangesAsync();
                await SendEmailAsync(email, code);

                return Json(new
                {
                    success = true,
                    expiryTime = expire.ToString("HH:mm:ss")
                });
            }
            catch (Exception ex)
            {
                string errorMsg = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                return Json(new { success = false, message = $"郵件發送失敗。原因：{errorMsg}" });
            }
        }

        private bool IsValidAsciiEmail(string email)
        {
            if (string.IsNullOrWhiteSpace(email)) return false;
            var regex = new System.Text.RegularExpressions.Regex(@"^[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}$");
            return regex.IsMatch(email);
        }

        // 🌟 最終版寄信方法（已解決 Authentication Required 問題）
        private async Task SendEmailAsync(string toEmail, string code)
        {
            // ========== 請務必改成你的真實資訊 ==========
            string fromEmail = "angela296123@gmail.com";
            string appPassword = "zgpj hcew cyqc qxiy";
            string companyName = "XXX公司";
            // ============================================

            using (var smtpClient = new SmtpClient("smtp.gmail.com", 587))
            {
                // 強制設定：必須先關閉預設憑證，再指定你的憑證
                smtpClient.UseDefaultCredentials = false;
                smtpClient.Credentials = new NetworkCredential(fromEmail, appPassword);
                smtpClient.EnableSsl = true; // 開啟 TLS 加密
                smtpClient.DeliveryFormat = SmtpDeliveryFormat.International;

                using (var mailMessage = new MailMessage())
                {
                    mailMessage.From = new MailAddress(fromEmail, companyName, Encoding.UTF8);
                    mailMessage.To.Add(toEmail);
                    mailMessage.Subject = $"【{companyName}】帳戶密碼重置驗證碼";
                    mailMessage.SubjectEncoding = Encoding.UTF8;
                    mailMessage.BodyEncoding = Encoding.UTF8;
                    mailMessage.IsBodyHtml = true;

                    mailMessage.Body = $@"
                        <h3>您好：</h3>
                        <p>我們收到了您重設密碼的請求。</p>
                        <p>您的 6 位數驗證碼為：<b style='color: red; font-size: 20px;'>{code}</b></p>
                        <p>該驗證碼將於 10 分鐘後過期，請儘速完成驗證。</p>
                        <br>
                        <p>如果您並未要求重設密碼，請忽略此郵件。</p>";

                    await smtpClient.SendMailAsync(mailMessage);
                }
            }
        }

        [HttpPost]
        public async Task<IActionResult> VerifyCodeOnly(string email, string code)
        {
            var member = await _db.Members.FirstOrDefaultAsync(m => m.Email == email);
            if (member == null) return Json(new { success = false, message = "用戶不存在" });

            var vEntry = await _db.VerificationCodes
                .Where(v => v.MemberId == member.Id && v.Code == code && !v.IsUsed && v.ExpireTime > DateTime.Now)
                .OrderByDescending(v => v.Id)
                .FirstOrDefaultAsync();

            if (vEntry == null) return Json(new { success = false, message = "驗證碼錯誤或已過期" });
            return Json(new { success = true });
        }

        [HttpPost]
        public async Task<IActionResult> ResetPassword(string email, string code, string newPassword)
        {
            var member = await _db.Members.FirstOrDefaultAsync(m => m.Email == email);
            if (member == null) return Json(new { success = false, message = "用戶不存在" });

            var vEntry = await _db.VerificationCodes
                .Where(v => v.MemberId == member.Id && v.Code == code && !v.IsUsed && v.ExpireTime > DateTime.Now)
                .OrderByDescending(v => v.Id)
                .FirstOrDefaultAsync();

            if (vEntry == null) return Json(new { success = false, message = "驗證碼錯誤或已過期" });

            member.PasswordHash = HashPassword(newPassword);
            vEntry.IsUsed = true;
            await _db.SaveChangesAsync();

            return Json(new { success = true, message = "密碼重設成功" });
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

        // POST: 登入（✨已優化：精準身分分流處理）
        [HttpPost]
        // POST: 登入（✨已改為 AJAX JSON 回傳格式）
        [HttpPost]
        public async Task<IActionResult> Login(string account, string password, string role)
        {
            string hashedPassword = HashPassword(password);

            // 1. 如果前端傳來的是求職者身分 (jobseeker)，只去 Members 表查
            if (role == "jobseeker")
            {
                var member = await _db.Members.FirstOrDefaultAsync(m => m.Email == account);

                if (member != null && member.PasswordHash == hashedPassword)
                {
                    // 求職者登入成功，寫入 Session
                    HttpContext.Session.SetInt32("MemberId", member.Id);
                    HttpContext.Session.SetString("MemberName", member.Name ?? "");
                    HttpContext.Session.SetString("MemberRole", member.Role ?? ""); // "jobseeker"

                    // 回傳成功狀態與目標網址
                    return Json(new { success = true, redirectUrl = Url.Action("Index", "Home") });
                }
            }
            // 2. 如果前端傳來的是員工身分 (employee)，只去 Employees 表查
            else if (role == "employee")
            {
                var employee = await _db.Employees.FirstOrDefaultAsync(e => e.Account == account);

                if (employee != null && employee.PasswordHash == hashedPassword)
                {
                    // 員工/HR 登入成功，寫入 Session
                    HttpContext.Session.SetInt32("MemberId", employee.Id);
                    HttpContext.Session.SetString("MemberName", employee.Name ?? "");
                    HttpContext.Session.SetString("MemberRole", employee.Role ); // "hr" 或 "manager"

                    string targetUrl = Url.Action("Index", "Home");

                    // 如果是 hr 或 manager，自動跳轉到 HR 後台的「職缺管理」
                    if (employee.Role == "hr" || employee.Role == "manager")
                    {
                        targetUrl = Url.Action("Index", "HrJob");
                    }

                    return Json(new { success = true, redirectUrl = targetUrl });
                }
            }

            // 3. 若不符合任何登入條件，回傳失敗訊息（前端會拿來跳 alert）
            return Json(new { success = false, message = "帳號、密碼錯誤，或登入身分不符！" });
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