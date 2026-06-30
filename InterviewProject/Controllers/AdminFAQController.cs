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
        // Q&A 後台管理列表
        // 權限：僅 hr
        // ==============================
        public async Task<IActionResult> Index()
        {
            if (!IsHr())
                return RedirectToAction("Index", "Login");

            var faqs = await _db.Faqs
                .OrderBy(f => f.SortOrder)
                .ThenByDescending(f => f.CreatedAt)
                .ToListAsync();

            return View(faqs);
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
    }
}