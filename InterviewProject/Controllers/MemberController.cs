using InterviewProject.Data;
using InterviewProject.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace InterviewProject.Controllers
{
    public class MemberController : Controller
    {
        private readonly AppDbContext _db;

        public MemberController(AppDbContext db)
        {
            _db = db;
        }

        // 輔助方法：取得當前登入者 ID
        private int GetCurrentUserId()
        {
            return HttpContext.Session.GetInt32("MemberId") ?? 0;
        }

        // GET: 基本資料
        public async Task<IActionResult> Profile()
        {
            var id = HttpContext.Session.GetInt32("MemberId");
            if (id == null) return RedirectToAction("Index", "Login");

            var member = await _db.Members.FindAsync(id);
            if (member == null) return RedirectToAction("Index", "Login");

            return View(member);
        }

        // POST: 儲存基本資料（只允許修改姓名、性別、生日、地址）
        [HttpPost]
        public async Task<IActionResult> ProfileSave(string name, string? gender,
            DateOnly? birthday, string? address)
        {
            var id = HttpContext.Session.GetInt32("MemberId");
            if (id == null) return RedirectToAction("Index", "Login");

            var member = await _db.Members.FindAsync(id);
            if (member == null) return NotFound();

            member.Name    = name;
            member.Gender  = gender;
            member.Birthday = birthday;
            member.Address = address;

            await _db.SaveChangesAsync();

            // 更新 Session 裡的姓名
            HttpContext.Session.SetString("MemberName", name);

            TempData["SaveSuccess"] = "true";
            return RedirectToAction("Profile");
        }

        // POST: 修改密碼
        [HttpPost]
        public async Task<IActionResult> ChangePassword(string currentPassword,
            string newPassword, string confirmPassword)
        {
            var id = HttpContext.Session.GetInt32("MemberId");
            if (id == null) return RedirectToAction("Index", "Login");

            var member = await _db.Members.FindAsync(id);
            if (member == null) return NotFound();

            if (member.PasswordHash != HashPassword(currentPassword))
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

            member.PasswordHash = HashPassword(newPassword);
            await _db.SaveChangesAsync();

            TempData["PwSuccess"] = "true";
            return RedirectToAction("Profile");
        }

        // 顯示履歷列表 (第一層)
        public async Task<IActionResult> Resume()
        {
            int userId = GetCurrentUserId();
            if (userId == 0) return RedirectToAction("Index", "Login");

            // 修正：將 _context 改為 _db
            var resumeList = await _db.Resume
                .Where(r => r.UserId == userId)
                .OrderByDescending(r => r.ResumeTime)
                .ToListAsync();

            return View(resumeList);
        }

        // 顯示單份履歷詳細內容 (第二層)
        public async Task<IActionResult> ResumeDetail(int id)
        {
            // 1. 取得當前登入者 ID
            int userId = GetCurrentUserId();
            if (userId == 0) return RedirectToAction("Index", "Login");

            // 2. 抓取這份履歷資料
            var resume = await _db.Resume.FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId);
            if (resume == null) return NotFound();

            // 3. 抓取 Member 基本資料 (為了讓 Resume.cshtml 顯示姓名、性別、Email)
            var member = await _db.Members.FirstOrDefaultAsync(m => m.Id == userId);
            if (member != null)
            {
                ViewBag.UserName = member.Name;
                ViewBag.UserGender = member.Gender;
                ViewBag.UserEmail = member.Email;
            }

            // 4. 設定為唯讀模式標記
            ViewBag.IsReadOnly = true;

            // 5. 回傳 ResumeController 下的 Resume 檢視頁面
            // 注意：路徑必須寫完整路徑 "~/Views/Resume/Resume.cshtml" 才能跨目錄讀取
            return View("~/Views/Resume/Resume.cshtml", resume);
        }

        public async Task<IActionResult> Application()
        {
            int userId = GetCurrentUserId();
            if (userId == 0) return RedirectToAction("Index", "Login");

            // 抓取該使用者的所有履歷，按時間倒序排列
            var applications = await _db.Resume
                .Where(r => r.UserId == userId)
                .OrderByDescending(r => r.ResumeTime)
                .ToListAsync();

            return View(applications);
        }

        public IActionResult Favorites()   => View();

        private static string HashPassword(string password)
        {
            var bytes = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(password));
            return Convert.ToHexString(bytes);
        }
    }
}