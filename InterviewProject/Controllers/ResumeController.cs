using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using InterviewProject.Data;
using InterviewProject.Models;
using MiniSoftware;
using Spire.Doc;
using System.IO;

namespace InterviewProject.Controllers
{
    public class ResumeController : Controller
    {
        private readonly IWebHostEnvironment _env;
        private readonly AppDbContext _context;

        public ResumeController(IWebHostEnvironment env, AppDbContext context)
        {
            _env = env;
            _context = context;
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
        // 1. 新增：供 Job_detail 下拉選單抓取該使用者所有的職位
        [HttpGet]
        public async Task<IActionResult> GetSavedPositions()
        {
            int userId = GetCurrentUserId();
            if (userId == 0) return Json(new List<string>());

            var positions = await _context.Resume
                .Where(r => r.UserId == userId)
                .Select(r => r.Position)
                .Distinct()
                .ToListAsync();

            return Json(positions);
        }

        // 2. 頁面進入點
        public async Task<IActionResult> Resume(bool isNew = false, string position = "", string fromPos = "", string mode = "")
        {
            int userId = GetCurrentUserId();
            if (userId == 0)
            {
                // 修正這裡：導向 Login 控制器的 Index Action
                return RedirectToAction("Index", "Login");
            }

            ViewBag.ViewMode = mode;

            if (mode == "new")
            {
                return View(new Resume { UserId = userId, Position = position });
            }
            else if (mode == "apply")
            {
                var existingData = await _context.Resume
                    .FirstOrDefaultAsync(r => r.UserId == userId && r.Position == fromPos);

                if (existingData != null)
                {
                    existingData.Id = 0;
                    existingData.Position = position;
                    existingData.UserId = userId; // 確保是當前 User
                    return View(existingData);
                }
            }

            var model = await _context.Resume.FirstOrDefaultAsync(r => r.UserId == userId && r.Position == position);
            return View(model ?? new Resume { UserId = userId, Position = position });
        }

        // 3. 儲存邏輯
        [HttpPost]
        public async Task<IActionResult> SaveResume(Resume model)
        {
            ModelState.Remove("ResumeTime");
            // 因為 UserId 是從後端抓的，前端傳回來的 model.UserId 可能不安全，我們手動補上
            int userId = GetCurrentUserId();
            model.UserId = userId;

            if (!ModelState.IsValid) return View("Resume", model);

            var existing = await _context.Resume
                .FirstOrDefaultAsync(r => r.UserId == userId && r.Position == model.Position);

            DateTime now = DateTime.UtcNow.AddHours(8);

            if (existing == null)
            {
                model.Id = 0;
                model.ResumeTime = now;
                _context.Resume.Add(model);
            }
            else
            {
                // 更新時，保持原有 ID
                model.Id = existing.Id;
                model.ResumeTime = now;
                _context.Entry(existing).CurrentValues.SetValues(model);
            }

            await _context.SaveChangesAsync();
            return RedirectToAction("Resume", new { isNew = false, position = model.Position });
        }

        // 按鈕：匯出 PDF
        [HttpPost]
        public IActionResult ExportToPdf(Resume model)
        {
            if (string.IsNullOrEmpty(model.Name))
            {
                return Content("姓名為必填，否則無法生成 PDF。");
            }

            string templatePath = Path.Combine(_env.WebRootPath, "file", "履歷表.docx");
            if (!System.IO.File.Exists(templatePath))
            {
                return Content($"找不到 Word 範本檔案：{templatePath}");
            }

            // 取得串接字串
            string lang = model.LanguageSkills ?? "";
            string lic = model.DriverLicense ?? "";
            string comp = model.ComputerSkills ?? "";
            string spec = model.Specialty ?? "";
            string cert = model.Certificates ?? "";
            string edu = model.EduStatus ?? "";

            // 定義符號
            string ck = "■";
            string un = "□";

            var value = new Dictionary<string, object>()
            {
                //基本資料
                ["Name"] = model.Name ?? "",
                ["G_M"] = (model.Gender == "男") ? ck : un,
                ["G_F"] = (model.Gender == "女") ? ck : un,
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
                ["Email"] = model.Email ?? "",

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
                ["OtherEdu"] = model.EduLevel?.Contains("其他") == true ? model.EduLevel : "______",

                ["SchoolName"] = model.SchoolName ?? "",
                ["Major"] = model.Major ?? "",
                ["E_Grad"] = edu.Contains("畢業") ? ck : un,
                ["E_Under"] = edu.Contains("肄業") ? ck : un,
                ["E_Stud"] = edu.Contains("在學") ? ck : un,
                ["EduDate"] = model.EduDate ?? "",
                
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

                ["L_Other_1"] = lang.Contains("其他(精通)") ? ck : un,
                ["L_Other_2"] = lang.Contains("其他(良好)") ? ck : un,
                ["L_Other_3"] = lang.Contains("其他(普通)") ? ck : un,
                ["L_Other_4"] = lang.Contains("其他(稍懂)") ? ck : un,

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
                // 前端用 "; " 分隔，這裡拆分後填入
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
                ["C_Other"] = comp.Contains("其他:") ? ck : un // 只要有 "其他:" 字眼就勾選
            };

            

            // 證照級別解析邏輯 (針對範本中的 1. 與 2. 進行解析)
            var certList = cert.Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 1; i <= 2; i++)
            {
                string currentCert = certList.ElementAtOrDefault(i - 1) ?? "";
                value[$"C{i}_Name"] = currentCert.Split('(')[0];
                value[$"C{i}_A"] = currentCert.Contains("甲") ? ck : un;
                value[$"C{i}_B"] = currentCert.Contains("乙") ? ck : un;
                value[$"C{i}_C"] = currentCert.Contains("丙") ? ck : un;
                value[$"C{i}_S"] = currentCert.Contains("單一級") ? ck : un;
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
                        return File(pdfStream.ToArray(), "application/pdf", $"{model.Name}_履歷表.pdf");
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