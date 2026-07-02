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
        // 🚨 替換為你真實的 API Key
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

        // 綁定基礎會員資料到 ViewBag，避免驗證失敗時畫面資料遺失
        private async Task PopulateViewBagData(int userId)
        {
            var member = await _db.Members.FindAsync(userId);
            ViewBag.UserName = member?.Name;
            ViewBag.UserGender = member?.Gender;
            ViewBag.UserIdNumber = member?.IdNumber;
            ViewBag.UserBirthday = member?.Birthday;
            ViewBag.UserAddress = member?.Address;
            ViewBag.UserEmail = member?.Email;
            ViewBag.UserPhotoBase64 = member?.ProfileImagePath;
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

            await PopulateViewBagData(userId);
            return View(model);
        }

        [HttpPost]
        public async Task<IActionResult> SaveResume(Resume model)
        {
            // 排除系統與導航屬性驗證
            ModelState.Remove("ResumeTime");
            ModelState.Remove("Job");
            ModelState.Remove("Status");
            ModelState.Remove("AiScore");
            ModelState.Remove("AiComment");
            ModelState.Remove("Members");

            int userId = GetCurrentUserId();

            if (userId == 0)
            {
                TempData["ApiError"] = "❌ 登入已過期，請重新登入後再送出履歷。";
                model.Job = await _db.Jobs.FindAsync(model.JobsId);
                await PopulateViewBagData(userId);
                return View("Resume", model);
            }

            model.MembersId = userId;

            if (model.JobsId <= 0)
            {
                TempData["ApiError"] = "❌ 系統錯誤：未接收到職缺編號。";
                model.Job = await _db.Jobs.FindAsync(model.JobsId);
                await PopulateViewBagData(userId);
                return View("Resume", model);
            }

            if (!ModelState.IsValid)
            {
                var errorDetails = string.Join(" | ", ModelState
                  .Where(x => x.Value.Errors.Count > 0)
                  .Select(x => $"[{x.Key}]: {string.Join(", ", x.Value.Errors.Select(e => e.ErrorMessage))}"));

                TempData["ApiError"] = $"❌ 表單驗證失敗：{errorDetails}";
                model.Job = await _db.Jobs.FindAsync(model.JobsId);
                await PopulateViewBagData(userId);
                return View("Resume", model);
            }

            // 🌟 開啟資料庫交易 (Transaction)，確保履歷與 AI 評分同進同退
            using (var transaction = await _db.Database.BeginTransactionAsync())
            {
                try
                {
                    var existing = await _db.Resumes
                      .FirstOrDefaultAsync(r => r.MembersId == userId && r.JobsId == model.JobsId);

                    DateTime now = DateTime.Now;
                    Resume trackedResume;

                    // 1. 將基本履歷資料寫入資料庫 (尚未正式 Commit)
                    if (existing == null)
                    {
                        model.ResumeTime = now;
                        model.Status = "待審核";
                        _db.Resumes.Add(model);
                        await _db.SaveChangesAsync(); // 產生 ID
                        trackedResume = model;
                    }
                    else
                    {
                        model.ResumeTime = now;
                        model.Status = existing.Status ?? "待審核";
                        model.AiScore = existing.AiScore; // 暫時保留舊分數
                        model.AiComment = existing.AiComment;

                        _db.Entry(existing).CurrentValues.SetValues(model);
                        await _db.SaveChangesAsync();
                        trackedResume = existing;
                    }

                    // 2. 寫入關聯資料表
                    await UpdateLanguageProficiency(trackedResume.Id, model.LanguageSkills);
                    await UpdateDriverLicense(trackedResume.Id, model.DriverLicense);
                    await UpdateComputerSkills(trackedResume.Id, model.ComputerSkills);
                    await UpdateSpecialties(trackedResume.Id, model.Specialty);

                    // 補齊供 AI 審查的完整資訊
                    trackedResume.Job = await _db.Jobs.FindAsync(trackedResume.JobsId);
                    trackedResume.LanguageSkills = model.LanguageSkills;
                    trackedResume.DriverLicense = model.DriverLicense;
                    trackedResume.ComputerSkills = model.ComputerSkills;

                    // 🌟 3. 呼叫 AI API 進行審核
                    var apiResult = await GetGeminiReviewAsync(trackedResume);

                    // 🚨 4. 如果 AI 連線失敗或格式錯誤 -> 取消寫入並 Alert
                    if (!apiResult.IsSuccess)
                    {
                        await transaction.RollbackAsync(); // 🛑 取消所有資料庫寫入動作！
                        TempData["ApiError"] = $"無法儲存履歷！\nAI 審查連線異常或失敗，原因：\n{apiResult.Message}";

                        model.Job = trackedResume.Job;
                        await PopulateViewBagData(userId);
                        return View("Resume", model); // 返回畫面讓使用者重試
                    }

                    // 🌟 5. 如果 AI 成功，將分數寫入欄位
                    trackedResume.AiScore = apiResult.Score;
                    trackedResume.AiComment = apiResult.Comment;

                    _db.Entry(trackedResume).Property(r => r.AiScore).IsModified = true;
                    _db.Entry(trackedResume).Property(r => r.AiComment).IsModified = true;
                    await _db.SaveChangesAsync();

                    // 🌟 6. 一切順利，正式提交進資料庫
                    await transaction.CommitAsync();

                    TempData["ShowSuccessAlert"] = "履歷已成功送出，AI 審核完成！";
                    return RedirectToAction("Job_detail", "Job", new { id = model.JobsId });
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    TempData["ApiError"] = $"❌ 系統處理異常，履歷未儲存：{ex.Message}";
                    model.Job = await _db.Jobs.FindAsync(model.JobsId);
                    await PopulateViewBagData(userId);
                    return View("Resume", model);
                }
            }
        }

        // 獨立出只負責「拿 AI 結果」的方法，不再處理資料庫寫入
        // 🎯 Job.Requirements 欄位已刪除，改把 SkillTags / MajorRequirements / LanguageRequirements /
        //    CertRequired / OtherRequirements 組成一段文字，餵給 AI 當作「職缺要求」內容
        //    ⚠️ 呼叫前記得確保 job 是用 Include 帶出 SkillTags/MajorRequirements/LanguageRequirements 的，
        //       否則這幾個集合會是空的（不會報錯，但 AI 審查會少了這些條件）
        private string BuildJobRequirementsText(Job? job)
        {
            if (job == null) return "無";

            var parts = new List<string>();

            if (job.SkillTags != null && job.SkillTags.Any())
                parts.Add("技能需求：" + string.Join("、", job.SkillTags.Select(t => t.Tag)));

            if (job.MajorRequirements != null && job.MajorRequirements.Any())
                parts.Add("科系需求：" + string.Join("、", job.MajorRequirements.Select(m => m.Major)));

            if (job.LanguageRequirements != null && job.LanguageRequirements.Any())
                parts.Add("語文需求：" + string.Join("、", job.LanguageRequirements.Select(l => $"{l.Language}（{l.Degree}）")));

            if (!string.IsNullOrEmpty(job.CertRequired))
                parts.Add("必要證照：" + job.CertRequired);

            if (!string.IsNullOrEmpty(job.OtherRequirements))
                parts.Add("其他條件：" + job.OtherRequirements);

            return parts.Count > 0 ? string.Join("\n", parts) : "無";
        }

        private async Task<(bool IsSuccess, string Message, int Score, string Comment)> GetGeminiReviewAsync(Resume resume)
        {
            try
            {
                if (string.IsNullOrEmpty(GeminiApiKey))
                    return (false, "系統未設定 Gemini API Key", 0, "");

                var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(60);

                var url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent?key={GeminiApiKey.Trim()}";

                var jobTitle = resume.Job?.Title ?? "未指定職缺";
                var jobDesc = resume.Job?.Description ?? "無說明";
                var jobReq = BuildJobRequirementsText(resume.Job);

                var promptBody = $@"
你是一位嚴格的資深人資主管，請針對以下職缺與履歷進行一對一精準匹配審查。

【職缺條件】
職稱：{jobTitle}
說明：{jobDesc}
要求：{jobReq}

【履歷內容】
學歷：{resume.EduLevel} ({resume.SchoolName} - {resume.Major} / {resume.EduStatus})
工作年資：{resume.WorkExperienceYears} 年
公司與職稱：{resume.CompanyName} - {resume.JobTitle}
經歷說明：{resume.JobDescription}
語文：{resume.LanguageSkills}
證照：{resume.Certificates}
自傳：{resume.Autobiography}

🚨 你必須嚴格遵守以下輸出格式，不可包含任何 Markdown (如 ```json) 或其他廢話：
[SCORE]請在此填入 0 到 100 的數字
[COMMENT]請在此填入具體評語";

                var body = new
                {
                    contents = new[] { new { parts = new[] { new { text = promptBody } } } },
                    generationConfig = new { maxOutputTokens = 1500, temperature = 0.2 }
                };

                var json = JsonSerializer.Serialize(body);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await client.PostAsync(url, content);
                var respBody = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                    return (false, $"Google API 拒絕連線 (HTTP {response.StatusCode})", 0, "");

                using var doc = JsonDocument.Parse(respBody);
                var rawText = doc.RootElement
                  .GetProperty("candidates")[0]
                  .GetProperty("content")
                  .GetProperty("parts")[0]
                  .GetProperty("text")
                  .GetString() ?? "";

                // 🚨 嚴格擷取分數與評語
                var scoreMatch = Regex.Match(rawText, @"\[SCORE\]\s*(\d+)", RegexOptions.IgnoreCase);
                var commentMatch = Regex.Match(rawText, @"\[COMMENT\]\s*([\s\S]*)", RegexOptions.IgnoreCase);

                if (!scoreMatch.Success || !commentMatch.Success)
                {
                    string shortResp = rawText.Length > 50 ? rawText.Substring(0, 50) + "..." : rawText;
                    return (false, $"AI 回傳格式不符預期，解析失敗。回傳內容擷取：{shortResp}", 0, "");
                }

                int score = int.Parse(scoreMatch.Groups[1].Value);
                string comment = commentMatch.Groups[1].Value.Trim().Replace("\"", "").Replace("{", "").Replace("}", "");

                return (true, "成功", score, comment);
            }
            catch (TaskCanceledException)
            {
                return (false, "AI 審查逾時（超過 60 秒），遠端伺服器無回應", 0, "");
            }
            catch (Exception ex)
            {
                return (false, $"發生未預期錯誤：{ex.Message}", 0, "");
            }
        }

        private string FormatLanguageString(List<LanguageProficiency> langs)
        {
            if (langs == null || !langs.Any()) return "";
            return string.Join(", ", langs.Select(l => l.Language == "不具外文能力" ? l.Language : $"{l.Language}({l.Degree})"));
        }

        private string FormatDriverLicenseString(List<DriverLicense> licenses)
        {
            if (licenses == null || !licenses.Any()) return "";
            var result = new List<string>();
            var grouped = licenses.Where(l => l.Driver != "汽(機)車").GroupBy(l => l.Driver);

            foreach (var g in grouped)
                result.Add($"{g.Key}({string.Join("/", g.Select(x => x.Type))})");

            var status = licenses.Where(l => l.Driver == "汽(機)車").Select(x => x.Type);
            if (status.Any())
                result.Add(string.Join("/", status));

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
                            _db.DriverLicense.Add(new DriverLicense { ResumeId = resumeId, Driver = driver, Type = t });
                    }
                    else
                    {
                        _db.DriverLicense.Add(new DriverLicense { ResumeId = resumeId, Driver = "汽(機)車", Type = p });
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
                    _db.ComputerSkills.Add(new ComputerSkills { ResumeId = resumeId, ComputerSkill = p });
                }
                await _db.SaveChangesAsync();
            }
        }

        private async Task UpdateSpecialties(int resumeId, string? specialtyString)
        {
            var oldItems = await _db.Specialties.Where(s => s.ResumeId == resumeId).ToListAsync();
            if (oldItems.Any())
            {
                _db.Specialties.RemoveRange(oldItems);
                await _db.SaveChangesAsync();
            }

            if (!string.IsNullOrEmpty(specialtyString))
            {
                var parts = specialtyString.Split(new[] { "; " }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < parts.Length; i++)
                {
                    string specValue = parts[i].Trim();
                    if (!string.IsNullOrEmpty(specValue))
                    {
                        _db.Specialties.Add(new Specialties { ResumeId = resumeId, Specialty = specValue, SortOrder = i + 1 });
                    }
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
            catch (Exception ex)
            {
                return Content($"導出 PDF 過程發生錯誤：{ex.Message}");
            }
        }
    }
}