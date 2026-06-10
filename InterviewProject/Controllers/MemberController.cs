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

        // 🎯語言能力
        private string FormatLanguageString(List<LanguageProficiency> langs)
        {
            if (langs == null || !langs.Any()) return "";
            return string.Join(", ", langs.Select(l =>
                l.Language == "不具外文能力" ? l.Language : $"{l.Language}({l.Degree})"));
        }
        
        private async Task<string> GetFormattedLanguageSkills(int resumeId)
        {
            var langs = await _db.LanguageProficiency
                .Where(l => l.ResumeId == resumeId)
                .ToListAsync();
            return FormatLanguageString(langs);
        }

        // 🎯駕照
        private string FormatDriverLicenseString(List<DriverLicense> licenses)
        {
            if (licenses == null || !licenses.Any()) return "";

            var result = new List<string>();

            // 按 Driver 分組 (自用、職業、機車)
            var grouped = licenses.Where(l => l.Driver != "汽(機)車")
                                  .GroupBy(l => l.Driver);

            foreach (var g in grouped)
            {
                result.Add($"{g.Key}({string.Join("/", g.Select(x => x.Type))})");
            }

            // 處理汽(機)車 (無、自備)
            var status = licenses.Where(l => l.Driver == "汽(機)車").Select(x => x.Type);
            if (status.Any())
            {
                result.Add(string.Join("/", status));
            }

            return string.Join(", ", result);
        }



        // 🎯電腦能力方法 A：負責「把 List 變成字串」供前端 hidden 欄位與匯出邏輯使用
        private string FormatComputerSkillString(List<ComputerSkills> skills)
        {
            if (skills == null || !skills.Any()) return "";
            // 直接取出 ComputerSkill 欄位的文字並用逗號隔開
            return string.Join(", ", skills.Select(s => s.ComputerSkill));
        }

        // 🎯電腦能力方法 B：負責「去資料庫抓資料並呼叫方法 A」
        private async Task<string> GetFormattedComputerSkills(int resumeId)
        {
            var skills = await _db.ComputerSkills
                .Where(s => s.ResumeId == resumeId)
                .ToListAsync();
            return FormatComputerSkillString(skills);
        }

        // 🎯專長方法 A：負責「把專長 List 變成用分號相隔的字串」供前端反填
        private string FormatSpecialtyString(List<Specialties> specs)
        {
            if (specs == null || !specs.Any()) return "";
            // 依排序撈出 Specialty，並用分號與空格串接 "; "
            return string.Join("; ", specs.OrderBy(s => s.SortOrder).Select(s => s.Specialty));
        }

        // 🎯專長方法 B：負責「去 Specialties 資料表抓 ResumeId 對應的資料，再丟給 A」
        private async Task<string> GetFormattedSpecialties(int resumeId)
        {
            var specs = await _db.Specialties
                .Where(s => s.ResumeId == resumeId)
                .ToListAsync();
            return FormatSpecialtyString(specs);
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

        // POST: 儲存基本資料
        [HttpPost]
        public async Task<IActionResult> ProfileSave(string name, string gender, string idNumber, DateOnly birthday, string address, string? profileImageBase64)
        {
            var id = HttpContext.Session.GetInt32("MemberId");
            if (id == null) return RedirectToAction("Index", "Login");

            // 後端強烈驗證：只針對純文字輸入框欄位檢查，防範前端漏洞留空
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

            // 更新資料欄位
            member.Name = name.Trim();
            member.Gender = gender;
            member.IdNumber = idNumber.Trim().ToUpper();
            member.Birthday = birthday;
            member.Address = address.Trim();

            // 🎯 核心防呆處理：只有當前端傳送過來的 Base64 為有效圖片資料時，才覆蓋寫入資料庫
            if (!string.IsNullOrEmpty(profileImageBase64) && profileImageBase64.StartsWith("data:image"))
            {
                member.ProfileImagePath = profileImageBase64;
            }

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

            resume.LanguageSkills = await GetFormattedLanguageSkills(resume.Id);
            resume.ComputerSkills = await GetFormattedComputerSkills(resume.Id);

            // 🎯 修正 2：抓取並格式化駕照資料 (原本漏掉這段)
            var dbLicenses = await _db.DriverLicense
                .Where(d => d.ResumeId == resume.Id)
                .ToListAsync();
            resume.DriverLicense = FormatDriverLicenseString(dbLicenses);

            // 🎯 這裡加上去 Specialties 抓取並格式化專長資料
            resume.Specialty = await GetFormattedSpecialties(resume.Id);
            
            var member = await _db.Members.FirstOrDefaultAsync(m => m.Id == userId);
            if (member != null)
            {
                ViewBag.UserName = member.Name;
                ViewBag.UserGender = member.Gender;
                ViewBag.UserIdNumber = member.IdNumber;
                ViewBag.UserBirthday = member.Birthday;
                ViewBag.UserAddress = member.Address;
                ViewBag.UserEmail = member.Email;
                // 照片
                ViewBag.UserPhotoBase64 = member?.ProfileImagePath;
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
                //.Include(r => r.Interview) // 🎯 關鍵：把面試資料表也 Join 進來！
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