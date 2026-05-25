using DocumentFormat.OpenXml.Spreadsheet;
using InterviewProject.Data;
using InterviewProject.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MiniSoftware;
using Spire.Doc;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace InterviewProject.Controllers
{
    public class ResumeController : Controller
    {
        private readonly IWebHostEnvironment _env;
        private readonly AppDbContext _db;

        public ResumeController(IWebHostEnvironment env, AppDbContext context)
        {
            _env = env;
            _db = context;
        }

        public async Task<IActionResult> CreateResume()
        {
            int userId = GetCurrentUserId();
            if (userId == 0) return RedirectToAction("Index", "Login");

            // 1. 抓取 Member 資料 (為了顯示姓名與性別)
            var member = await _db.Members.FirstOrDefaultAsync(m => m.Id == userId);

            if (member == null) return NotFound();

            // 2. 將姓名與性別存入 ViewBag 供 View 顯示（因為 Resume 表不存這些）
            ViewBag.UserName = member.Name;
            ViewBag.UserGender = member.Gender;
            ViewBag.UserEmail = member.Email;

            // 3. 建立新的 Resume 物件，僅賦值資料庫有的欄位
            var resume = new Resume
            {
                UserId = userId,
            };

            // 返回 View
            return View(resume);
        }

        // 在 Controller 內取得當前登入者 ID 的方法
        private int GetCurrentUserId()
        {
            // 改為從 Session 抓取 "MemberId"
            int? userId = HttpContext.Session.GetInt32("MemberId");

            if (userId.HasValue)
            {
                return userId.Value;
            }

            // 如果 Session 抓不到，代表沒登入或是 Session 過期
            return 0;
        }

        // 頁面進入：讀取資料
        // 1. 修正：供 Job_detail 下拉選單抓取該使用者所有的職位 Id (傳回整數清單)
        [HttpGet]
        public async Task<IActionResult> GetSavedPositions()
        {
            int userId = GetCurrentUserId();
            if (userId == 0) return Json(new List<int>());

            var positions = await _db.Resumes
                .Where(r => r.UserId == userId)
                .Select(r => r.Position)
                .Distinct()
                .ToListAsync();

            return Json(positions);
        }

        // 2. 頁面進入點 (🎯 精準修正：將原本的字串 position / fromPos 調整為整數 jobId / fromJobId)
        public async Task<IActionResult> Resume(int jobId, int fromJobId = 0, string mode = "")
        {
            int userId = GetCurrentUserId();
            if (userId == 0) return RedirectToAction("Index", "Login");

            // 將當前點擊的職位 Id 與 Mode 存入 ViewBag，確保 View 隨時拿得到
            ViewBag.CurrentJobId = jobId;
            ViewBag.ViewMode = mode;

            var member = await _db.Members.FirstOrDefaultAsync(m => m.Id == userId);
            ViewBag.UserEmail = member?.Email ?? "";
            ViewBag.UserName = member?.Name ?? "";
            ViewBag.UserGender = member?.Gender ?? "";

            // 1. 套用現有履歷模式 (🎯 修正行 99、111 與 116 的強型別比對錯誤)
            if (mode == "apply" && fromJobId != 0)
            {
                var existingData = await _db.Resumes
                    .Include(r => r.Job)
                    .FirstOrDefaultAsync(r => r.UserId == userId && r.Position == fromJobId);

                if (existingData != null)
                {
                    existingData.Id = 0;
                    existingData.Position = jobId; // 強制將職位外鍵設為當前點擊的這個職缺 Id
                    return View(existingData);
                }
            }

            // 2. 一般模式 (讀取或新建) - 加上 .Include(r => r.Job) 確保前端看得到 Job.Title
            var model = await _db.Resumes
                .Include(r => r.Job)
                .FirstOrDefaultAsync(r => r.UserId == userId && r.Position == jobId);

            if (model == null)
            {
                // 如果是新應徵，建立空物件並帶入職位 Id
                model = new Resume { UserId = userId, Position = jobId };

                // 動態加載職缺物件，防止前端讀取 Model.Job?.Title 報空值
                model.Job = await _db.Jobs.FindAsync(jobId);
            }

            return View(model);
        }

        // 3. 儲存邏輯
        [HttpPost]
        public async Task<IActionResult> SaveResume(Resume model)
        {
            ModelState.Remove("ResumeTime");
            ModelState.Remove("Job"); // 移除對導覽屬性的驗證

            int userId = GetCurrentUserId();
            model.UserId = userId;

            if (!ModelState.IsValid)
            {
                // 若驗證失敗，回填 Job 導覽屬性，避免前端畫面拋出空指標
                model.Job = await _db.Jobs.FindAsync(model.Position);
                return View("Resume", model);
            }

            var existing = await _db.Resumes
                .FirstOrDefaultAsync(r => r.UserId == userId && r.Position == model.Position);

            DateTime now = DateTime.Now;

            if (existing == null)
            {
                model.Id = 0;
                model.ResumeTime = now;
                model.Status = "待審核"; // 新增時預設狀態
                _db.Resumes.Add(model);
            }
            else
            {
                model.Id = existing.Id;
                model.ResumeTime = now;
                model.Status = existing.Status ?? "待審核";
                _db.Entry(existing).CurrentValues.SetValues(model);
            }

            await _db.SaveChangesAsync();
            return RedirectToAction("Job_search", "Job");
        }

        // 按鈕：匯出 PDF
        [HttpPost]
        public async Task<IActionResult> ExportToPdf(Resume model)
        {
            // 1. 驗證姓名
            var member = await _db.Members.FirstOrDefaultAsync(m => m.Id == model.UserId);
            if (member == null) return Content("找不到會員資料");

            string realName = member.Name ?? "";
            string realGender = member.Gender ?? "";
            string realEmail = member.Email ?? "";

            string templatePath = Path.Combine(_env.WebRootPath, "file", "履歷表.docx");
            if (!System.IO.File.Exists(templatePath))
            {
                return Content($"找不到 Word 範本檔案：{templatePath}");
            }

            // 定義符號
            string ck = "■";
            string un = "□";

            // 取得串接字串
            string lang = model.LanguageSkills ?? "";
            string[] knownLangs = { "英語", "日語", "台語", "客語", "不具外文能力" };

            string lic = model.DriverLicense ?? "";
            string comp = model.ComputerSkills ?? "";
            string spec = model.Specialty ?? "";
            string cert = model.Certificates ?? "";
            string edu = model.EduStatus ?? "";

            var value = new Dictionary<string, object>()
            {
                //基本資料
                ["Name"] = realName,
                ["G_M"] = (realGender == "男") ? ck : un,
                ["G_F"] = (realGender == "女") ? ck : un,
                ["IdNumber"] = model.IdNumber ?? "",
                ["Birthday"] = model.Birthday?.ToString("yyyy/MM/dd") ?? "",
                ["ZipCode"] = model.ZipCode ?? "",
                ["Address"] = model.Address ?? "",
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

                //學歷
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

                // 🎯 核心修正：EduDate 已經演化為 DateTime?，改用標準時間格式化輸出，移除了不相容的 ?? "" 串接
                ["EduDate"] = model.EduDate?.ToString("yyyy/MM") ?? "",

                // 如果年資 >= 1，WorkExp 顯示 ■，否則顯示 □
                ["WorkExp"] = (model.WorkExperienceYears >= 1) ? "■" : "□",
                ["NoWorkExp"] = (model.WorkExperienceYears < 1) ? "■" : "□",

                //工作經驗
                ["WorkExperienceYears"] = model.WorkExperienceYears.ToString(),
                ["CompanyName"] = model.CompanyName ?? "",
                ["JobTitle"] = model.JobTitle ?? "",
                ["JobDescription"] = model.JobDescription ?? "",
                ["Autobiography"] = model.Autobiography ?? "",

                //背景及專長
                ["L_None"] = lang.Contains("不具外文能力") ? ck : un,
                ["L_Eng_1"] = lang.Contains("英語(精通)") ? ck : un,
                ["L_Eng_2"] = lang.Contains("英語(良好)") ? ck : un,
                ["L_Eng_3"] = lang.Contains("英語(普通)") ? ck : un,
                ["L_Eng_4"] = lang.Contains("英語(稍懂)") ? ck : un,

                ["L_Jap_1"] = lang.Contains("日語(精通)") ? ck : un,
                ["L_Jap_2"] = lang.Contains("日語(良好)") ? ck : un,
                ["L_Jap_3"] = lang.Contains("日語(普通)") ? ck : un,
                ["L_Jap_4"] = lang.Contains("日語(稍懂)") ? ck : un,

                ["L_Twn_1"] = lang.Contains("台語(精通)") ? ck : un,
                ["L_Twn_2"] = lang.Contains("台語(良好)") ? ck : un,
                ["L_Twn_3"] = lang.Contains("台語(普通)") ? ck : un,
                ["L_Twn_4"] = lang.Contains("台語(稍懂)") ? ck : un,

                ["L_Hakka_1"] = lang.Contains("客語(精通)") ? ck : un,
                ["L_Hakka_2"] = lang.Contains("客語(良好)") ? ck : un,
                ["L_Hakka_3"] = lang.Contains("客語(普通)") ? ck : un,
                ["L_Hakka_4"] = lang.Contains("客語(稍懂)") ? ck : un,

                //駕照種類
                ["D_Self_S"] = lic.Contains("自用(小)") ? ck : un,
                ["D_Self_B"] = lic.Contains("自用(大)") ? ck : un,
                ["D_Pro_S"] = lic.Contains("職業(小)") ? ck : un,
                ["D_Pro_B"] = lic.Contains("職業(大)") ? ck : un,
                ["D_Pro_K"] = lic.Contains("職業(客)") ? ck : un,
                ["D_Moto_L"] = lic.Contains("機車(輕型)") ? ck : un,
                ["D_Moto_H"] = lic.Contains("機車(重型)") ? ck : un,
                ["D_None"] = lic.Contains("無") ? ck : un,
                ["D_Own"] = lic.Contains("自備") ? ck : un,

                //專長
                ["Spec1"] = spec.Split(new[] { "; " }, StringSplitOptions.None).ElementAtOrDefault(0) ?? "",
                ["Spec2"] = spec.Split(new[] { "; " }, StringSplitOptions.None).ElementAtOrDefault(1) ?? "",
                ["Spec3"] = spec.Split(new[] { "; " }, StringSplitOptions.None).ElementAtOrDefault(2) ?? "",

                ["Cert"] = cert,

                //電腦能力
                ["C_Base"] = comp.Contains("電腦基本操作") ? ck : un,
                ["C_Doc"] = comp.Contains("文書處理") ? ck : un,
                ["C_Net"] = comp.Contains("網際網路") ? ck : un,
                ["C_Web"] = comp.Contains("網頁編輯") ? ck : un,
                ["C_Biz"] = comp.Contains("商業軟體") ? ck : un,
                ["C_Prog"] = comp.Contains("程式設計") ? ck : un,
                ["C_Other"] = comp.Contains("其他:") ? ck : un
            };

            // 1. 找出不屬於已知語言的項目 (例如：韓語(普通))
            var otherLangItem = lang.Split(", ")
                .FirstOrDefault(s => !knownLangs.Any(k => s.StartsWith(k)) && s.Contains("("));

            string otherLevel = "";
            string otherName = "";

            if (otherLangItem != null)
            {
                otherName = otherLangItem.Split('(')[0];
                otherLevel = otherLangItem.Split('(', ')')[1];
            }

            // 2. 填入 Dictionary
            value["L_Other_Name"] = otherName;

            value["L_Other_1"] = (otherLevel == "精通") ? ck : un;
            value["L_Other_2"] = (otherLevel == "良好") ? ck : un;
            value["L_Other_3"] = (otherLevel == "普通") ? ck : un;
            value["L_Other_4"] = (otherLevel == "稍懂") ? ck : un;

            // 證照級別解析邏輯
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

            // 處理電腦能力「其他」
            if (comp.Contains("其他:"))
            {
                value["C_Other"] = ck;
                int startIndex = comp.IndexOf("其他:") + 3;
                string remainingStr = comp.Substring(startIndex);
                string otherText = remainingStr.Split(',')[0].Trim();
                value["C_Other_Text"] = otherText;
            }
            else
            {
                value["C_Other"] = un;
                value["C_Other_Text"] = "";
            }

            try
            {
                using (MemoryStream ms = new MemoryStream())
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
                return Content($"導出 PDF 過程發生錯誤：{ex.Message}");
            }
        }
    }
}