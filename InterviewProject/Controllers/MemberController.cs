using DocumentFormat.OpenXml.Spreadsheet;
using InterviewProject.Data;
using InterviewProject.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace InterviewProject.Controllers
{
    public class MemberController : Controller
    {
        private readonly IWebHostEnvironment _env;
        private readonly AppDbContext _db;
        private readonly IConfiguration _config; // 🎯 新增：讀取 appsettings.json 裡 LibreOffice:Path 設定用

        public MemberController(AppDbContext db, IWebHostEnvironment env, IConfiguration config)
        {
            _env = env;
            _db = db;
            _config = config;
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
            // 🎯 修正：分隔符號改成 "; "，跟 ResumeController 的 FormatComputerSkillString()、
            //    UpdateComputerSkills() 存檔切割邏輯，以及 Resume.cshtml 前端 split('; ') 對齊，
            //    否則電腦能力會被當成一整筆文字，無法像專長一樣一筆一列顯示。
            return string.Join("; ", skills.Select(s => s.ComputerSkill));
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

        // 🎯🎯🎯 以下同 ResumeController.cs 的 PDF 匯出輔助函式（履歷表.docx 改版後專用，兩邊維持同一套邏輯）

        /// <summary>把一串文字組成「1. xxx\n2. yyy...」的編號清單字串，沒有資料就顯示「無」。</summary>
        private string BuildNumberedList(IEnumerable<string?> items)
        {
            var list = (items ?? Enumerable.Empty<string?>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x!.Trim())
                .ToList();

            if (!list.Any()) return "無";
            return string.Join("\n", list.Select((x, i) => $"{i + 1}. {x}"));
        }

        /// <summary>駕照種類：只列出使用者實際勾選的類別與細項，例如「自用：小、大」「汽(機)車：無」，一類一行。</summary>
        private string BuildDriverLicenseText(List<DriverLicense> licenses)
        {
            if (licenses == null || !licenses.Any()) return "無";

            var lines = new List<string>();
            string[] order = { "自用", "職業", "機車", "汽(機)車" };
            foreach (var category in order)
            {
                var types = licenses.Where(l => l.Driver == category).Select(l => l.Type).ToList();
                if (types.Any())
                    lines.Add($"{category}：{string.Join("、", types)}");
            }

            return lines.Any() ? string.Join("\n", lines) : "無";
        }

        /// <summary>語文能力：不限筆數，逐筆列出「語言 熟練度」，例如「1. 中文 精通」「2. 英語 良好」。</summary>
        private string BuildLanguageList(List<LanguageProficiency> langs)
        {
            var items = (langs ?? new List<LanguageProficiency>())
                .Select(l => l.Language == "不具外文能力" ? "不具外文能力" : $"{l.Language} {l.Degree}");
            return BuildNumberedList(items);
        }

        /// <summary>證照職類及級別：不限筆數，有級別就加註「　級別：X」，沒有就只顯示名稱。</summary>
        private string BuildCertificateList(List<InterviewProject.Models.Certificates> certs)
        {
            var items = (certs ?? new List<InterviewProject.Models.Certificates>())
                .Select(c => string.IsNullOrWhiteSpace(c.Levels) ? c.CName : $"{c.CName}　級別：{c.Levels}");
            return BuildNumberedList(items);
        }

        /// <summary>使用電腦能力：不限筆數的自由輸入清單（不再是固定 6 個核取方塊）。</summary>
        private string BuildComputerSkillList(List<ComputerSkills> skills)
        {
            var items = (skills ?? new List<ComputerSkills>()).Select(s => s.ComputerSkill);
            return BuildNumberedList(items);
        }

        /// <summary>專長：不限筆數的自由輸入清單（不再是固定 Spec1/Spec2/Spec3 三格）。</summary>
        private string BuildSpecialtyList(string? specialtyString)
        {
            var items = (specialtyString ?? "").Split(new[] { "; " }, StringSplitOptions.RemoveEmptyEntries);
            return BuildNumberedList(items);
        }

        /// <summary>把日期格式化成「2024年7月」這種範本要的樣式，沒填就回傳空字串。</summary>
        private string FormatYearMonth(DateTime? date) => date.HasValue ? $"{date.Value.Year}年{date.Value.Month}月" : "";

        /// <summary>作品集「上傳檔案」欄：FilePath 用「|」存多個相對路徑，這裡拆開只取檔名、一個檔案一行。</summary>
        private string BuildPortfolioFilesText(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return "";
            var names = filePath.Split('|', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => Path.GetFileName(p.Trim()))
                .Where(n => !string.IsNullOrWhiteSpace(n));
            return string.Join("\n", names);
        }

        /// <summary>PDF 檔名裡不能出現路徑分隔符號等字元，這裡把姓名/職稱裡的地雷字元換成底線。</summary>
        private string SanitizeForFileName(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var invalid = Path.GetInvalidFileNameChars();
            var chars = text.Select(c => invalid.Contains(c) || c == '/' || c == '\\' ? '_' : c).ToArray();
            return new string(chars).Trim();
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
        public async Task<IActionResult> ProfileSave(string name, string gender, string idNumber, DateOnly birthday, string address, string phone, string email, string? profileImageBase64)
        {
            var id = HttpContext.Session.GetInt32("MemberId");
            if (id == null) return RedirectToAction("Index", "Login");

            // 後端強烈驗證：只針對純文字輸入框欄位檢查，防範前端漏洞留空
            // 🎯 手機號碼、電子郵件現在開放使用者變更，一併納入必填檢查
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(gender) ||
                string.IsNullOrWhiteSpace(idNumber) || string.IsNullOrWhiteSpace(address) ||
                string.IsNullOrWhiteSpace(phone) || string.IsNullOrWhiteSpace(email))
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

            // 🎯 電子郵件通常是登入帳號/唯一鍵，變更前先檢查其他人是不是已經用了這組信箱（排除自己）
            var emailExists = await _db.Members.AnyAsync(m => m.Email == email.Trim() && m.Id != id);
            if (emailExists)
            {
                TempData["SaveError"] = "該電子郵件已被其他會員使用！";
                return RedirectToAction("Profile");
            }

            // 更新資料欄位
            member.Name = name.Trim();
            member.Gender = gender;
            member.IdNumber = idNumber.Trim().ToUpper();
            member.Birthday = birthday;
            member.Address = address.Trim();
            // 🎯 手機號碼、電子郵件改為可變更，覆蓋寫回資料庫原本的值
            member.Phone = phone.Trim();
            member.Email = email.Trim();

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
        public async Task<IActionResult> Resume(string visitorSort = "desc", string status = "")
        {
            int userId = GetCurrentUserId();
            if (userId == 0) return RedirectToAction("Index", "Login");

            // 1. 基礎查詢：篩選該會員的履歷
            var query = _db.Resumes
                .Include(r => r.Job)
                .Where(r => r.MembersId == userId);

            // 2. 抓取目前資料庫中「該會員所有履歷」出現過的 Status（供前端下拉式選單使用）
            var availableStatuses = await query
                .Where(r => !string.IsNullOrEmpty(r.Status))
                .Select(r => r.Status)
                .Distinct()
                .ToListAsync();

            // 3. 狀態篩選條件
            if (!string.IsNullOrEmpty(status))
            {
                query = query.Where(r => r.Status == status);
            }

            // 4. 投遞時間排序條件
            if (visitorSort == "asc")
            {
                query = query.OrderBy(r => r.ResumeTime);
            }
            else
            {
                query = query.OrderByDescending(r => r.ResumeTime);
            }

            var resumeList = await query.ToListAsync();

            // 5. 透過 ViewBag 將篩選條件與下拉選項傳給 View
            ViewBag.VisitorSort = visitorSort;
            ViewBag.SelectedStatus = status;
            ViewBag.StatusList = availableStatuses;

            return View(resumeList);
        }

        // 顯示單份履歷詳細內容 (第二層)
        public async Task<IActionResult> ResumeDetail(int id)
        {
            int userId = GetCurrentUserId();
            if (userId == 0) return RedirectToAction("Index", "Login");

            var resume = await _db.Resumes
                 .Include(r => r.Job)
                 .Include(r => r.Educations) // 🎯 讀取詳細內容時要一併帶出學歷子表，不然唯讀畫面學歷區塊會是空的
                 .Include(r => r.WorkExperiences) // 🎯 同樣要帶出工作經歷子表
                 .Include(r => r.Portfolios) // 🎯 修正：補上作品集子表的 Include，不然唯讀畫面作品集區塊會是空的
                 .FirstOrDefaultAsync(r => r.Id == id && r.MembersId == userId);

            if (resume == null) return NotFound();

            resume.LanguageSkills = await GetFormattedLanguageSkills(resume.Id);
            resume.ComputerSkills = await GetFormattedComputerSkills(resume.Id);

            // 🎯 修正：Portfolios 需依 SortOrder 排序後重新指派，Include() 帶回來的順序不保證正確
            resume.Portfolios = await _db.Portfolios
                .Where(p => p.ResumeId == resume.Id)
                .OrderBy(p => p.SortOrder)
                .ToListAsync();

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

            // 🎯🎯🎯 2024 改版：範本（履歷表.docx）已經不是固定核取方塊／固定筆數的版面了，
            //    而是「只印出使用者實際填寫/勾選的內容」，且學歷／工作經歷／作品集是可重複列的表格，
            //    語文能力／專長／證照／電腦能力則是不限筆數的編號清單，跟 ResumeController.ExportToPdf() 邏輯一致。
            var dbLangs = await _db.LanguageProficiency.Where(l => l.ResumeId == model.Id).ToListAsync();
            var dbLicenses = await _db.DriverLicense.Where(d => d.ResumeId == model.Id).ToListAsync();
            var dbCompSkills = await _db.ComputerSkills.Where(s => s.ResumeId == model.Id).ToListAsync();
            var dbCertificates = await _db.Certificates.Where(s => s.ResumeId == model.Id).ToListAsync();
            var dbEducations = await _db.Educations.Where(e => e.ResumeId == model.Id).OrderBy(e => e.SortOrder).ToListAsync();
            var dbWorkExperiences = await _db.WorkExperiences.Where(w => w.ResumeId == model.Id).OrderBy(w => w.SortOrder).ToListAsync();
            var dbPortfolios = await _db.Portfolios.Where(p => p.ResumeId == model.Id).OrderBy(p => p.SortOrder).ToListAsync();

            string realName = member.Name ?? "";
            string realGender = member.Gender ?? "";
            string realIdNumber = member.IdNumber ?? "";
            string realBirthday = member.Birthday.ToString("yyyy/MM/dd");
            string realEmail = member.Email ?? "";
            string realAddress = model.ContactAddress ?? member.Address ?? ""; // 🎯 優先用履歷自己存的聯絡地址，跟 ResumeController 對齊

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

            var value = new Dictionary<string, object>()
            {
                ["ApplyJobTitle"] = model.Job?.Title ?? "未指定職缺", // model 已經用 .Include(r => r.Job) 撈出來了

                ["Name"] = realName,
                ["Gender"] = realGender,
                ["IdNumber"] = realIdNumber,
                ["Birthday"] = realBirthday,
                ["Address"] = realAddress,
                ["ProfileImagePath"] = photoData,

                ["MaritalStatus"] = model.MaritalStatus ?? "",
                ["MilitaryService"] = model.MilitaryService ?? "",
                ["Phone1"] = model.Phone1 ?? "",
                ["Email"] = realEmail,

                ["Autobiography"] = model.Autobiography ?? "",

                ["LanguageList"] = BuildLanguageList(dbLangs),
                ["DriverLicenseText"] = BuildDriverLicenseText(dbLicenses),
                // 🎯 專長不要直接讀 model.Specialty，改用你寫好的獨立表查詢方法並 await 它，跟原本的做法一致
                ["SpecialtyList"] = BuildSpecialtyList(await GetFormattedSpecialties(model.Id)),
                ["CertificateList"] = BuildCertificateList(dbCertificates),
                ["ComputerSkillList"] = BuildComputerSkillList(dbCompSkills),
            };

            value["Educations"] = dbEducations.Select(e => new Dictionary<string, object>
            {
                ["EduLevel"] = e.EduLevel ?? "",
                ["SchoolName"] = e.SchoolName ?? "",
                ["Major"] = e.Major ?? "",
                ["EduStatus"] = e.EduStatus ?? "",
                ["StartDate"] = FormatYearMonth(e.StartDate),
                ["EndDate"] = FormatYearMonth(e.EndDate),
            }).ToList();

            value["WorkExperiences"] = dbWorkExperiences.Select(w => new Dictionary<string, object>
            {
                ["CompanyName"] = w.CompanyName ?? "",
                ["JobTitle"] = w.JobTitle ?? "",
                ["JobDescription"] = w.JobDescription ?? "",
                ["StartDate"] = FormatYearMonth(w.StartDate),
                ["EndDate"] = FormatYearMonth(w.EndDate),
            }).ToList();

            value["Portfolios"] = dbPortfolios.Select(p => new Dictionary<string, object>
            {
                ["Title"] = p.Title ?? "",
                ["Description"] = p.Description ?? "",
                ["Link"] = p.Link ?? "",
                ["Files"] = BuildPortfolioFilesText(p.FilePath),
            }).ToList();

            try
            {
                byte[] docxBytes;
                using (var ms = new MemoryStream())
                {
                    MiniSoftware.MiniWord.SaveAsByTemplate(ms, templatePath, value);
                    docxBytes = ms.ToArray();
                }

                // 🎯 改用 LibreOffice headless 轉檔（取代 Spire.Doc 免費版），原因和作法跟
                //    ResumeController.ConvertDocxToPdfAsync() 完全一樣：Spire.Doc 免費版只能輸出前 3 頁，
                //    而且處理儲存格橫向合併（w:gridSpan）時版面會跑掉。
                byte[] pdfBytes = await ConvertDocxToPdfAsync(docxBytes);

                string fileName = $"{SanitizeForFileName(realName)}_{SanitizeForFileName(model.Job?.Title ?? "未指定職缺")}_履歷表.pdf";
                return File(pdfBytes, "application/pdf", fileName);
            }
            catch (Exception ex)
            {
                return Content($"導出 PDF 過程發生錯誤：{ex.Message}");
            }
        }

        // 🎯🎯🎯 用 LibreOffice headless 模式把 Word 檔轉成 PDF，取代 Spire.Doc 免費版。
        //    前提：伺服器要安裝 LibreOffice。
        //    - Windows 預設安裝路徑通常是 C:\Program Files\LibreOffice\program\soffice.exe
        //    - Linux 通常安裝好之後 "soffice" 指令就能直接在 PATH 裡執行
        //    如果安裝路徑不是預設值，可以在 appsettings.json 加一段：
        //      "LibreOffice": { "Path": "你的 soffice 執行檔完整路徑" }
        private async Task<byte[]> ConvertDocxToPdfAsync(byte[] docxBytes)
        {
            string sofficePath = _config["LibreOffice:Path"] ?? GetDefaultSofficePath();

            string workDir = Path.Combine(Path.GetTempPath(), "resume_pdf_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDir);
            string docxPath = Path.Combine(workDir, "resume.docx");
            string pdfPath = Path.Combine(workDir, "resume.pdf");

            try
            {
                await System.IO.File.WriteAllBytesAsync(docxPath, docxBytes);

                var psi = new ProcessStartInfo
                {
                    FileName = sofficePath,
                    Arguments = $"--headless --norestore --convert-to pdf --outdir \"{workDir}\" " +
                                $"-env:UserInstallation=file:///{workDir.Replace('\\', '/')}/.lo_profile \"{docxPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using var process = Process.Start(psi);
                if (process == null)
                    throw new Exception($"無法啟動 LibreOffice（soffice）轉檔程序，請確認伺服器已安裝 LibreOffice，且路徑設定正確：{sofficePath}");

                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(60));
                var exitTask = process.WaitForExitAsync();
                var completed = await Task.WhenAny(exitTask, timeoutTask);

                if (completed == timeoutTask)
                {
                    try { process.Kill(entireProcessTree: true); } catch { /* 忽略終止程序時的例外 */ }
                    throw new Exception("LibreOffice 轉檔逾時（超過 60 秒），請稍後再試一次。");
                }

                if (!System.IO.File.Exists(pdfPath))
                {
                    string stderr = await process.StandardError.ReadToEndAsync();
                    throw new Exception($"LibreOffice 轉檔失敗，找不到輸出的 PDF 檔。錯誤訊息：{stderr}");
                }

                return await System.IO.File.ReadAllBytesAsync(pdfPath);
            }
            finally
            {
                try { Directory.Delete(workDir, recursive: true); } catch { /* 清暫存檔失敗不影響主要流程 */ }
            }
        }

        /// <summary>依作業系統猜測 LibreOffice 的預設安裝路徑；建議還是在 appsettings.json 明確設定 LibreOffice:Path。</summary>
        private string GetDefaultSofficePath()
        {
            if (OperatingSystem.IsWindows())
            {
                string[] candidates =
                {
                    @"C:\Program Files\LibreOffice\program\soffice.exe",
                    @"C:\Program Files (x86)\LibreOffice\program\soffice.exe",
                };
                foreach (var c in candidates)
                {
                    if (System.IO.File.Exists(c)) return c;
                }
                return candidates[0];
            }
            return "soffice";
        }

        public async Task<IActionResult> Application(
    string visitorSort = "desc",
    string status = "",
    string interviewStatus = "",
    string admissionResult = "")
        {
            int userId = GetCurrentUserId();
            if (userId == 0) return RedirectToAction("Index", "Login");

            // 1. 建立基礎查詢：該會員所有的履歷紀錄
            var query = _db.Resumes
                .Include(r => r.Job)
                .Where(r => r.MembersId == userId);

            // 2. 抓取資料庫中出現過的不重複選單選項（供前端下拉選單使用）
            var statusList = await query
                .Where(r => !string.IsNullOrEmpty(r.Status))
                .Select(r => r.Status)
                .Distinct()
                .ToListAsync();

            var interviewStatusList = await query
                .Where(r => !string.IsNullOrEmpty(r.InterviewStatus))
                .Select(r => r.InterviewStatus!)
                .Distinct()
                .ToListAsync();

            var admissionResultList = await query
                .Where(r => !string.IsNullOrEmpty(r.AdmissionResult))
                .Select(r => r.AdmissionResult!)
                .Distinct()
                .ToListAsync();

            // 3. 依據傳入的條件進行動態篩選
            if (!string.IsNullOrEmpty(status))
            {
                query = query.Where(r => r.Status == status);
            }

            if (!string.IsNullOrEmpty(interviewStatus))
            {
                query = query.Where(r => r.InterviewStatus == interviewStatus);
            }

            if (!string.IsNullOrEmpty(admissionResult))
            {
                query = query.Where(r => r.AdmissionResult == admissionResult);
            }

            // 4. 排序條件處理（投遞時間）
            if (visitorSort == "asc")
            {
                query = query.OrderBy(r => r.ResumeTime);
            }
            else
            {
                query = query.OrderByDescending(r => r.ResumeTime);
            }

            var applications = await query.ToListAsync();

            // 5. 房間資料反查（維護原功能）
            var scheduledResumeIds = applications
                .Where(r => r.InterviewStatus == InterviewStatusValues.Scheduled)
                .Select(r => r.Id)
                .ToList();

            var roomsByResumeId = await _db.RoomParticipants
                .Where(p => p.Role == ParticipantRole.Jobseeker
                            && p.ResumeId != null
                            && scheduledResumeIds.Contains(p.ResumeId.Value))
                .Include(p => p.Room)
                .Where(p => p.Room != null)
                .ToDictionaryAsync(p => p.ResumeId!.Value, p => p.Room!);

            ViewBag.RoomsByResumeId = roomsByResumeId;

            // 6. 適性測驗完成狀態檢查（維護原功能）
            var allResumeIds = applications.Select(r => r.Id).ToList();
            var completedTestResumeIds = await _db.AptitudeTestResults
                .Where(t => allResumeIds.Contains(t.ResumeId))
                .Select(t => t.ResumeId)
                .ToListAsync();
            ViewBag.CompletedTestResumeIds = new HashSet<int>(completedTestResumeIds);

            // 7. 將當前選取的條件與選單清單傳遞至 View
            ViewBag.VisitorSort = visitorSort;
            ViewBag.SelectedStatus = status;
            ViewBag.SelectedInterviewStatus = interviewStatus;
            ViewBag.SelectedAdmissionResult = admissionResult;

            ViewBag.StatusList = statusList;
            ViewBag.InterviewStatusList = interviewStatusList;
            ViewBag.AdmissionResultList = admissionResultList;

            return View(applications);
        }

        // 1. 收藏頁面 View
        public async Task<IActionResult> Favorites()
        {
            ViewBag.MemberId = HttpContext.Session.GetInt32("MemberId") ?? 0;

            // 準備下拉選單的選項資料
            ViewBag.Categories = await _db.Jobs.Select(j => j.Department).Distinct().Where(x => x != null).ToListAsync();
            ViewBag.Locations = await _db.Jobs.Select(j => j.Location).Distinct().Where(x => x != null).ToListAsync();
            ViewBag.JobTypes = await _db.Jobs.Select(j => j.JobType).Distinct().Where(x => x != null).ToListAsync();

            return View();
        }

        // 2. 供前端 Fetch 依據 ID 陣列取得職缺列表 API
        [HttpGet]
        public async Task<IActionResult> GetJobsByIds(string ids)
        {
            if (string.IsNullOrEmpty(ids))
            {
                return Json(new object[] { });
            }

            var idList = ids.Split(',')
                            .Select(id => int.TryParse(id, out var parsedId) ? parsedId : (int?)null)
                            .Where(id => id.HasValue)
                            .Select(id => id.Value)
                            .ToList();

            var jobs = await _db.Jobs
                .Where(j => idList.Contains(j.Id))
                .Select(j => new
                {
                    id = j.Id,
                    title = j.Title,
                    department = j.Department,
                    location = j.Location,
                    jobType = j.JobType,
                    experienceRequired = j.ExperienceRequired,
                    educationRequired = j.EducationRequired,
                    deadline = j.Deadline.ToString("yyyy/MM/dd")
                })
                .ToListAsync();

            return Json(jobs);
        }

        private static string HashPassword(string password)
        {
            var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(password));
            return Convert.ToHexString(bytes).ToLower();
        }
    }
}