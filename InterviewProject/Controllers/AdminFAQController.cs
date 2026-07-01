using InterviewProject.Data;
using InterviewProject.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace InterviewProject.Controllers
{
    public class AdminFAQController : Controller
    {
        private readonly AppDbContext _db;

        public AdminFAQController(AppDbContext db)
        {
            _db = db;
        }

        // ==============================
        // 權限檢查：僅 hr
        // ==============================
        private bool IsHr()
        {
            var memberId = HttpContext.Session.GetInt32("MemberId");
            var role = HttpContext.Session.GetString("MemberRole")?.ToLower();

            return memberId != null && role == "hr";
        }

        // ==============================
        // 求職 Q&A 管理首頁
        // 包含：FAQ 管理 + Q&A 回報
        // 權限：hr / manager / director
        // ==============================
        public async Task<IActionResult> Index(string visitorStatus = "pending", string visitorSort = "asc", string jobseekerStatus = "pending", string jobseekerSort = "asc")
        {
            var memberId = HttpContext.Session.GetInt32("MemberId");
            if (memberId == null)
                return RedirectToAction("Index", "Login");

            var role = HttpContext.Session.GetString("MemberRole")?.ToLower();

            if (role != "hr" && role != "manager" && role != "director")
                return RedirectToAction("Index", "Home");

            ViewBag.MemberRole = role;
            ViewBag.MemberId = memberId;

            // FAQ 管理：只有 HR 需要看到
            var faqs = await _db.Faqs
                .OrderBy(f => f.SortOrder)
                .ThenByDescending(f => f.CreatedAt)
                .ToListAsync();

            // ── 訪客回覆 ──
            var visitorQuery = _db.FAQReports.Where(r => r.Role == "訪客");
            visitorQuery = ApplyStatusFilter(visitorQuery, visitorStatus);
            visitorQuery = ApplySort(visitorQuery, visitorSort);
            var visitorReports = await visitorQuery.ToListAsync();

            // ── 求職者回覆 ──
            var jobseekerQuery = _db.FAQReports.Where(r => r.Role == "求職者");
            jobseekerQuery = ApplyStatusFilter(jobseekerQuery, jobseekerStatus);
            jobseekerQuery = ApplySort(jobseekerQuery, jobseekerSort);
            var jobseekerReports = await jobseekerQuery.ToListAsync();

            ViewBag.VisitorReports = visitorReports;
            ViewBag.JobseekerReports = jobseekerReports;

            ViewBag.VisitorStatus = visitorStatus;
            ViewBag.VisitorSort = visitorSort;
            ViewBag.JobseekerStatus = jobseekerStatus;
            ViewBag.JobseekerSort = jobseekerSort;

            // 待處理數量，不受篩選條件影響，用於分頁籤上的提示數字
            ViewBag.VisitorPendingCount = await _db.FAQReports
                .CountAsync(r => r.Role == "訪客" && r.Status != "已回覆");
            ViewBag.JobseekerPendingCount = await _db.FAQReports
                .CountAsync(r => r.Role == "求職者" && r.Status != "已回覆");

            return View(faqs);
        }

        // ==============================
        // 狀態篩選：待處理(預設) / 已回覆 / 全部
        // ==============================
        private static IQueryable<FAQReport> ApplyStatusFilter(IQueryable<FAQReport> query, string status)
        {
            return status switch
            {
                "replied" => query.Where(r => r.Status == "已回覆"),
                "all" => query,
                _ => query.Where(r => r.Status != "已回覆") // 預設：待處理
            };
        }

        // ==============================
        // 時間排序：asc(預設，越早在上面) / desc
        // ==============================
        private static IQueryable<FAQReport> ApplySort(IQueryable<FAQReport> query, string sort)
        {
            return sort == "desc"
                ? query.OrderByDescending(r => r.CreatedAt)
                : query.OrderBy(r => r.CreatedAt);
        }

        // ==============================
        // 新增 FAQ 頁面
        // 權限：僅 hr
        // ==============================
        public IActionResult Create()
        {
            if (!IsHr())
                return RedirectToAction("Index", "Login");

            return View();
        }

        // ==============================
        // 新增 FAQ
        // 權限：僅 hr
        // ==============================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(FAQ faq)
        {   
            if (!IsHr())
                return RedirectToAction("Index", "Login");

            faq.CreatedAt = DateTime.Now;

            _db.Faqs.Add(faq);
            await _db.SaveChangesAsync();

            TempData["Success"] = "FAQ 已新增";
            return RedirectToAction("Index");
        }

        // ==============================
        // 編輯 FAQ 頁面
        // 權限：僅 hr
        // ==============================
        public async Task<IActionResult> Edit(int id)
        {
            if (!IsHr())
                return RedirectToAction("Index", "Login");

            var faq = await _db.Faqs.FindAsync(id);
            if (faq == null) return NotFound();

            return View(faq);
        }

        // ==============================
        // 儲存編輯 FAQ
        // 權限：僅 hr
        // ==============================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(FAQ faq)
        {
            if (!IsHr())
                return RedirectToAction("Index", "Login");

            var existing = await _db.Faqs.FindAsync(faq.Id);
            if (existing == null) return NotFound();

            existing.Question = faq.Question;
            existing.Answer = faq.Answer;
            existing.SortOrder = faq.SortOrder;
            existing.IsActive = faq.IsActive;

            await _db.SaveChangesAsync();

            TempData["Success"] = "FAQ 已更新";
            return RedirectToAction("Index");
        }

        // ==============================
        // 刪除 FAQ
        // 權限：僅 hr
        // ==============================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            if (!IsHr())
                return RedirectToAction("Index", "Login");

            var faq = await _db.Faqs.FindAsync(id);
            if (faq == null) return NotFound();

            _db.Faqs.Remove(faq);
            await _db.SaveChangesAsync();

            TempData["Success"] = "FAQ 已刪除";
            return RedirectToAction("Index");
        }

        // ==============================
        // FAQ 上架 / 下架切換
        // 權限：僅 hr
        // ==============================
        [HttpPost]
        [Route("AdminFAQ/ToggleActive/{id}")]
        public async Task<IActionResult> ToggleActive(int id)
        {
            if (!IsHr())
                return Json(new { success = false, message = "權限不足" });

            var faq = await _db.Faqs.FindAsync(id);
            if (faq == null)
                return Json(new { success = false, message = "找不到 FAQ" });

            faq.IsActive = !faq.IsActive;
            await _db.SaveChangesAsync();

            return Json(new
            {
                success = true,
                isActive = faq.IsActive
            });
        }

        // ==============================
        // Q&A 回報詳細資料
        // 權限：hr / manager / director
        // ==============================
        public async Task<IActionResult> ReportDetail(int id)
        {
            var memberId = HttpContext.Session.GetInt32("MemberId");
            if (memberId == null)
                return RedirectToAction("Index", "Login");

            var role = HttpContext.Session.GetString("MemberRole")?.ToLower();

            if (role != "hr" &&
                role != "manager" &&
                role != "director")
                return RedirectToAction("Index", "Home");

            var report = await _db.FAQReports
                .FirstOrDefaultAsync(x => x.Id == id);

            if (report == null)
                return NotFound();

            return View(report);
        }

        // ==============================
        // Q&A 回報：回覆問題
        // 權限：HR / Manager / Director
        // ==============================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ReplyReport(
            int id,
            string replyContent,
            string? internalNote)
        {
            var memberId = HttpContext.Session.GetInt32("MemberId");
            if (memberId == null)
                return RedirectToAction("Index", "Login");

            var role = HttpContext.Session.GetString("MemberRole")?.ToLower();

            if (role != "hr" &&
                role != "manager" &&
                role != "director")
                return RedirectToAction("Index", "Home");

            var report = await _db.FAQReports.FindAsync(id);

            if (report == null)
                return NotFound();

            if (string.IsNullOrWhiteSpace(replyContent))
            {
                TempData["Error"] = "請輸入回覆內容。";
                return RedirectToAction(nameof(ReportDetail), new { id });
            }

            report.ReplyContent = replyContent;
            report.InternalNote = internalNote;
            report.Status = "已回覆";
            report.RepliedAt = DateTime.Now;

            await _db.SaveChangesAsync();

            TempData["Success"] = "Q&A 已成功回覆。";

            return RedirectToAction(nameof(ReportDetail), new { id });
        }

        // ==============================
        // Q&A 回報：轉交主管
        // 權限：僅 HR
        // ==============================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> TransferReport(
            int id,
            string assignedRole,
            string? internalNote)
        {
            if (!IsHr())
                return RedirectToAction("Index", "Login");

            var report = await _db.FAQReports.FindAsync(id);

            if (report == null)
                return NotFound();

            if (assignedRole != "manager" &&
                assignedRole != "director")
            {
                TempData["Error"] = "請選擇轉交對象。";
                return RedirectToAction(nameof(ReportDetail), new { id });
            }

            report.AssignedRole = assignedRole;
            report.InternalNote = internalNote;

            report.Status = assignedRole == "manager"
                ? "已轉主管"
                : "已轉最高主管";

            await _db.SaveChangesAsync();

            TempData["Success"] = "已成功轉交。";

            return RedirectToAction(nameof(ReportDetail), new { id });
        }
    }
}