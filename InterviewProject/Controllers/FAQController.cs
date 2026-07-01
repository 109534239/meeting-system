using InterviewProject.Data;
using InterviewProject.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace InterviewProject.Controllers
{
    public class FAQController : Controller
    {
        private readonly AppDbContext _db;

        public FAQController(AppDbContext db)
        {
            _db = db;
        }

        // ==============================
        // 前台 FAQ 顯示
        // 訪客 / 求職者皆可查看
        // 只顯示 HR 已上架的 FAQ
        // ==============================
        public async Task<IActionResult> Index()
        {
            var faqs = await _db.Faqs
                .Where(x => x.IsActive)
                .OrderBy(x => x.SortOrder)
                .ThenByDescending(x => x.CreatedAt)
                .ToListAsync();

            return View(faqs);
        }

        // ==============================
        // 前台 Q&A 回報表單送出
        // 訪客 / 求職者皆可送出問題給 HR
        // ==============================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SubmitReport(FAQReport report, bool agree)
        {
            // 檢查是否同意個資條款
            if (!agree)
            {
                TempData["Error"] = "請先勾選同意個人資料保護法定應告知事項。";
                return RedirectToAction("Index");
            }

            // 將 null 轉成空字串，避免 PostgreSQL NOT NULL 欄位錯誤
            report.Name = report.Name ?? "";
            report.Email = report.Email ?? "";
            report.Category = report.Category ?? "";
            report.Subject = report.Subject ?? "";
            report.Content = report.Content ?? "";

            // 基本欄位防呆
            if (string.IsNullOrWhiteSpace(report.Name) ||
                string.IsNullOrWhiteSpace(report.Email) ||
                string.IsNullOrWhiteSpace(report.Category) ||
                string.IsNullOrWhiteSpace(report.Subject) ||
                string.IsNullOrWhiteSpace(report.Content))
            {
                TempData["Error"] = "請完整填寫姓名、電子信箱、問題類別、信件主旨與信件內容。";
                return RedirectToAction("Index");
            }

            var memberId = HttpContext.Session.GetInt32("MemberId");
            var memberRole = HttpContext.Session.GetString("MemberRole");
            report.Role = (memberId != null && memberRole == "jobseeker") ? "求職者" : "訪客";

            report.Status = "待處理";
            report.CreatedAt = DateTime.Now;
            report.RepliedAt = null;
            report.Department = null;
            report.ReplyContent = null;

            _db.FAQReports.Add(report);
            await _db.SaveChangesAsync();

            TempData["Success"] = "您的問題已成功送出，我們將於 1～3 個工作天內與您聯繫，感謝您的來信！";
            return RedirectToAction("Index");
        }
    }
}