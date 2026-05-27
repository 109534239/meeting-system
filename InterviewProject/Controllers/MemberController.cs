using InterviewProject.Data;
using InterviewProject.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using System;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Generic;

namespace InterviewProject.Controllers
{
    public class MemberController : Controller
    {
        private readonly AppDbContext _db;

        public MemberController(AppDbContext db)
        {
            _db = db;
        }

        private int GetCurrentUserId()
        {
            return HttpContext.Session.GetInt32("MemberId") ?? 0;
        }

        // --- 🎯 新增的語文處理輔助方法 (從 ResumeController 拷貝過來) ---
        // 方法 A：把資料表 List 轉成字串
        private string FormatLanguageString(List<LanguageProficiency> langs)
        {
            if (langs == null || !langs.Any()) return "";
            return string.Join(", ", langs.Select(l =>
                l.Language == "不具外文能力" ? l.Language : $"{l.Language}({l.Degree})"));
        }

        // 方法 B：去資料庫抓資料並呼叫方法 A
        private async Task<string> GetFormattedLanguageSkills(int resumeId)
        {
            var langs = await _db.LanguageProficiency
                .Where(l => l.ResumeId == resumeId)
                .ToListAsync();
            return FormatLanguageString(langs);
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

        // POST: 儲存基本資料（🎯 已完全納入必填 IdNumber）
        [HttpPost]
        public async Task<IActionResult> ProfileSave(string name, string gender, string idNumber, DateOnly birthday, string address)
        {
            var id = HttpContext.Session.GetInt32("MemberId");
            if (id == null) return RedirectToAction("Index", "Login");

            // 後端嚴格驗證：防堵任何惡意繞過前端而傳入空字串的情況
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(gender) ||
                string.IsNullOrWhiteSpace(idNumber) || string.IsNullOrWhiteSpace(address))
            {
                TempData["SaveError"] = "所有欄位皆為必填，請勿留空。";
                return RedirectToAction("Profile");
            }

            var member = await _db.Members.FindAsync(id);
            if (member == null) return NotFound();

            // 檢查其他人是不是已經用了這組身分證字號（排除自己）
            var idExists = await _db.Members.AnyAsync(m => m.IdNumber == idNumber.Trim().ToUpper() && m.Id != id);
            if (idExists)
            {
                TempData["SaveError"] = "該身分證字號已被其他會員使用！";
                return RedirectToAction("Profile");
            }

            // 更新資料
            member.Name = name.Trim();
            member.Gender = gender;
            member.IdNumber = idNumber.Trim().ToUpper();
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
        public async Task<IActionResult> ChangePassword(string currentPassword, string newPassword, string confirmPassword)
        {
            var id = HttpContext.Session.GetInt32("MemberId");
            if (id == null) return RedirectToAction("Index", "Login");

            var member = await _db.Members.FindAsync(id);
            if (member == null) return NotFound();

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

            var resumeList = await _db.Resumes
                .Include(r => r.Job)
                .Where(r => r.MembersId == userId)
                .OrderByDescending(r => r.ResumeTime)
                .ToListAsync();

            return View(resumeList);
        }

        // 顯示單份履歷詳細內容 (第二層)
        public async Task<IActionResult> ResumeDetail(int id)
        {
            int userId = GetCurrentUserId();
            if (userId == 0) return RedirectToAction("Index", "Login");

            var resume = await _db.Resumes
                .Include(r => r.Job)
                .FirstOrDefaultAsync(r => r.Id == id && r.MembersId == userId);

            if (resume == null) return NotFound();

            // 🎯 這裡現在有輔助方法支撐了，可以正確抓取副表語言
            resume.LanguageSkills = await GetFormattedLanguageSkills(resume.Id);

            var member = await _db.Members.FirstOrDefaultAsync(m => m.Id == userId);
            if (member != null)
            {
                ViewBag.UserName = member.Name;
                ViewBag.UserGender = member.Gender;
                ViewBag.UserIdNumber = member.IdNumber;
                ViewBag.UserBirthday = member.Birthday;
                ViewBag.UserAddress = member.Address;
                ViewBag.UserEmail = member.Email;
            }

            ViewBag.IsReadOnly = true;

            return View("~/Views/Resume/Resume.cshtml", resume);
        }

        public async Task<IActionResult> Application()
        {
            int userId = GetCurrentUserId();
            if (userId == 0) return RedirectToAction("Index", "Login");

            var applications = await _db.Resumes
                .Include(r => r.Job)
                .Where(r => r.MembersId == userId)
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
            var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(password));
            return Convert.ToHexString(bytes).ToLower();
        }
    }
}