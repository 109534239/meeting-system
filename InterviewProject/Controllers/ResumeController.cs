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
            ViewBag.UserPhone = member?.Phone; // 🎯 新增：帶入會員註冊時填的手機號碼，履歷表聯絡電話預設帶入這支
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
                    var dbCertificates = await _db.Certificates.Where(s => s.ResumeId == existingResume.Id).ToListAsync();
                    var sourceSpecialties = await _db.Specialties.Where(s => s.ResumeId == existingResume.Id).OrderBy(s => s.SortOrder).ToListAsync(); // 🎯 修正：套用現有履歷時，專長之前完全沒有被查詢與帶入
                    var sourceEducations = await _db.Educations.AsNoTracking().Where(e => e.ResumeId == existingResume.Id).OrderBy(e => e.SortOrder).ToListAsync();
                    var sourceWorkExperiences = await _db.WorkExperiences.AsNoTracking().Where(w => w.ResumeId == existingResume.Id).OrderBy(w => w.SortOrder).ToListAsync();
                    var sourcePortfolios = await _db.Portfolios.AsNoTracking().Where(p => p.ResumeId == existingResume.Id).OrderBy(p => p.SortOrder).ToListAsync();

                    model = existingResume;
                    model.LanguageSkills = FormatLanguageString(sourceLangs);
                    model.DriverLicense = FormatDriverLicenseString(dbLicenses);
                    model.ComputerSkills = FormatComputerSkillString(dbCompSkills);
                    model.Certificates = FormatCertificatesString(dbCertificates);
                    model.Specialty = FormatSpecialtyString(sourceSpecialties); // 🎯 修正：補上專長的反填，之前這行完全沒有出現過
                    model.Educations = sourceEducations; // 🎯 學歷子表也要一起帶到新履歷（Id 沿用只是拿來顯示，實際存檔時會重新新增）
                    model.WorkExperiences = sourceWorkExperiences; // 🎯 工作經歷子表同樣要帶過去
                    model.Portfolios = sourcePortfolios; // 🎯 作品集子表同樣要帶過去（實體檔案沿用同一份，不重新複製檔案）

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
                  .Include(r => r.Educations) // 🎯 帶出學歷子表，讓表單能還原多筆學歷
                  .Include(r => r.WorkExperiences) // 🎯 帶出工作經歷子表，讓表單能還原多筆工作經歷
                  .Include(r => r.Portfolios) // 🎯 帶出作品集子表，讓表單能還原多筆作品集
                  .FirstOrDefaultAsync(r => r.MembersId == userId && r.JobsId == jobId);

                if (model == null)
                {
                    var member = await _db.Members.FindAsync(userId);
                    model = new Resume
                    {
                        MembersId = userId,
                        JobsId = jobId,
                        WorkExperienceYears = -1,
                        Job = targetJob,
                        ContactAddress = member?.Address ?? "", // 🎯 新履歷預設帶入會員地址，之後可於履歷中變更
                        Phone1 = member?.Phone ?? "" // 🎯 新履歷預設帶入會員註冊手機號碼，之後可於履歷中變更（電話欄位整併只留 Phone1，Phone2/Mobile 已移除）
                    };
                }
                else
                {
                    var langs = await _db.LanguageProficiency.Where(l => l.ResumeId == model.Id).ToListAsync();
                    model.LanguageSkills = FormatLanguageString(langs);
                    var dbLicenses = await _db.DriverLicense.Where(d => d.ResumeId == model.Id).ToListAsync();
                    model.DriverLicense = FormatDriverLicenseString(dbLicenses);
                    var dbCompSkills = await _db.ComputerSkills.Where(s => s.ResumeId == model.Id).ToListAsync();
                    model.ComputerSkills = FormatComputerSkillString(dbCompSkills);
                    var dbCertificates = await _db.Certificates.Where(s => s.ResumeId == model.Id).ToListAsync();
                    model.Certificates = FormatCertificatesString(dbCertificates);
                    var dbSpecialties = await _db.Specialties.Where(s => s.ResumeId == model.Id).OrderBy(s => s.SortOrder).ToListAsync(); // 🎯 修正：一般編輯載入時，專長之前也完全沒有被查詢與帶入
                    model.Specialty = FormatSpecialtyString(dbSpecialties); // 🎯 修正：補上專長的反填
                }
            }

            await PopulateViewBagData(userId);
            return View(model);
        }

        [HttpPost]
        public async Task<IActionResult> SaveResume(
            Resume model,
            List<string>? EduLevelList,
            List<string>? SchoolNameList,
            List<string>? MajorList,
            List<string>? EduStatusList,
            List<string>? StartDateList,
            List<string>? EndDateList,
            List<string>? CompanyNameList,
            List<string>? JobTitleList,
            List<string>? JobDescriptionList,
            List<string>? WorkStartDateList,
            List<string>? WorkEndDateList,
            List<string>? PortfolioTitleList,
            List<string>? PortfolioDescList,
            List<string>? PortfolioLinkList,
            List<IFormFile>? PortfolioFileList,
            List<string>? PortfolioExistingFileList,
            string? ProfileImageBase64)
        {
            // 🎯 作品集上傳改成「一列可多個檔案」，前端用 name="PortfolioFileList_{列序}" + multiple
            //    送出（見 Resume.cshtml 的 preparePortfolioFilesForSubmit），沒辦法直接靠單一
            //    List<IFormFile> 參數綁定，所以改成自己讀 Request.Form.Files、
            //    依欄位名稱分組成「第幾列 → 這一列選了哪些檔案」。
            var portfolioFilesByRow = new Dictionary<int, List<IFormFile>>();
            const string portfolioFilePrefix = "PortfolioFileList_";
            foreach (var f in Request.Form.Files)
            {
                if (f.Length <= 0 || !f.Name.StartsWith(portfolioFilePrefix)) continue;
                if (!int.TryParse(f.Name.Substring(portfolioFilePrefix.Length), out int rowIndex)) continue;
                if (!portfolioFilesByRow.TryGetValue(rowIndex, out var rowFiles))
                {
                    rowFiles = new List<IFormFile>();
                    portfolioFilesByRow[rowIndex] = rowFiles;
                }
                rowFiles.Add(f);
            }

            // 排除系統與導航屬性驗證
            ModelState.Remove("ResumeTime");
            ModelState.Remove("Job");
            ModelState.Remove("Status");
            ModelState.Remove("AiScore");
            ModelState.Remove("AiComment");
            ModelState.Remove("Members");
            ModelState.Remove("Educations"); // 🎯 學歷改用平行陣列（EduLevelList 等）送出，不透過 model.Educations 綁定
            ModelState.Remove("WorkExperiences"); // 🎯 工作經歷同樣改用平行陣列送出
            ModelState.Remove("Portfolios"); // 🎯 作品集同樣改用平行陣列（含檔案上傳）送出

            int userId = GetCurrentUserId();

            if (userId == 0)
            {
                TempData["ApiError"] = "❌ 登入已過期，請重新登入後再送出履歷。";
                model.Job = await _db.Jobs.FindAsync(model.JobsId);
                await PopulateViewBagData(userId);
                return View("Resume", model);
            }

            model.MembersId = userId;

            // 🎯 大頭照：跟 Profile.cshtml 用同一套 base64 直存法，且獨立於履歷驗證/交易之外，
            //    避免「照片換好了，但履歷表單其他欄位驗證沒過」導致照片也跟著白改
            var photoMember = await _db.Members.FindAsync(userId);
            if (photoMember != null && !string.IsNullOrEmpty(ProfileImageBase64) && ProfileImageBase64.StartsWith("data:image"))
            {
                photoMember.ProfileImagePath = ProfileImageBase64;
                await _db.SaveChangesAsync();
            }

            // 🚨 大頭照為必填：前端 JS 已經擋過一次，但 JS 驗證永遠可能因為瀏覽器停用 JavaScript、
            //    第三方套件（SweetAlert2）載入延遲/失敗、或使用者直接用工具送出原始 POST 而被繞過，
            //    所以伺服器端一定要重新檢查一次，這裡才是真正擋得住的最後一道防線。
            //    不管是這次新上傳的照片，還是先前已經存在會員資料裡的照片，只要目前完全沒有照片就不放行。
            if (photoMember == null || string.IsNullOrWhiteSpace(photoMember.ProfileImagePath))
            {
                TempData["ApiError"] = "❌ 請上傳大頭照後再送出履歷。";
                model.Job = await _db.Jobs.FindAsync(model.JobsId);
                await PopulateViewBagData(userId);
                return View("Resume", model);
            }

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

            // 🚨 證照職類及級別為條件必填：若使用者填的證照名稱，在證照對照表（Certificatecategories）
            //    裡查得到、且該證照確實有「可選級別」，卻沒有一併填寫級別，就視為漏填，擋下送出。
            //    這一樣是伺服器端最後把關，理由同上（前端 JS 驗證可能被繞過或因為非同步資料還沒載入而失效）。
            var certLevelError = await ValidateCertificateLevelsAsync(model.Certificates);
            if (!string.IsNullOrEmpty(certLevelError))
            {
                TempData["ApiError"] = $"❌ {certLevelError}";
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
                    await UpdateCertificates(trackedResume.Id, model.Certificates);
                    await UpdateEducations(trackedResume.Id, EduLevelList, SchoolNameList, MajorList, EduStatusList, StartDateList, EndDateList);
                    await UpdateWorkExperiences(trackedResume.Id, CompanyNameList, JobTitleList, JobDescriptionList, WorkStartDateList, WorkEndDateList);
                    // 🎯 作品集檔案改成依「會員姓名／應徵職稱」分資料夾存放，所以要先查出這兩個名稱
                    var portfolioMember = await _db.Members.FindAsync(userId);
                    var portfolioJob = await _db.Jobs.FindAsync(model.JobsId);
                    await UpdatePortfolios(trackedResume.Id, portfolioMember?.Name ?? "", portfolioJob?.Title ?? "", PortfolioTitleList, PortfolioDescList, PortfolioLinkList, portfolioFilesByRow, PortfolioExistingFileList);

                    // 補齊供 AI 審查的完整資訊
                    trackedResume.Job = await _db.Jobs.FindAsync(trackedResume.JobsId);
                    trackedResume.LanguageSkills = model.LanguageSkills;
                    trackedResume.DriverLicense = model.DriverLicense; 
                    trackedResume.ComputerSkills = model.ComputerSkills;
                    trackedResume.Certificates = model.Certificates;
                    trackedResume.Educations = await _db.Educations
                       .Where(e => e.ResumeId == trackedResume.Id)
                       .OrderBy(e => e.SortOrder)
                       .ToListAsync();
                    trackedResume.WorkExperiences = await _db.WorkExperiences
                        .Where(w => w.ResumeId == trackedResume.Id)
                        .OrderBy(w => w.SortOrder)
                        .ToListAsync();
                    trackedResume.Portfolios = await _db.Portfolios
                       .Where(p => p.ResumeId == trackedResume.Id)
                       .OrderBy(p => p.SortOrder)
                       .ToListAsync();

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
學歷：{FormatEducationString(resume.Educations)}
工作年資：{resume.WorkExperienceYears} 年
工作經歷：{FormatWorkExperienceString(resume.WorkExperiences)}
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
            // 🎯 修正：改用 "; " 分隔，跟 Resume.cshtml 反填時的 split('; ') 對齊。
            //    原本用 ", " 會跟「電腦能力內容本身含有逗號」的情況（如「專案管理工具（MS Project, Jira, Trello）」）混淆，
            //    也跟前端反填邏輯的分隔符號不一致，導致資料整包被當成一筆、甚至讓反填的 JS 出錯而整段失敗。
            return string.Join("; ", skills.Select(s => s.ComputerSkill));
        }

        // 🎯 修正：新增專長的格式化函式（先前完全沒有這個函式，也沒有任何地方把 Specialties 資料表讀回 model.Specialty）
        private string FormatSpecialtyString(List<Specialties> specialties)
        {
            if (specialties == null || !specialties.Any()) return "";
            // 用 "; " 分隔，對齊 UpdateSpecialties() 存檔時的 split 邏輯，以及 Resume.cshtml 反填時的 specData.split('; ')
            return string.Join("; ", specialties.OrderBy(s => s.SortOrder).Select(s => s.Specialty));
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

        // 🎯 把多筆學歷組成一段可讀文字，給 AI 審查 prompt 用
        private string FormatEducationString(ICollection<Education>? educations)
        {
            if (educations == null || !educations.Any()) return "無";

            return string.Join("; ", educations.OrderBy(e => e.SortOrder).Select(e =>
            {
                string period = e.StartDate.HasValue || e.EndDate.HasValue
                    ? $"，{e.StartDate?.ToString("yyyy/MM")}~{e.EndDate?.ToString("yyyy/MM")}"
                    : "";
                return $"{e.EduLevel} {e.SchoolName} - {e.Major} / {e.EduStatus}{period}";
            }));
        }

        // 🎯 把多筆工作經歷組成一段可讀文字，給 AI 審查 prompt 用
        private string FormatWorkExperienceString(ICollection<WorkExperience>? workExperiences)
        {
            if (workExperiences == null || !workExperiences.Any()) return "無";

            return string.Join("; ", workExperiences.OrderBy(w => w.SortOrder).Select(w =>
            {
                string period = w.StartDate.HasValue || w.EndDate.HasValue
                    ? $"，{w.StartDate?.ToString("yyyy/MM")}~{(w.EndDate.HasValue ? w.EndDate.Value.ToString("yyyy/MM") : "至今")}"
                    : "";
                return $"{w.CompanyName} - {w.JobTitle}（{w.JobDescription}）{period}";
            }));
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
                // 🎯 修正：分隔符號改成 "; "，跟 FormatComputerSkillString() 的 join 分隔符號、
                //    以及 Resume.cshtml 送出/反填時使用的 "; " 對齊，避免電腦能力內容本身含逗號時被錯誤切分。
                var parts = computerSkills.Split(new[] { "; " }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var p in parts)
                {
                    var trimmed = p.Trim();
                    if (!string.IsNullOrEmpty(trimmed))
                        _db.ComputerSkills.Add(new ComputerSkills { ResumeId = resumeId, ComputerSkill = trimmed });
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

        // 🚨 伺服器端證照級別驗證：解析邏輯必須跟 UpdateCertificates() 拆字串的方式（"名稱(級別)"、逗號分隔）
        //    保持一致，否則兩邊對同一筆資料的認知會兜不起來。
        //    回傳 null 代表驗證通過；回傳非 null 字串代表有錯誤，內容可直接顯示給使用者看。
        private async Task<string?> ValidateCertificateLevelsAsync(string? certificatesString)
        {
            if (string.IsNullOrWhiteSpace(certificatesString)) return null;

            var certItems = certificatesString.Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries);
            if (certItems.Length == 0) return null;

            // 💡 一次把證照對照表撈出來，避免在迴圈裡重複查詢資料庫
            var dbCerts = await _db.Certificatecategories
                .Select(c => new
                {
                    CertName = c.CertName != null ? c.CertName.Trim() : "",
                    c.AvailableLevels
                })
                .ToListAsync();

            foreach (var item in certItems)
            {
                var trimmedItem = item.Trim();
                if (string.IsNullOrEmpty(trimmedItem)) continue;

                string cName = trimmedItem;
                string levels = "";

                // 💡 拆分 "電腦軟體應用(丙級)"，跟 UpdateCertificates() 用同一套規則
                if (trimmedItem.Contains("(") && trimmedItem.EndsWith(")"))
                {
                    int openBracketIndex = trimmedItem.IndexOf('(');
                    cName = trimmedItem.Substring(0, openBracketIndex).Trim();
                    levels = trimmedItem.Substring(openBracketIndex + 1, trimmedItem.Length - openBracketIndex - 2).Trim();
                }

                // 🎯 只有「對照表裡查得到這張證照、且它明確有可選級別」時才要求必填級別；
                //    使用者自訂輸入、清單裡查不到的證照，允許不填級別（跟前端邏輯一致）
                var match = dbCerts.FirstOrDefault(c => c.CertName == cName);
                bool hasAvailableLevels = match != null && !string.IsNullOrWhiteSpace(match.AvailableLevels);

                if (hasAvailableLevels && string.IsNullOrWhiteSpace(levels))
                {
                    return $"證照「{cName}」有級別可選，請選擇對應的級別後再送出。";
                }
            }

            return null;
        }

        private async Task UpdateCertificates(int resumeId, string certificatesString)
        {
            // 💡 先刪除該履歷舊有的所有證照資料
            var oldCerts = await _db.Certificates.Where(c => c.ResumeId == resumeId).ToListAsync();
            if (oldCerts.Any())
            {
                _db.Certificates.RemoveRange(oldCerts);
                await _db.SaveChangesAsync();
            }

            if (string.IsNullOrWhiteSpace(certificatesString)) return;

            // 💡 修正點：將小寫的 split 改為大寫的 Split
            var certItems = certificatesString.Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var item in certItems)
            {
                var trimmedItem = item.Trim();
                if (string.IsNullOrEmpty(trimmedItem)) continue;

                string cName = trimmedItem;
                string levels = "";

                // 💡 拆分 "電腦軟體應用(丙級)"
                if (trimmedItem.Contains("(") && trimmedItem.EndsWith(")"))
                {
                    int openBracketIndex = trimmedItem.IndexOf('(');
                    cName = trimmedItem.Substring(0, openBracketIndex).Trim();
                    levels = trimmedItem.Substring(openBracketIndex + 1, trimmedItem.Length - openBracketIndex - 2).Trim();
                }

                // 💡 建立新實體並寫入資料庫 (請確保 Cname 的大小寫與你的 Entity 屬性一致)
                var newCert = new InterviewProject.Models.Certificates
                {
                    ResumeId = resumeId,
                    CName = cName,
                    Levels = levels
                };

                _db.Certificates.Add(newCert);
            }

            await _db.SaveChangesAsync();
        }

        // 🎯 學歷子表：比照 HrJobController.AddChildRecords 的平行陣列作法
        //    簡單作法 = 該履歷舊學歷全刪、依表單重新寫入，履歷筆數不多，不會有效能問題
        private async Task UpdateEducations(
            int resumeId,
            List<string>? levelList,
            List<string>? schoolList,
            List<string>? majorList,
            List<string>? statusList,
            List<string>? startList,
            List<string>? endList)
        {
            var oldEducations = await _db.Educations.Where(e => e.ResumeId == resumeId).ToListAsync();
            if (oldEducations.Any())
            {
                _db.Educations.RemoveRange(oldEducations);
                await _db.SaveChangesAsync();
            }

            if (schoolList == null) return;

            string Get(List<string>? list, int idx) =>
                (list != null && idx < list.Count) ? (list[idx]?.Trim() ?? "") : "";

            for (int i = 0; i < schoolList.Count; i++)
            {
                // 學校名稱是每一列的必要判斷依據：留空視為這一列沒有真的填寫，直接跳過
                if (string.IsNullOrWhiteSpace(Get(schoolList, i))) continue;

                DateTime? start = DateTime.TryParse(Get(startList, i), out var s) ? s : null;
                DateTime? end = DateTime.TryParse(Get(endList, i), out var e2) ? e2 : null;

                _db.Educations.Add(new Education
                {
                    ResumeId = resumeId,
                    EduLevel = Get(levelList, i),
                    SchoolName = Get(schoolList, i),
                    Major = Get(majorList, i),
                    EduStatus = Get(statusList, i),
                    StartDate = start,
                    EndDate = end,
                    SortOrder = i
                });
            }

            await _db.SaveChangesAsync();
        }

        // 🎯 工作經歷子表：跟 UpdateEducations 同一套寫法（全刪重寫）
        //    沒有任何列（或全部列的公司名稱都空白）＝「無工作經歷」，不需要再靠額外的旗標欄位判斷
        private async Task UpdateWorkExperiences(
            int resumeId,
            List<string>? companyList,
            List<string>? titleList,
            List<string>? descList,
            List<string>? startDateList,
            List<string>? endDateList)
        {
            var oldWorkExperiences = await _db.WorkExperiences.Where(w => w.ResumeId == resumeId).ToListAsync();
            if (oldWorkExperiences.Any())
            {
                _db.WorkExperiences.RemoveRange(oldWorkExperiences);
                await _db.SaveChangesAsync();
            }

            if (companyList == null) return;

            string Get(List<string>? list, int idx) =>
                (list != null && idx < list.Count) ? (list[idx]?.Trim() ?? "") : "";

            DateTime? GetDate(List<string>? list, int idx)
            {
                var raw = Get(list, idx);
                if (string.IsNullOrWhiteSpace(raw)) return null;
                // <input type="month"> 送出的格式是 yyyy-MM
                return DateTime.TryParse(raw + "-01", out var dt) ? dt : null;
            }

            int sortOrder = 0;
            for (int i = 0; i < companyList.Count; i++)
            {
                // 公司名稱空白視為這一列沒填，直接跳過（沒有任何一列有效資料時，等於「無工作經歷」）
                if (string.IsNullOrWhiteSpace(Get(companyList, i))) continue;

                _db.WorkExperiences.Add(new WorkExperience
                {
                    ResumeId = resumeId,
                    CompanyName = Get(companyList, i),
                    JobTitle = Get(titleList, i),
                    JobDescription = Get(descList, i),
                    StartDate = GetDate(startDateList, i),
                    EndDate = GetDate(endDateList, i),
                    SortOrder = sortOrder++
                });
            }

            await _db.SaveChangesAsync();
        }

        // 🎯 作品集子表：說明/連結是文字，另外還有「上傳檔案」。
        //    做法：檔案存實體到 wwwroot/uploads/portfolio，資料庫只存相對路徑（FilePath）。
        //    每一列如果沒有重新上傳檔案，就沿用 PortfolioExistingFileList 裡帶回來的舊路徑；
        //    整批資料庫紀錄一樣採「全刪重寫」（跟 Educations/WorkExperiences 同一套），
        //    但實體檔案要額外比對，只刪除「這次沒有被留用」的舊檔案，避免誤刪還在使用中的檔案。
        // 🎯 需求改成「什麼類型檔案都行」，不再限制副檔名白名單。
        // 🎯 把「會員姓名」「應徵職稱」清成可以安全當資料夾名稱的字串：
        //    去除檔案系統不允許的字元（例如 / \ : * ? " < > |）、去除頭尾空白，
        //    避免使用者姓名或職稱恰好包含這些符號時，Directory.CreateDirectory 直接丟例外。
        private static string SanitizeFolderName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "未命名";

            var invalidChars = Path.GetInvalidFileNameChars();
            var chars = name.Trim().Select(c => invalidChars.Contains(c) ? '_' : c).ToArray();
            var cleaned = new string(chars).Trim(' ', '.'); // Windows 資料夾名稱不能以空白或句點結尾

            if (cleaned.Length > 100) cleaned = cleaned.Substring(0, 100);

            return string.IsNullOrWhiteSpace(cleaned) ? "未命名" : cleaned;
        }

        // 🎯 把使用者上傳的原始檔名（不含副檔名）清成可以安全當檔名的字串，邏輯同 SanitizeFolderName
        private static string SanitizeFileName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "file";

            var invalidChars = Path.GetInvalidFileNameChars();
            var chars = name.Trim().Select(c => invalidChars.Contains(c) ? '_' : c).ToArray();
            var cleaned = new string(chars).Trim(' ', '.');

            if (cleaned.Length > 150) cleaned = cleaned.Substring(0, 150);

            return cleaned;
        }

        private async Task UpdatePortfolios(
            int resumeId,
            string memberName,
            string jobTitle,
            List<string>? titleList,
            List<string>? descList,
            List<string>? linkList,
            Dictionary<int, List<IFormFile>>? filesByRow,
            List<string>? existingFilePathList)
        {
            var oldPortfolios = await _db.Portfolios.Where(p => p.ResumeId == resumeId).ToListAsync();

            string Get(List<string>? list, int idx) =>
                (list != null && idx < list.Count) ? (list[idx]?.Trim() ?? "") : "";

            int filesRowCount = (filesByRow != null && filesByRow.Count > 0) ? filesByRow.Keys.Max() + 1 : 0;
            int rowCount = new[] { titleList?.Count ?? 0, descList?.Count ?? 0, linkList?.Count ?? 0, filesRowCount, existingFilePathList?.Count ?? 0 }.Max();

            // 🎯 以前所有人的檔案都平放在同一個 uploads/portfolio 資料夾、且檔名被改成 GUID，既難辨識也難整理。
            //    改成按「會員姓名/應徵職稱」分資料夾，並保留使用者上傳時的原始檔名（只做基本清理，防止路徑跳脫或不合法字元）。
            var safeMemberFolder = SanitizeFolderName(memberName);
            var safeJobFolder = SanitizeFolderName(jobTitle);
            var uploadRoot = Path.Combine(_env.WebRootPath, "uploads", "portfolio", safeMemberFolder, safeJobFolder);
            Directory.CreateDirectory(uploadRoot);
            var relativeFolder = $"/uploads/portfolio/{safeMemberFolder}/{safeJobFolder}";

            var newPortfolios = new List<Portfolio>();
            var keptFilePaths = new HashSet<string>();
            int sortOrder = 0;

            for (int i = 0; i < rowCount; i++)
            {
                string title = Get(titleList, i);
                string desc = Get(descList, i);
                string link = Get(linkList, i);
                string existingPath = Get(existingFilePathList, i);
                var uploadFiles = (filesByRow != null && filesByRow.TryGetValue(i, out var rowFiles))
                    ? rowFiles.Where(f => f != null && f.Length > 0).ToList()
                    : new List<IFormFile>();
                bool hasNewFiles = uploadFiles.Count > 0;

                // 這一列完全沒填任何東西（沒名稱、沒說明、沒連結、沒新檔案、也沒有沿用舊檔案）＝這一列沒有真的填寫，跳過
                if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(desc) && string.IsNullOrWhiteSpace(link) && !hasNewFiles && string.IsNullOrWhiteSpace(existingPath))
                    continue;

                // 🎯 一列可能對應多個檔案，路徑用「|」串接成單一字串存進 FilePath
                //    （比照 Resume 其他多值欄位如 Specialty／ComputerSkills 的逗號/分號分隔寫法）。
                //    有選新檔案就整組取代舊的；沒有新檔案就沿用 existingPath（本身也可能已經是「|」串接的多個路徑）。
                string finalFilePath = existingPath;

                if (hasNewFiles)
                {
                    var savedPaths = new List<string>();
                    foreach (var uploadFile in uploadFiles)
                    {
                        // 🎯 保留使用者上傳時的原始檔名（不再改成 GUID）。
                        //    Path.GetFileName 先把上傳者可能夾帶的路徑部分剝除（防 ../ 路徑跳脫），
                        //    再用 SanitizeFileName 清掉檔案系統不允許的字元。
                        var originalName = Path.GetFileName(uploadFile.FileName);
                        var ext = Path.GetExtension(originalName);
                        var baseName = Path.GetFileNameWithoutExtension(originalName);
                        var safeBaseName = SanitizeFileName(baseName);
                        if (string.IsNullOrWhiteSpace(safeBaseName)) safeBaseName = "file";

                        var finalName = $"{safeBaseName}{ext}";
                        var savePath = Path.Combine(uploadRoot, finalName);

                        // 🎯 同資料夾已有同名檔案時（例如重新上傳同一個檔名），加上流水號尾綴避免覆蓋別筆資料（例如 resume(1).pdf）
                        int dup = 1;
                        while (System.IO.File.Exists(savePath))
                        {
                            finalName = $"{safeBaseName}({dup}){ext}";
                            savePath = Path.Combine(uploadRoot, finalName);
                            dup++;
                        }

                        using (var stream = new FileStream(savePath, FileMode.Create))
                        {
                            await uploadFile.CopyToAsync(stream);
                        }
                        savedPaths.Add($"{relativeFolder}/{finalName}");
                    }
                    finalFilePath = string.Join("|", savedPaths);
                }

                if (!string.IsNullOrWhiteSpace(finalFilePath))
                {
                    foreach (var singlePath in finalFilePath.Split('|', StringSplitOptions.RemoveEmptyEntries))
                    {
                        keptFilePaths.Add(singlePath);
                    }
                }

                newPortfolios.Add(new Portfolio
                {
                    ResumeId = resumeId,
                    Title = title,
                    Description = desc,
                    Link = link,
                    FilePath = string.IsNullOrWhiteSpace(finalFilePath) ? null : finalFilePath,
                    SortOrder = sortOrder++
                });
            }
            if (oldPortfolios.Any())
            {
                _db.Portfolios.RemoveRange(oldPortfolios);
                await _db.SaveChangesAsync();
            }

            if (newPortfolios.Any())
            {
                _db.Portfolios.AddRange(newPortfolios);
                await _db.SaveChangesAsync();
            }

            // 🎯 清掉硬碟上「舊有、但這次沒有被留用」的實體檔案（換新檔、單獨刪除某個檔案、或整筆被刪除的情況）。
            //    old.FilePath 現在可能是「|」串接的多個路徑，要逐一拆開個別比對。
            foreach (var old in oldPortfolios)
            {
                if (string.IsNullOrWhiteSpace(old.FilePath)) continue;
                foreach (var singlePath in old.FilePath.Split('|', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (keptFilePaths.Contains(singlePath)) continue;
                    var relativePath = singlePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
                    var physicalPath = Path.Combine(_env.WebRootPath, relativePath);
                    if (System.IO.File.Exists(physicalPath))
                    {
                        try { System.IO.File.Delete(physicalPath); } catch { /* 刪檔失敗不影響主流程，忽略即可 */ }
                    }
                }
            }
        }

        // 🎯 把職缺 CertRequired 這種自由文字（例如「PMP證照、Scrum Master 認證」「CEH、CISSP 或 ISO 27001 證照優先加分。」）
        //    拆成一段一段的候選關鍵字，用來跟 Certificatecategories 的證照名稱做模糊比對。
        //    常見的頓號/逗號/斜線/中英文「或」「及」「與」都當作分隔符號，
        //    再把「證照」「認證」「佳」「加分」「優先」這類語氣詞從候選字串頭尾清掉，保留比較乾淨的核心名稱。
        private List<string> ParseCertKeywords(string? certRequired)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(certRequired)) return result;

            var noiseWords = new[] { "證照", "認證", "佳", "加分", "優先", "尤佳", "者佳", "為佳", "。", "、" };

            var segments = System.Text.RegularExpressions.Regex.Split(
                certRequired, @"[、,，/；;]|或|及|與");

            foreach (var seg in segments)
            {
                var s = seg.Trim();
                foreach (var noise in noiseWords)
                {
                    s = s.Replace(noise, "");
                }
                s = s.Trim();

                // 過濾掉「無特定要求」「不限」「無」這種代表沒有要求的片語，不當作關鍵字
                if (string.IsNullOrWhiteSpace(s)) continue;
                if (s.Contains("無特定要求") || s == "不限" || s == "無") continue;

                result.Add(s);
            }

            return result;
        }

        [HttpGet]
        public async Task<IActionResult> GetCertificates(int? jobId)
        {
            try
            {
                // 💡 1. 儘量在資料庫端就先把名稱 Trim 好，減少記憶體浪費
                var dbCerts = await _db.Certificatecategories
                    .Select(c => new {
                        CertName = c.CertName != null ? c.CertName.Trim() : "",
                        c.AvailableLevels
                    })
                    .ToListAsync();

                // 🎯 如果有帶 jobId，撈出該職缺的 CertRequired 文字，拆出關鍵字列表，用來標記「推薦」
                List<string> certKeywords = new List<string>();
                if (jobId.HasValue)
                {
                    var job = await _db.Jobs.FindAsync(jobId.Value);
                    certKeywords = ParseCertKeywords(job?.CertRequired);
                }

                // 💡 2. 記憶體內處理字串切分與補「級」字邏輯
                var certs = dbCerts.Select(c => new
                {
                    c.CertName,
                    Levels = (c.AvailableLevels ?? "")
                        .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(l =>
                        {
                            var val = l.Trim();
                            // 如果是單字 甲/乙/丙/丁 且結尾不是級，就自動補上「級」，否則保持原樣（如：單一級）
                            return (val.Length == 1 && "甲乙丙丁".Contains(val)) ? val + "級" : val;
                        })
                        .ToArray(),
                    // 🎯 雙向模糊比對：職缺關鍵字包含證照名稱、或證照名稱包含職缺關鍵字，只要有一邊命中就算推薦
                    //    （例如關鍵字「AWS Certified Cloud Practitioner」完全等於證照名稱；
                    //      關鍵字「多益(TOEIC) 900分以上」則包含證照名稱「多益 TOEIC」）
                    IsRecommended = certKeywords.Any(k =>
                        !string.IsNullOrWhiteSpace(c.CertName) &&
                        (k.IndexOf(c.CertName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                         c.CertName.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0))
                })
                .OrderByDescending(c => c.IsRecommended) // 推薦的排最前面
                .ToList();

                // 強制使用 PascalCase (不改動屬性大小寫)
                return Json(certs, new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNamingPolicy = null
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = $"資料庫讀取失敗: {ex.Message}" });
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
            var dbCertificates = await _db.Certificates.Where(s => s.ResumeId == model.Id).ToListAsync();
            var dbEducations = await _db.Educations.Where(e => e.ResumeId == model.Id).OrderBy(e => e.SortOrder).ToListAsync();
            // 🎯 Word 範本（履歷表.docx）的學歷欄位是單一列版面，這裡先用「第一筆＝最高學歷」帶入既有欄位。
            //    若要在匯出檔案顯示多筆學歷，範本本身也需要改成可重複的表格區塊，非純程式碼能解決。
            var topEducation = dbEducations.FirstOrDefault();
            var dbWorkExperiences = await _db.WorkExperiences.Where(w => w.ResumeId == model.Id).OrderBy(w => w.SortOrder).ToListAsync();
            // 🎯 同樣道理，工作經歷範本也只有一列，取第一筆帶入
            var topWorkExperience = dbWorkExperiences.FirstOrDefault();

            string realName = member.Name ?? "";
            string realGender = member.Gender ?? "";
            string realIdNumber = member.IdNumber ?? "";
            string realBirthday = member.Birthday.ToString("yyyy/MM/dd");
            string realEmail = member.Email ?? "";
            string realAddress = model.ContactAddress ?? ""; // 🎯 改抓履歷自身儲存的地址，而非會員即時地址

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
            string edu = topEducation?.EduStatus ?? "";
            string cert = FormatCertificatesString(dbCertificates);

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
                ["Email"] = realEmail,

                ["E_Dr"] = (topEducation?.EduLevel == "博士") ? ck : un,
                ["E_Ms"] = (topEducation?.EduLevel == "碩士") ? ck : un,
                ["E_Uni"] = (topEducation?.EduLevel == "大學") ? ck : un,
                ["E_Col"] = (topEducation?.EduLevel == "專科") ? ck : un,
                ["E_Voc"] = (topEducation?.EduLevel == "高職") ? ck : un,
                ["E_High"] = (topEducation?.EduLevel == "高中") ? ck : un,
                ["E_Jun"] = (topEducation?.EduLevel == "國中") ? ck : un,
                ["E_Pri"] = (topEducation?.EduLevel == "國小") ? ck : un,
                ["E_Other"] = (!string.IsNullOrEmpty(topEducation?.EduLevel) && !new[] { "博士", "碩士", "大學", "專科", "高職", "高中", "國中", "國小" }.Contains(topEducation?.EduLevel)) ? ck : un,
                ["OtherEdu"] = topEducation?.EduLevel ?? "______",

                ["SchoolName"] = topEducation?.SchoolName ?? "",
                ["Major"] = topEducation?.Major ?? "",
                ["E_Grad"] = edu.Contains("畢業") ? ck : un,
                ["E_Under"] = edu.Contains("肄業") ? ck : un,
                ["E_Stud"] = edu.Contains("在學") ? ck : un,

                ["EduDate"] = topEducation?.EndDate?.ToString("yyyy/MM") ?? "", // 🎯 範本目前只有一個「年月」欄位，先對應到結束(畢業)年月；若要同時顯示入學年月，需在 .docx 範本裡新增一個 {{EduStartDate}} 佔位符後，再多傳一組 ["EduStartDate"] = topEducation?.StartDate?.ToString("yyyy/MM") ?? "",

                ["WorkExp"] = (model.WorkExperienceYears >= 1) ? ck : un,
                ["NoWorkExp"] = (model.WorkExperienceYears == 0) ? ck : un,
                ["WorkExperienceYears"] = (model.WorkExperienceYears == -1) ? "0" : model.WorkExperienceYears.ToString(),

                ["CompanyName"] = topWorkExperience?.CompanyName ?? "",
                ["JobTitle"] = topWorkExperience?.JobTitle ?? "",
                ["JobDescription"] = topWorkExperience?.JobDescription ?? "",
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

            // 🎯 樣板（履歷表.docx）的學歷／工作經歷／作品集區塊已改成可重複列（MiniWord Table 語法：{{ListKey.Field}}），
            //    這裡把整批資料（不只第一筆）傳入，樣板會依筆數自動重複整列。
            string FormatDateRange(DateTime? start, DateTime? end) =>
                $"{(start?.ToString("yyyy/MM") ?? "")} - {(end?.ToString("yyyy/MM") ?? "")}";

            value["Educations"] = dbEducations.Select(e => new Dictionary<string, object>
            {
                ["SchoolName"] = e.SchoolName ?? "",
                ["Major"] = e.Major ?? "",
                ["EduStatus"] = e.EduStatus ?? "",
                ["EduDate"] = FormatDateRange(e.StartDate, e.EndDate),
            }).ToList();

            value["WorkExperiences"] = dbWorkExperiences.Select(w => new Dictionary<string, object>
            {
                ["CompanyName"] = w.CompanyName ?? "",
                ["JobTitle"] = w.JobTitle ?? "",
                ["JobDescription"] = w.JobDescription ?? "",
                ["DateRange"] = FormatDateRange(w.StartDate, w.EndDate),
            }).ToList();

            var dbPortfolios = await _db.Portfolios.Where(p => p.ResumeId == model.Id).OrderBy(p => p.SortOrder).ToListAsync();
            value["Portfolios"] = dbPortfolios.Select(p => new Dictionary<string, object>
            {
                ["Title"] = p.Title ?? "",
                ["Description"] = p.Description ?? "",
                ["Link"] = p.Link ?? "",
            }).ToList();

            var certList = cert.Split(", ").ToList();
            for (int i = 1; i <= 3; i++)
            {
                // 直接拿第 i-1 個陣列元素
                var currentCert = dbCertificates.ElementAtOrDefault(i - 1);

                if (currentCert != null)
                {
                    // 直接讀取新表的 Cname 與 Levels 欄位
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