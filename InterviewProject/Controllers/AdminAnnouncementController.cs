using InterviewProject.Models;
using InterviewProject.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace InterviewProject.Controllers
{
    public class AdminAnnouncementController : Controller
    {
        private readonly AppDbContext _db;

        public AdminAnnouncementController(AppDbContext db)
        {
            _db = db;
        }

        public async Task<IActionResult> Index()
        {
            // 驗證登入狀態
            var memberId = HttpContext.Session.GetInt32("MemberId");
            if (memberId == null)
                return RedirectToAction("Index", "Login");

            // 驗證角色：只有 hr 可以進入
            var role = HttpContext.Session.GetString("MemberRole")?.ToLower();
            if (role != "hr")
                return RedirectToAction("Index", "Home");

            ViewBag.MemberRole = role;
            ViewBag.MemberId   = memberId;

            // 🎯 核心修正：從資料庫撈出所有公告（依日期由新到舊排序），並傳送給 View
            var announcements = await _db.Announcements
                                        .OrderByDescending(a => a.Date)
                                        .ToListAsync();

            return View(announcements);
        }

        // ==============================
        // 新增公告（GET）
        // ==============================
        public IActionResult Create()
        {
            var memberId = HttpContext.Session.GetInt32("MemberId");
            if (memberId == null) return RedirectToAction("Index", "Login");

            var role = HttpContext.Session.GetString("MemberRole")?.ToLower();
            if (role != "hr") return RedirectToAction("Index", "Home");

            return View();
        }

        // 新增公告（POST）
        [HttpPost]
        public async Task<IActionResult> Create(string title, string content,
            string category, DateTime date, bool isActive)
        {
            var memberId = HttpContext.Session.GetInt32("MemberId");
            if (memberId == null) return RedirectToAction("Index", "Login");

            _db.Announcements.Add(new Announcement
            {
                Title     = title,
                Content   = content,
                Category  = category,
                Date      = date,
                IsActive  = isActive,
                CreatedAt = DateTime.UtcNow
            });

            await _db.SaveChangesAsync();
            TempData["Success"] = "公告已新增";
            return RedirectToAction("Index");
        }

        // ==============================
        // 編輯公告（GET）
        // ==============================
        public async Task<IActionResult> Edit(int id)
        {
            var memberId = HttpContext.Session.GetInt32("MemberId");
            if (memberId == null) return RedirectToAction("Index", "Login");

            var role = HttpContext.Session.GetString("MemberRole")?.ToLower();
            if (role != "hr") return RedirectToAction("Index", "Home");

            var announcement = await _db.Announcements.FindAsync(id);
            if (announcement == null) return NotFound();

            return View(announcement);
        }

        // 編輯公告（POST）
        [HttpPost]
        public async Task<IActionResult> Edit(int id, string title, string content,
            string category, DateTime date, bool isActive)
        {
            var announcement = await _db.Announcements.FindAsync(id);
            if (announcement == null) return NotFound();

            announcement.Title    = title;
            announcement.Content  = content;
            announcement.Category = category;
            announcement.Date     = date;
            announcement.IsActive = isActive;

            await _db.SaveChangesAsync();
            TempData["Success"] = "公告已更新";
            return RedirectToAction("Index");
        }

        // ==============================
        // 刪除公告（POST）
        // ==============================
        [HttpPost]
        public async Task<IActionResult> Delete(int id)
        {
            var announcement = await _db.Announcements.FindAsync(id);
            if (announcement != null)
            {
                _db.Announcements.Remove(announcement);
                await _db.SaveChangesAsync();
            }
            TempData["Success"] = "公告已刪除";
            return RedirectToAction("Index");
        }

        // ==============================
        // 切換顯示狀態（AJAX）
        // ==============================
        [HttpPost]
        public async Task<IActionResult> ToggleActive(int id)
        {
            var announcement = await _db.Announcements.FindAsync(id);
            if (announcement == null) return NotFound();

            announcement.IsActive = !announcement.IsActive;
            await _db.SaveChangesAsync();

            return Json(new { isActive = announcement.IsActive });
        }
    }
}