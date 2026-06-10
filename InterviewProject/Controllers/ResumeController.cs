using DocumentFormat.OpenXml.Spreadsheet;
using InterviewProject.Data;
using InterviewProject.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using MiniSoftware;
using Spire.Doc;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace InterviewProject.Controllers
{
    public class ResumeController : Controller
    {
        // 🎯 您的付費版無限速超級金鑰（RWlA 正式付費帳戶通道）
        private const string GeminiApiKey = "";

        private readonly IWebHostEnvironment _env;
        private readonly AppDbContext _db;
        private readonly IConfiguration _config;
        private readonly IHttpClientFactory _httpClientFactory;

        public ResumeController(IWebHostEnvironment env, AppDbContext context, IConfiguration configuration, IHttpClientFactory httpClientFactory)
        {
            _env = env;
            _db = context;
            _config = configuration;
            _httpClientFactory = httpClientFactory;
        }

        private int GetCurrentUserId()
        {
            int? userId = HttpContext.Session.GetInt32("MemberId");
            return userId ?? 0;
        }

        [HttpGet]
        public async Task<IActionResult> GetSavedPositions()
        {
            int userId = GetCurrentUserId();
            if (userId == 0) return Json(new List<object>());

            var positions = await _db.Resumes
                .Where(r => r.MembersId == userId)
                .Include(r => r.Job)
                .Select(r => new
                {
                    id = r.JobsId,
                    title = r.Job != null ? r.Job.Title : "未知職缺"
                })
                .Distinct()
                .ToListAsync();

            return Json(positions);
        }

        public async Task<IActionResult> Resume(int jobId, int? fromJobId = null, string mode = "")
        {
            int userId = GetCurrentUserId();
            if (userId == 0) return RedirectToAction("Index", "Login");

            // 🎯【進頁面驗證】：確保進來時 100% 撈得到目標職缺，讓頁面清楚知道現在是應徵哪份工作
            var targetJob = await _db.Jobs.FindAsync(jobId);
            if (targetJob == null) return Content("找不到目標職缺");

            Resume model = null;

            if (mode == "apply" && fromJobId.HasValue)
            {
                var existingResume = await _db.Resumes
                    .AsNoTracking()
                    .FirstOrDefaultAsync(r => r.MembersId == userId && r.JobsId == fromJobId.Value);

                if (existingResume != null)
                {
                    var sourceLangs = await _db.LanguageProficiency.Where(l => l.ResumeId == existingResume.Id).ToListAsync();
                    var dbLicenses = await _db.DriverLicense.Where(d => d.ResumeId == existingResume.Id).ToListAsync();
                    var dbCompSkills = await _db.ComputerSkills.Where(s => s.ResumeId == existingResume.Id).ToListAsync();

                    model = existingResume;
                    model.LanguageSkills = FormatLanguageString(sourceLangs);
                    model.DriverLicense = FormatDriverLicenseString(dbLicenses);
                    model.ComputerSkills = FormatComputerSkillString(dbCompSkills);

                    model.Id = 0;
                    model.JobsId = jobId;
                    model.Job = targetJob;
                    model.Status = "待審核";
                }
            }

            if (model == null)
            {
                model = await _db.Resumes
                    .Include(r => r.Job)
                    .FirstOrDefaultAsync(r => r.MembersId == userId && r.JobsId == jobId);

                if (model == null)
                {
                    model = new Resume { MembersId = userId, JobsId = jobId, WorkExperienceYears = -1, Job = targetJob };
                }
                else
                {
                    var langs = await _db.LanguageProficiency.Where(l => l.ResumeId == model.Id).ToListAsync();
                    model.LanguageSkills = FormatLanguageString(langs);
                    var dbLicenses = await _db.DriverLicense.Where(d => d.ResumeId == model.Id).ToListAsync();
                    model.DriverLicense = FormatDriverLicenseString(dbLicenses);
                    var dbCompSkills = await _db.ComputerSkills.Where(s => s.ResumeId == model.Id).ToListAsync();
                    model.ComputerSkills = FormatComputerSkillString(dbCompSkills);
                }
            }

            var member = await _db.Members.FindAsync(userId);
            ViewBag.UserName = member?.Name;
            ViewBag.UserGender = member?.Gender;
            ViewBag.UserIdNumber = member?.IdNumber;
            ViewBag.UserBirthday = member?.Birthday;
            ViewBag.UserAddress = member?.Address;
            ViewBag.UserEmail = member?.Email;
            ViewBag.UserPhotoBase64 = member?.ProfileImagePath;

            return View(model);
        }

        [HttpPost]
        public async Task<IActionResult> SaveResume(Resume model)
        {
            ModelState.Remove("ResumeTime");
            ModelState.Remove("Job");
            ModelState.Remove("Status");

            int userId = GetCurrentUserId();
            model.MembersId = userId;

            // 🎯【安全性防禦】：如果前端忘記加隱藏欄位導致 JobsId 為 0，主動拦截阻擋
            if (model.JobsId <= 0)
            {
                TempData["ApiError"] = "❌ 系統錯誤：未接收到職缺編號(JobsId)，請確認表單中是否包含職缺隱藏欄位。";
                model.Job = await _db.Jobs.FindAsync(model.JobsId);
                return View("Resume", model);
            }

            if (!ModelState.IsValid)
            {
                TempData["ApiError"] = "❌ 填寫欄位格式不正確或有必填未填（如自傳），請檢查後再試一次。";
                model.Job = await _db.Jobs.FindAsync(model.JobsId);
                return View("Resume", model);
            }

            using (var transaction = await _db.Database.BeginTransactionAsync())
            {
                try
                {
                    var existing = await _db.Resumes
                        .FirstOrDefaultAsync(r => r.MembersId == userId && r.JobsId == model.JobsId);

                    DateTime now = DateTime.Now;
                    int finalResumeId;

                    if (existing == null)
                    {
                        model.ResumeTime = now;
                        model.Status = "待審核";
                        _db.Resumes.Add(model);
                        await _db.SaveChangesAsync();
                        finalResumeId = model.Id;
                    }
                    else
                    {
                        model.ResumeTime = now;
                        model.Status = existing.Status ?? "待審核";

                        model.AiScore = existing.AiScore;
                        model.AiComment = existing.AiComment;

                        _db.Entry(existing).CurrentValues.SetValues(model);
                        await _db.SaveChangesAsync();
                        finalResumeId = existing.Id;
                    }

                    await UpdateLanguageProficiency(finalResumeId, model.LanguageSkills);
                    await UpdateDriverLicense(finalResumeId, model.DriverLicense);
                    await UpdateComputerSkills(finalResumeId, model.ComputerSkills);

                    var fullResume = await _db.Resumes
                        .Include(r => r.Job)
                        .FirstOrDefaultAsync(r => r.Id == finalResumeId);

                    if (fullResume != null)
                    {
                        fullResume.LanguageSkills = model.LanguageSkills;
                        fullResume.DriverLicense = model.DriverLicense;
                        fullResume.ComputerSkills = model.ComputerSkills;

                        // 呼叫 AI 審查
                        var apiResult = await CallGeminiApiAndUpdateAsync(fullResume);

                        if (!apiResult.IsSuccess)
                        {
                            await transaction.RollbackAsync();
                            TempData["ApiError"] = apiResult.Message;
                            model.Job = await _db.Jobs.FindAsync(model.JobsId);
                            return View("Resume", model);
                        }
                    }
                    else
                    {
                        await transaction.RollbackAsync();
                        TempData["ApiError"] = "❌ 系統在儲存中發生編號衝突，請重新整理頁面再試一次。";
                        model.Job = await _db.Jobs.FindAsync(model.JobsId);
                        return View("Resume", model);
                    }

                    await transaction.CommitAsync();
                    TempData["ShowSuccessAlert"] = "履歷已送出！";
                    return RedirectToAction("Job_detail", "Job", new { id = model.JobsId });
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    TempData["ApiError"] = $"❌ 系統資料庫寫入異常，原因：{ex.Message}";
                    model.Job = await _db.Jobs.FindAsync(model.JobsId);
                    return View("Resume", model);
                }
            }
        }

        private async Task<(bool IsSuccess, string Message)> CallGeminiApiAndUpdateAsync(Resume resume)
        {
            try
            {
                if (string.IsNullOrEmpty(GeminiApiKey))
                    return (false, "❌ 未設定 Gemini API Key");

                if (resume.Job == null)
                {
                    resume.Job = await _db.Jobs.FindAsync(resume.JobsId);
                }

                var client = _httpClientFactory.CreateClient();

                // 使用官方最穩健的生產通道終端點
                var url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent?key={GeminiApiKey.Trim()}";

                var jobTitle = resume.Job?.Title ?? "未指定職缺";
                var jobDesc = resume.Job?.Description ?? "無職缺說明";
                var jobReq = resume.Job?.Requirements ?? "無特殊專長要求";
                var expReq = resume.Job?.ExperienceRequired ?? "無經驗限制";
                var eduReq = resume.Job?.EducationRequired ?? "無學歷限制";
                var indExpReq = resume.Job?.IndustryExperience ?? "不限行業背景";
                var majorReq = resume.Job?.MajorRequired ?? "不限科系";
                var langReq = resume.Job?.LanguageRequired ?? "不限語文能力";
                var certReq = resume.Job?.CertRequired ?? "無必備證照要求";
                var otherReq = resume.Job?.OtherRequirements ?? "無其他要求";
                var skillTags = resume.Job?.SkillTags ?? "無技能標籤";

                var systemPart = @"你是一位台灣科技企業眼光極度犀利、絕不寬容的資深人資主管。請將下方的「目標職缺需求條件」與「求職者履歷內容」進行一對一的精準匹配審查。
你必須深層評估求職者在【職缺職稱(Title)對應度】、【歷任工作經歷(JobDescription)真實含金量】與【自傳(Autobiography)專業特質】上是否符合該工作的要求。

⚖️ 鋼鐵評分與點評對齊紀律：
1. 🎯 分數級距實體定義：
   - 【80~100分】：最高學歷、要求科系(MajorRequired)、年資、核心技能皆全面完美符合或超出職缺預期。
   - 【60~79分】：符合學歷或年資基本門檻，但缺乏部分進階專長，或缺少加分證照。
   - 【0~59分】：求職者存在嚴重的硬性條件不符！(例如：職缺要求資訊科系，求職者卻是完全無關的科系；或者求職者完全不具備程式實作背景)。

2. 🚨【低分群懲罰機制】：
   - 如果你給出的分數低於 60 分，評語中絕對不准出現『條件相符』等正面或敷衍字眼！
   - 低於 60 分時，評語開頭必須以『【資格不符】』為起手式，並毫不留情地具體指出是職稱背景不對、經歷太淺、還是自傳不符。

🗂️ 嚴格回傳格式規範：
[SCORE]請在此處直接輸出0-100的純數字，不准帶有任何標點符號或引號
[COMMENT]請詳細且具體地輸出你的犀利點評，用詞冷酷客觀、直擊痛點。請詳細寫出考量的細節原因（不要刻意壓縮字數，把話完整講完），不准使用任何 JSON 括號或引號外殼！";

                var promptBody = $@"
【目標職缺需求條件（標準答案）】
職缺名稱（關鍵職稱）：{jobTitle}
工作說明：{jobDesc}
必備技能要求：{jobReq}
工作經驗要求：{expReq}
學歷要求：{eduReq}
要求科系背景：{majorReq}
特定行業經驗：{indExpReq}
要求語文能力：{langReq}
必備證照資格：{certReq}
其他特殊要求：{otherReq}
技能標籤：{skillTags}

【求職者履歷內容（考生考卷）】
最高學歷：{resume.EduLevel} ({resume.SchoolName} - {resume.Major} / {resume.EduStatus})
工作年資：{resume.WorkExperienceYears} 年
歷任公司與職稱：{resume.CompanyName} - {resume.JobTitle}
歷任工作內容說明：{resume.JobDescription}
語文能力：{resume.LanguageSkills}
專業證照資格：{resume.Certificates}
自傳本文：{resume.Autobiography}";

                var fullPrompt = $"{systemPart}\n\n{promptBody}";

                var body = new
                {
                    contents = new[]
                    {
                        new { parts = new[] { new { text = fullPrompt } } }
                    },
                    generationConfig = new
                    {
                        maxOutputTokens = 1500, // 🎯 擴大 Token 上限，讓 AI 盡情把長點評寫完
                        temperature = 0.2
                    }
                };

                var json = JsonSerializer.Serialize(body);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await client.PostAsync(url, content);
                var respBody = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                    return (false, $"Google API 錯誤代碼: {response.StatusCode}, 錯誤訊息: {respBody}");

                using var doc = JsonDocument.Parse(respBody);
                var rawText = doc.RootElement
                    .GetProperty("candidates")[0]
                    .GetProperty("content")
                    .GetProperty("parts")[0]
                    .GetProperty("text")
                    .GetString() ?? "";

                rawText = rawText.Trim();

                int score = 0;
                string comment = "AI人資未成功完成審查。";

                // 提取分數 [SCORE]
                var scoreMatch = Regex.Match(rawText, @"\[SCORE\]\s*(\d+)", RegexOptions.IgnoreCase);
                if (scoreMatch.Success)
                {
                    score = int.Parse(scoreMatch.Groups[1].Value);
                }

                // 提取完整的評語內容 [COMMENT]
                var commentMatch = Regex.Match(rawText, @"\[COMMENT\]\s*([\s\S]*)", RegexOptions.IgnoreCase);
                if (commentMatch.Success)
                {
                    comment = commentMatch.Groups[1].Value.Trim();
                }
                else
                {
                    if (rawText.Contains("[SCORE]"))
                    {
                        comment = rawText.Substring(rawText.IndexOf(']') + 1).Trim();
                    }
                }

                // 清洗可能殘留的引號外殼或大括號
                comment = comment.Replace("\"", "").Replace("'", "").Replace("{", "").Replace("}", "").Trim();

                // 🎯【硬核解鎖防線】：直接操作追蹤實體，通知 EF Core 欄位已被完全修改
                var dbEntity = _db.Resumes.Local.FirstOrDefault(r => r.Id == resume.Id);
                if (dbEntity == null)
                {
                    _db.Resumes.Attach(resume);
                }

                _db.Entry(resume).Property(r => r.AiScore).IsModified = true;
                _db.Entry(resume).Property(r => r.AiComment).IsModified = true;

                resume.AiScore = score;
                resume.AiComment = comment;

                await _db.SaveChangesAsync();
                return (true, "成功");
            }
            catch (Exception ex)
            {
                return (false, $"❌ 系統異常：{ex.Message}");
            }
        }

        private string FormatLanguageString(List<LanguageProficiency> langs)
        {
            if (langs == null || !langs.Any()) return "";
            return string.Join(", ", langs.Select(l =>
                l.Language == "不具外文能力" ? l.Language : $"{l.Language}({l.Degree})"));
        }

        private string FormatDriverLicenseString(List<DriverLicense> licenses)
        {
            if (licenses == null || !licenses.Any()) return "";

            var result = new List<string>();
            var grouped = licenses.Where(l => l.Driver != "汽(機)車").GroupBy(l => l.Driver);

            foreach (var g in grouped)
            {
                result.Add($"{g.Key}({string.Join("/", g.Select(x => x.Type))})");
            }

            var status = licenses.Where(l => l.Driver == "汽(機)車").Select(x => x.Type);
            if (status.Any())
            {
                result.Add(string.Join("/", status));
            }

            return string.Join(", ", result);
        }

        private string FormatComputerSkillString(List<ComputerSkills> skills)
        {
            if (skills == null || !skills.Any()) return "";
            return string.Join(", ", skills.Select(s => s.ComputerSkill));
        }

        private async Task UpdateLanguageProficiency(int resumeId, string? languageSkills)
        {
            var oldItems = _db.LanguageProficiency.Where(l => l.ResumeId == resumeId);
            _db.LanguageProficiency.RemoveRange(oldItems);

            if (!string.IsNullOrEmpty(languageSkills))
            {
                var parts = languageSkills.Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var p in parts)
                {
                    if (p == "不具外文能力")
                    {
                        _db.LanguageProficiency.Add(new LanguageProficiency { ResumeId = resumeId, Language = p, Degree = "無" });
                    }
                    else if (p.Contains("(") && p.Contains(")"))
                    {
                        var langName = p.Split('(')[0];
                        var degreeName = p.Split('(', ')')[1];
                        _db.LanguageProficiency.Add(new LanguageProficiency { ResumeId = resumeId, Language = langName, Degree = degreeName });
                    }
                }
            }
            await _db.SaveChangesAsync();
        }

        private async Task UpdateDriverLicense(int resumeId, string? driverLicense)
        {
            var oldItems = _db.DriverLicense.Where(d => d.ResumeId == resumeId);
            _db.DriverLicense.RemoveRange(oldItems);

            if (!string.IsNullOrEmpty(driverLicense))
            {
                var parts = driverLicense.Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var p in parts)
                {
                    if (p.Contains("(") && p.Contains(")"))
                    {
                        var driver = p.Split('(')[0];
                        var types = p.Split('(', ')')[1].Split('/');
                        foreach (var t in types)
                        {
                            _db.DriverLicense.Add(new DriverLicense
                            {
                                ResumeId = resumeId,
                                Driver = driver,
                                Type = t
                            });
                        }
                    }
                    else
                    {
                        _db.DriverLicense.Add(new DriverLicense
                        {
                            ResumeId = resumeId,
                            Driver = "汽(機)車",
                            Type = p
                        });
                    }
                }
            }
            await _db.SaveChangesAsync();
        }

        private async Task UpdateComputerSkills(int resumeId, string? computerSkills)
        {
            var oldItems = await _db.ComputerSkills.Where(s => s.ResumeId == resumeId).ToListAsync();

            if (oldItems.Any())
            {
                _db.ComputerSkills.RemoveRange(oldItems);
                await _db.SaveChangesAsync();
            }

            if (!string.IsNullOrEmpty(computerSkills))
            {
                var parts = computerSkills.Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var p in parts)
                {
                    _db.ComputerSkills.Add(new ComputerSkills
                    {
                        ResumeId = resumeId,
                        ComputerSkill = p
                    });
                }
                await _db.SaveChangesAsync();
            }
        }

        [HttpPost]
        public async Task<IActionResult> ExportToPdf(Resume model)
        {
            if (model.Id == 0) return Content("請先儲存履歷後再匯出");

            var member = await _db.Members.FirstOrDefaultAsync(m => m.Id == model.MembersId);
            if (member == null) return Content("找不到會員資料");

            var dbLangs = await _db.LanguageProficiency.Where(l => l.ResumeId == model.Id).ToListAsync();
            var dbLicenses = await _db.DriverLicense.Where(d => d.ResumeId == model.Id).ToListAsync();
            var dbCompSkills = await _db.ComputerSkills.Where(s => s.ResumeId == model.Id).ToListAsync();

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
                catch
                {
                    photoData = "";
                }
            }

            string templatePath = Path.Combine(_env.WebRootPath, "file", "履歷表.docx");
            if (!System.IO.File.Exists(templatePath))
            {
                return Content($"找不到 Word 範本檔案：{templatePath}");
            }

            string ck = "■";
            string un = "□";

            Func<string, string, string> checkLang = (name, degree) =>
                dbLangs.Any(x => x.Language == name && x.Degree == degree) ? ck : un;
            Func<string, string, string> checkLic = (driver, type) => dbLicenses.Any(x => x.Driver == driver && x.Type == type) ? ck : un;
            Func<string, string> checkcomp = (skillName) => dbCompSkills.Any(x => x.ComputerSkill == skillName) ? ck : un;

            string[] knownLangs = { "英語", "日語", "台語", "客語", "不具外文能力" };
            var otherLang = dbLangs.FirstOrDefault(x => !knownLangs.Contains(x.Language));

            string lic = model.DriverLicense ?? "";
            string comp = model.ComputerSkills ?? "";
            string spec = model.Specialty ?? "";
            string cert = model.Certificates ?? "";
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
                ["E_Other"] = (!string.IsNullOrEmpty(model.EduLevel) &&
                      !new[] { "博士", "碩士", "大學", "專科", "高職", "高中", "國中", "國小" }.Contains(model.EduLevel)) ? ck : un,
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
                string currentCert = certList.ElementAtOrDefault(i - 1) ?? "";

                if (!string.IsNullOrEmpty(currentCert))
                {
                    value[$"C{i}_Name"] = currentCert.Split('(')[0].Trim();
                    value[$"C{i}_A"] = currentCert.Contains("甲") ? ck : un;
                    value[$"C{i}_B"] = currentCert.Contains("乙") ? ck : un;
                    value[$"C{i}_C"] = currentCert.Contains("丙") ? ck : un;
                    value[$"C{i}_S"] = currentCert.Contains("單一級") ? ck : un;
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
                    ms.SaveAsByTemplate(templatePath, value);
                    ms.Position = 0;

                    Document doc = new Document();
                    doc.LoadFromStream(ms, FileFormat.Docx);

                    using (MemoryStream pdfStream = new MemoryStream())
                    {
                        doc.SaveToStream(pdfStream, FileFormat.PDF);
                        return File(pdfStream.ToArray(), "application/pdf", $"{realName}_履歷表.pdf");
                    }
                }
            }
            catch (System.Exception ex)
            {
                return Content($"導出 PDF 過程發生錯誤：{ex.Message}\n 堆疊追蹤：{ex.StackTrace}");
            }
        }
    }
}