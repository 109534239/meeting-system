using InterviewProject.Data;
using Microsoft.AspNetCore.Mvc;
using System.Security.Cryptography;
using System.Text;

namespace InterviewProject.Controllers
{
    public class EmployeeController : Controller
    {
        private readonly AppDbContext _db;

        public EmployeeController(AppDbContext db)
        {
            _db = db;
        }

        // GET：員工個人資料
        public async Task<IActionResult> Profile()
        {
            var memberId = HttpContext.Session.GetInt32("MemberId");
            if (memberId == null)
                return RedirectToAction("Index", "Login");

            var role = HttpContext.Session.GetString("MemberRole")?.ToLower();
            if (role != "hr" && role != "manager" && role != "director")
                return RedirectToAction("Index", "Home");

            var employee = await _db.Employees.FindAsync(memberId);
            if (employee == null)
                return RedirectToAction("Index", "Login");

            return View(employee);
        }

        // 🎯 POST：變更照片 (接收前端 AJAX 傳來的 Base64 字串，寫入 Employee.ProfileImagePath)
        [HttpPost]
        public async Task<IActionResult> UploadAvatar([FromBody] string? profileImageBase64)
        {
            // 1. 驗證 Session 登入狀態
            var memberId = HttpContext.Session.GetInt32("MemberId");
            if (memberId == null)
            {
                return Json(new { success = false, message = "未登入或連線逾時，請重新登入" });
            }

            // 2. 防呆驗證：檢查字串是否為有效的圖片 Base64 格式
            if (string.IsNullOrEmpty(profileImageBase64) || !profileImageBase64.StartsWith("data:image"))
            {
                return Json(new { success = false, message = "無效的圖片資料，請重新選擇照片" });
            }

            try
            {
                // 3. 根據 Session 的 MemberId 撈取員工資料
                var employee = await _db.Employees.FindAsync(memberId);
                if (employee == null)
                {
                    return Json(new { success = false, message = "找不到該員工資料" });
                }

                // 4. 將 Base64 字串存入 ProfileImagePath 欄位
                employee.ProfileImagePath = profileImageBase64;

                // 5. 儲存變更至資料庫 (Employees 表)
                await _db.SaveChangesAsync();

                return Json(new { success = true, message = "照片更新成功！" });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "照片儲存失敗：" + ex.Message });
            }
        }

        // POST：修改密碼
        [HttpPost]
        public async Task<IActionResult> ChangePassword(string currentPassword,
            string newPassword, string confirmPassword)
        {
            var memberId = HttpContext.Session.GetInt32("MemberId");
            if (memberId == null)
                return RedirectToAction("Index", "Login");

            var employee = await _db.Employees.FindAsync(memberId);
            if (employee == null)
                return NotFound();

            if (employee.PasswordHash != HashPassword(currentPassword))
            {
                TempData["PwError"] = "目前密碼錯誤";
                return RedirectToAction("Profile");
            }

            if (newPassword != confirmPassword)
            {
                TempData["PwError"] = "新密碼與確認密碼不一致";
                return RedirectToAction("Profile");
            }

            if (newPassword.Length < 6)
            {
                TempData["PwError"] = "新密碼至少需要 6 個字元";
                return RedirectToAction("Profile");
            }

            employee.PasswordHash = HashPassword(newPassword);
            await _db.SaveChangesAsync();

            TempData["PwSuccess"] = "密碼修改成功";
            return RedirectToAction("Profile");
        }

        private static string HashPassword(string password)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(password));
            return Convert.ToHexString(bytes).ToLower();
        }
    }
}