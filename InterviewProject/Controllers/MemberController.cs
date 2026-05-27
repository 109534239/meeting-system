using InterviewProject.Data;
using InterviewProject.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using System;
using System.Threading.Tasks;
using System.Linq;

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
        public async Task<IActionResult> ProfileSave(string name, string gender, DateOnly birthday, string address)
        {
            // 🎯 1. 調整接收參數，移除 "?" 改為必填
            var id = HttpContext.Session.GetInt32("MemberId");
            if (id == null) return RedirectToAction("Index", "Login");

            // 🎯 2. 後端嚴格驗證：確保參數絕非 Null 或是空字串
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(gender) || string.IsNullOrWhiteSpace(address))
            {
                TempData["SaveError"] = "所有欄位皆為必填，請勿留空。";
                return RedirectToAction("Profile");
            }

            var member = await _db.Members.FindAsync(id);
            if (member == null) return NotFound();

            // 🎯 3. 正常賦值（由於型態安全，此處絕不會存入 null）
            member.Name = name.Trim();
            member.Gender = gender;
            member.Birthday = birthday;
            member.Address = address.Trim();

            await _db.SaveChangesAsync();

            // 更新 Session 裡的姓名
            HttpContext.Session.SetString("MemberName", member.Name);

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

            // 後端密碼必填防護
            if (string.IsNullOrEmpty(currentPassword) || string.IsNullOrEmpty(newPassword) || string.IsNullOrEmpty(confirmPassword))
            {
                TempData["PwError"] = "密碼欄位不可為空";
                return RedirectToAction("Profile");
            }

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

            var resumeList = await _db.Resume
                .Include(r => r.Job)
                .Where(r => r.UserId == userId)
                .OrderByDescending(r => r.ResumeTime)
                .ToListAsync();

            return View(resumeList);
        }

        // 顯示單份履歷詳細內容 (第二層)
        public async Task<IActionResult> ResumeDetail(int id)
        {
            int userId = GetCurrentUserId();
            if (userId == 0) return RedirectToAction("Index", "Login");

            var resume = await _db.Resume
                .Include(r => r.Job)
                .FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId);

            if (resume == null) return NotFound();

            var member = await _db.Members.FirstOrDefaultAsync(m => m.Id == userId);
            if (member != null)
            {
                ViewBag.UserName = member.Name;
                ViewBag.UserGender = member.Gender;
                ViewBag.UserEmail = member.Email;
            }

            ViewBag.IsReadOnly = true;

            return View("~/Views/Resume/Resume.cshtml", resume);
        }

        public async Task<IActionResult> Application()
        {
            int userId = GetCurrentUserId();
            if (userId == 0) return RedirectToAction("Index", "Login");

            var applications = await _db.Resume
                .Include(r => r.Job)
                .Where(r => r.UserId == userId)
                .OrderByDescending(r => r.ResumeTime)
                .ToListAsync();

            return View(applications);
        }

        public IActionResult Favorites()
        {
            ViewBag.MemberId = HttpContext.Session.GetInt32("MemberId") ?? 0;
            return View();
        }

        private static string HashPassword(string password)
        {
            var bytes = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(password));
            return Convert.ToHexString(bytes);
        }
    }
}