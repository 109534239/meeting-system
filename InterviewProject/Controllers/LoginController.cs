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

        // 🎯 供註冊前端第一步（Mail、手機）與第二步（身分證）點擊時，異步阻擋重複資料使用
        [HttpPost]
        public async Task<IActionResult> CheckDuplicate(string? email, string? phone, string? idNumber)
        {
            if (!string.IsNullOrWhiteSpace(email))
            {
                var emailExists = await _db.Members.AnyAsync(m => m.Email == email.Trim());
                if (emailExists) return Json(new { isDuplicate = true, message = "此電子郵件已被註冊！" });
            }

            if (!string.IsNullOrWhiteSpace(phone))
            {
                var phoneExists = await _db.Members.AnyAsync(m => m.Phone == phone.Trim());
                if (phoneExists) return Json(new { isDuplicate = true, message = "此手機號碼已被註冊！" });
            }

            if (!string.IsNullOrWhiteSpace(idNumber))
            {
                var idExists = await _db.Members.AnyAsync(m => m.IdNumber == idNumber.Trim().ToUpper());
                if (idExists) return Json(new { isDuplicate = true, message = "此身分證字號已被註冊！" });
            }

            return Json(new { isDuplicate = false });
        }

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

        private async Task SendEmailAsync(string toEmail, string code)
        {
            string fromEmail = "angela296123@gmail.com";
            string appPassword = "zgpj hcew cyqc qxiy";
            string companyName = "XXX公司";

            using (var smtpClient = new SmtpClient("smtp.gmail.com", 587))
            {
                smtpClient.UseDefaultCredentials = false;
                smtpClient.Credentials = new NetworkCredential(fromEmail, appPassword);
                smtpClient.EnableSsl = true;
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

        // POST: 註冊（🎯 修正點：接收圖片檔案並將其轉換成 Base64 字串存入資料庫）
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Register(string email, string phone, string password,
            string name, string gender, string idNumber, IFormFile? profileImage, DateOnly birthday, string address)
        {
            // 1. 後端必填防線：包含新加的 idNumber 與 profileImage
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(phone) ||
                string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(name) ||
                string.IsNullOrWhiteSpace(gender) || string.IsNullOrWhiteSpace(idNumber) ||
                profileImage == null || profileImage.Length == 0 ||
                string.IsNullOrWhiteSpace(address))
            {
                TempData["RegisterError"] = "所有欄位皆為必填項目，且必須上傳大頭照！";
                return RedirectToAction("Register");
            }

            // 2. 檢查重複
            if (await _db.Members.AnyAsync(m => m.Email == email.Trim()))
            {
                TempData["RegisterError"] = "此 Email 已被註冊";
                return RedirectToAction("Register");
            }

            // 🎯 核心變更：將上傳的實體圖片轉為 Base64 資料字串
            string base64ImageString = "";
            try
            {
                using (var ms = new MemoryStream())
                {
                    await profileImage.CopyToAsync(ms);
                    byte[] fileBytes = ms.ToArray();

                    // 串接成瀏覽器可以直接解析的資料格式標準：data:[MIME型態];base64,[資料碼]
                    base64ImageString = $"data:{profileImage.ContentType};base64,{Convert.ToBase64String(fileBytes)}";
                }
            }
            catch (Exception)
            {
                TempData["RegisterError"] = "大頭照轉換失敗，請重新選擇相片再試。";
                return RedirectToAction("Register");
            }

            // 3. 實例化物件：將完整的 Base64 字串指派給 ProfileImagePath 屬性
            var member = new Member
            {
                Email = email.Trim(),
                Phone = phone.Trim(),
                PasswordHash = HashPassword(password),
                Name = name.Trim(),
                Gender = gender,
                IdNumber = idNumber.Trim().ToUpper(),
                ProfileImagePath = base64ImageString, // 🎯 這裡改存一長串圖片編碼
                Birthday = birthday,
                Address = address.Trim(),
                CreatedAt = DateTime.UtcNow
            };

            _db.Members.Add(member);
            await _db.SaveChangesAsync();

            return RedirectToAction("Index");
        }

        // POST: 登入
        [HttpPost]
        public async Task<IActionResult> Login(string account, string password, string role)
        {
            string hashedPassword = HashPassword(password);

            if (role == "jobseeker")
            {
                var member = await _db.Members.FirstOrDefaultAsync(m => m.Email == account);

                if (member != null && member.PasswordHash == hashedPassword)
                {
                    HttpContext.Session.SetInt32("MemberId", member.Id);
                    HttpContext.Session.SetString("MemberName", member.Name ?? "");
                    HttpContext.Session.SetString("MemberRole", "jobseeker");

                    return Json(new { success = true, redirectUrl = Url.Action("Index", "Home") });
                }
            }
            else if (role == "employee")
            {
                var employee = await _db.Employees.FirstOrDefaultAsync(e => e.Account == account);

                if (employee != null && employee.PasswordHash == hashedPassword)
                {
                    string cleanRole = employee.Role?.ToLower() ?? "";

                    HttpContext.Session.SetInt32("MemberId", employee.Id);
                    HttpContext.Session.SetString("MemberName", employee.Name ?? "");
                    HttpContext.Session.SetString("MemberRole", cleanRole);

                    string targetUrl = Url.Action("Index", "Home") ?? "/";

                    // 🎯 HR 有職缺管理權限，所以登入後導向職缺管理頁
                    if (cleanRole == "hr")
                    {
                        targetUrl = Url.Action("Index", "HrJob") ?? "/";
                    }
                    // 🎯 Manager / Director 沒有職缺管理權限，但可以進入招募數據頁
                    else if (cleanRole == "manager" || cleanRole == "director")
                    {
                        targetUrl = Url.Action("Dashboard", "AdminHome") ?? "/";
                    }
                    // 🎯 其他 employee 角色暫時維持原本首頁導向
                    else if (cleanRole == "employee")
                    {
                        targetUrl = Url.Action("Index", "Home") ?? "/";
                    }

                    return Json(new { success = true, redirectUrl = targetUrl });
                }
            }

            return Json(new { success = false, message = "帳號、密碼錯誤，或登入身分不符！" });
        }

        public IActionResult Logout()
        {
            HttpContext.Session.Clear();
            return RedirectToAction("Index");
        }

        public IActionResult LogoLogout()
        {
            HttpContext.Session.Clear();
            return RedirectToAction("Index", "Home");
        }

        private static string HashPassword(string password)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(password));
            return Convert.ToHexString(bytes).ToLower();
        }
    }
}