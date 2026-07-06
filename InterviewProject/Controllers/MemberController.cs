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
        private readonly IWebHostEnvironment _env;
        private readonly AppDbContext _db;

        public MemberController(AppDbContext db, IWebHostEnvironment env)
        {
            _env = env;
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

        private string FormatCertificatesString(List<InterviewProject.Models.Certificates> dbCerts)
        {
            if (dbCerts == null || !dbCerts.Any()) return "";

            // 將每筆資料組合成 "證照名稱(級別)"，如果沒級別就只留 "證照名稱"
            var certStrings = dbCerts.Select(c =>
                !string.IsNullOrEmpty(c.Levels) ? $"{c.CName.Trim()}({c.Levels.Trim()})" : c.CName.Trim()
            );

            return string.Join(", ", certStrings);
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

            var dbLicenses = await _db.DriverLicense
                .Where(d => d.ResumeId == resume.Id)
                .ToListAsync();
            resume.DriverLicense = FormatDriverLicenseString(dbLicenses);

            resume.Specialty = await GetFormattedSpecialties(resume.Id);

            var dbCertificates = await _db.Certificates
                .Where(c => c.ResumeId == resume.Id)
                .ToListAsync();
            resume.Certificates = FormatCertificatesString(dbCertificates); // 寫入 NotMapped 的暫存屬性

            var member = await _db.Members.FirstOrDefaultAsync(m => m.Id == userId);
            if (member != null)
            {
                ViewBag.UserName = member.Name;
                ViewBag.UserGender = member.Gender;
                ViewBag.UserIdNumber = member.IdNumber;
                ViewBag.UserBirthday = member.Birthday;
                ViewBag.UserAddress = member.Address;
                ViewBag.UserEmail = member.Email;
                ViewBag.UserPhotoBase64 = member?.ProfileImagePath;
            }

            ViewBag.IsReadOnly = true;

            return View("~/Views/Resume/Resume.cshtml", resume);
        }

        // 🎯 修改點 1：改成 HttpGet，讓列表的 <a> 標籤可以直接透過網址傳入 id 呼叫
        [HttpGet]
        public async Task<IActionResult> ExportToPdf(int id)
        {
            int userId = GetCurrentUserId();
            if (userId == 0) return RedirectToAction("Index", "Login");

            // 🎯 修改點 2：直接用傳入的 id，去資料庫撈出該份完整的履歷資料
            var model = await _db.Resumes
                .Include(r => r.Job)
                .FirstOrDefaultAsync(r => r.Id == id && r.MembersId == userId);

            if (model == null) return Content("找不到此履歷資料");

            var member = await _db.Members.FirstOrDefaultAsync(m => m.Id == model.MembersId);
            if (member == null) return Content("找不到會員資料");

            // 以下完全保留你原本寫的超強邏輯與欄位對齊：
            var dbLangs = await _db.LanguageProficiency.Where(l => l.ResumeId == model.Id).ToListAsync();
            var dbLicenses = await _db.DriverLicense.Where(d => d.ResumeId == model.Id).ToListAsync();
            var dbCompSkills = await _db.ComputerSkills.Where(s => s.ResumeId == model.Id).ToListAsync();
            var dbCertificates = await _db.Certificates.Where(s => s.ResumeId == model.Id).ToListAsync();

            string realName = member.Name ?? "";
            string realGender = member.Gender ?? "";
            string realIdNumber = member.IdNumber ?? "";
            string realBirthday = member.Birthday.ToString("yyyy/MM/dd");
            string realEmail = member.Email ?? "";
            string realAddress = member.Address ?? "";

            object photoData = "";
            if (!string.IsNullOrEmpty(member.ProfileImagePath) && member.ProfileImagePath.Contains(","))
            {
                try
                {
                    string extension = "jpg";
                    if (member.ProfileImagePath.Contains("image/png")) extension = "png";
                    else if (member.ProfileImagePath.Contains("image/gif")) extension = "gif";

                    string base64Data = member.ProfileImagePath.Split(',')[1];
                    byte[] imageBytes = Convert.FromBase64String(base64Data);

                    photoData = new MiniSoftware.MiniWordPicture
                    {
                        Bytes = imageBytes,
                        Width = 120,
                        Height = 150,
                        Extension = extension
                    };
                }
                catch { photoData = ""; }
            }

            string templatePath = Path.Combine(_env.WebRootPath, "file", "履歷表.docx");
            if (!System.IO.File.Exists(templatePath)) return Content($"找不到 Word 範本檔案：{templatePath}");

            string ck = "■";
            string un = "□";

            Func<string, string, string> checkLang = (name, degree) => dbLangs.Any(x => x.Language == name && x.Degree == degree) ? ck : un;
            Func<string, string, string> checkLic = (driver, type) => dbLicenses.Any(x => x.Driver == driver && x.Type == type) ? ck : un;
            Func<string, string> checkcomp = (skillName) => dbCompSkills.Any(x => x.ComputerSkill == skillName) ? ck : un;

            string[] knownLangs = { "英語", "日語", "台語", "客語", "不具外文能力" };
            var otherLang = dbLangs.FirstOrDefault(x => !knownLangs.Contains(x.Language));

            // 🎯 【核心修正】：不要直接讀 model.Specialty，請改用你寫好的獨立表查詢方法並 await 它！
            string spec = await GetFormattedSpecialties(model.Id);
            string cert = FormatCertificatesString(dbCertificates);
            string edu = model.EduStatus ?? "";

            var value = new Dictionary<string, object>()
            {
                ["Name"] = realName,
                ["G_M"] = (realGender == "男") ? ck : un,
                ["G_F"] = (realGender == "女") ? ck : un,
                ["IdNumber"] = realIdNumber,
                ["Birthday"] = realBirthday,
                ["Address"] = realAddress,
                ["ProfileImagePath"] = photoData,

                ["M_M"] = (model.MaritalStatus == "已婚") ? ck : un,
                ["M_S"] = (model.MaritalStatus == "單身") ? ck : un,
                ["MS_1"] = (model.MilitaryService == "免役") ? ck : un,
                ["MS_2"] = (model.MilitaryService == "役畢") ? ck : un,
                ["MS_3"] = (model.MilitaryService == "未役") ? ck : un,
                ["MS_4"] = (model.MilitaryService == "待役中") ? ck : un,
                ["Phone1"] = model.Phone1 ?? "",
                ["Phone2"] = model.Phone2 ?? "",
                ["Mobile"] = model.Mobile ?? "",
                ["Email"] = realEmail,

                ["E_Dr"] = (model.EduLevel == "博士") ? ck : un,
                ["E_Ms"] = (model.EduLevel == "碩士") ? ck : un,
                ["E_Uni"] = (model.EduLevel == "大學") ? ck : un,
                ["E_Col"] = (model.EduLevel == "專科") ? ck : un,
                ["E_Voc"] = (model.EduLevel == "高職") ? ck : un,
                ["E_High"] = (model.EduLevel == "高中") ? ck : un,
                ["E_Jun"] = (model.EduLevel == "國中") ? ck : un,
                ["E_Pri"] = (model.EduLevel == "國小") ? ck : un,
                ["E_Other"] = (!string.IsNullOrEmpty(model.EduLevel) && !new[] { "博士", "碩士", "大學", "專科", "高職", "高中", "國中", "國小" }.Contains(model.EduLevel)) ? ck : un,
                ["OtherEdu"] = model.EduLevel ?? "______",

                ["SchoolName"] = model.SchoolName ?? "",
                ["Major"] = model.Major ?? "",
                ["E_Grad"] = edu.Contains("畢業") ? ck : un,
                ["E_Under"] = edu.Contains("肄業") ? ck : un,
                ["E_Stud"] = edu.Contains("在學") ? ck : un,

                ["EduDate"] = model.EduDate?.ToString("yyyy/MM") ?? "",

                ["WorkExp"] = (model.WorkExperienceYears >= 1) ? ck : un,
                ["NoWorkExp"] = (model.WorkExperienceYears == 0) ? ck : un,
                ["WorkExperienceYears"] = (model.WorkExperienceYears == -1) ? "0" : model.WorkExperienceYears.ToString(),

                ["CompanyName"] = model.CompanyName ?? "",
                ["JobTitle"] = model.JobTitle ?? "",
                ["JobDescription"] = model.JobDescription ?? "",
                ["Autobiography"] = model.Autobiography ?? "",

                ["L_None"] = dbLangs.Any(x => x.Language == "不具外文能力") ? ck : un,
                ["L_Eng_1"] = checkLang("英語", "精通"),
                ["L_Eng_2"] = checkLang("英語", "良好"),
                ["L_Eng_3"] = checkLang("英語", "普通"),
                ["L_Eng_4"] = checkLang("英語", "稍懂"),

                ["L_Jap_1"] = checkLang("日語", "精通"),
                ["L_Jap_2"] = checkLang("日語", "良好"),
                ["L_Jap_3"] = checkLang("日語", "普通"),
                ["L_Jap_4"] = checkLang("日語", "稍懂"),

                ["L_Twn_1"] = checkLang("台語", "精通"),
                ["L_Twn_2"] = checkLang("台語", "良好"),
                ["L_Twn_3"] = checkLang("台語", "普通"),
                ["L_Twn_4"] = checkLang("台語", "稍懂"),

                ["L_Hakka_1"] = checkLang("客語", "精通"),
                ["L_Hakka_2"] = checkLang("客語", "良好"),
                ["L_Hakka_3"] = checkLang("客語", "普通"),
                ["L_Hakka_4"] = checkLang("客語", "稍懂"),

                ["L_Other_Name"] = otherLang?.Language ?? "",
                ["L_Other_1"] = (otherLang?.Degree == "精通") ? ck : un,
                ["L_Other_2"] = (otherLang?.Degree == "良好") ? ck : un,
                ["L_Other_3"] = (otherLang?.Degree == "普通") ? ck : un,
                ["L_Other_4"] = (otherLang?.Degree == "稍懂") ? ck : un,

                ["D_Self_S"] = checkLic("自用", "小"),
                ["D_Self_B"] = checkLic("自用", "大"),
                ["D_Pro_S"] = checkLic("職業", "小"),
                ["D_Pro_B"] = checkLic("職業", "大"),
                ["D_Pro_K"] = checkLic("職業", "客"),
                ["D_Moto_L"] = checkLic("機車", "輕型"),
                ["D_Moto_H"] = checkLic("機車", "重型"),
                ["D_None"] = checkLic("汽(機)車", "無"),
                ["D_Own"] = checkLic("汽(機)車", "自備"),

                // 🎯 這裡會成功切開由資料庫撈回並用 "; " 串接的專長字串
                ["Spec1"] = spec.Split(new[] { "; " }, StringSplitOptions.None).ElementAtOrDefault(0) ?? "",
                ["Spec2"] = spec.Split(new[] { "; " }, StringSplitOptions.None).ElementAtOrDefault(1) ?? "",
                ["Spec3"] = spec.Split(new[] { "; " }, StringSplitOptions.None).ElementAtOrDefault(2) ?? "",

                ["Cert"] = cert,

                ["C_Base"] = checkcomp("電腦基本操作"),
                ["C_Doc"] = checkcomp("文書處理"),
                ["C_Net"] = checkcomp("網際網路"),
                ["C_Web"] = checkcomp("網頁編輯"),
                ["C_Biz"] = checkcomp("商業軟體"),
                ["C_Prog"] = checkcomp("程式設計"),
            };

            var certList = cert.Split(", ").ToList();
            for (int i = 1; i <= 3; i++)
            {
                var currentCert = dbCertificates.ElementAtOrDefault(i - 1);

                if (currentCert != null)
                {
                    value[$"C{i}_Name"] = currentCert.CName ?? "";
                    value[$"C{i}_A"] = (currentCert.Levels == "甲級" || currentCert.Levels == "甲") ? ck : un;
                    value[$"C{i}_B"] = (currentCert.Levels == "乙級" || currentCert.Levels == "乙") ? ck : un;
                    value[$"C{i}_C"] = (currentCert.Levels == "丙級" || currentCert.Levels == "丙") ? ck : un;
                    value[$"C{i}_S"] = (currentCert.Levels == "單一級") ? ck : un;
                }
                else
                {
                    value[$"C{i}_Name"] = "";
                    value[$"C{i}_A"] = un;
                    value[$"C{i}_B"] = un;
                    value[$"C{i}_C"] = un;
                    value[$"C{i}_S"] = un;
                }
            }

            var otherComp = dbCompSkills.FirstOrDefault(s => s.ComputerSkill.StartsWith("其他:"));
            if (otherComp != null)
            {
                value["C_Other"] = ck;
                value["C_Other_Text"] = otherComp.ComputerSkill.Replace("其他:", "").Trim();
            }
            else
            {
                value["C_Other"] = un;
                value["C_Other_Text"] = "";
            }

            try
            {
                using (var ms = new MemoryStream())
                {
                    MiniSoftware.MiniWord.SaveAsByTemplate(ms, templatePath, value);
                    ms.Position = 0;

                    Spire.Doc.Document doc = new Spire.Doc.Document();
                    doc.LoadFromStream(ms, Spire.Doc.FileFormat.Docx);

                    using (MemoryStream pdfStream = new MemoryStream())
                    {
                        doc.SaveToStream(pdfStream, Spire.Doc.FileFormat.PDF);
                        return File(pdfStream.ToArray(), "application/pdf", $"{realName}_履歷表.pdf");
                    }
                }
            }
            catch (Exception ex)
            {
                return Content($"導出 PDF 過程發生錯誤：{ex.Message}");
            }
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