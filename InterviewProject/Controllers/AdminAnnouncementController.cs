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

        public async Task<IActionResult> Index(string category, string status, DateTime? date, string keyword)
        {
            // 1. 驗證登入狀態
            var memberId = HttpContext.Session.GetInt32("MemberId");
            if (memberId == null)
                return RedirectToAction("Index", "Login");

            // 2. 驗證角色：只有 hr 可以進入
            var role = HttpContext.Session.GetString("MemberRole")?.ToLower();
            if (role != "hr")
                return RedirectToAction("Index", "Home");

            ViewBag.MemberRole = role;
            ViewBag.MemberId = memberId;

            var today = DateTime.Today;

            // 3. 建立動態查詢基礎 (IQueryable)
            var query = _db.Announcements.AsQueryable();

            // 🎯 類別篩選 (對應資料表 Category 欄位)
            if (!string.IsNullOrWhiteSpace(category))
            {
                query = query.Where(a => a.Category == category);
            }

            // 🎯 關鍵字搜尋 (標題或內文)
            if (!string.IsNullOrWhiteSpace(keyword))
            {
                query = query.Where(a => a.Title.Contains(keyword));
            }

            // 🎯 日期篩選：落在上架日 (SDate) ~ 下架日 (CDate) 之間
            if (date.HasValue)
            {
                query = query.Where(a => a.SDate.Date <= date.Value.Date && a.CDate.Date >= date.Value.Date);
            }

            // 🎯 上架狀態篩選
            if (!string.IsNullOrWhiteSpace(status))
            {
                switch (status)
                {
                    case "active":    // 上架中：IsActive 為 true 且 在有效期限內
                        query = query.Where(a => a.IsActive && a.SDate.Date <= today && a.CDate.Date >= today);
                        break;
                    case "upcoming":  // 未上架：IsActive 為 true 但 尚未到上架日
                        query = query.Where(a => a.IsActive && a.SDate.Date > today);
                        break;
                    case "inactive":  // 已下架：IsActive 為 false 或 已超過下架日
                        query = query.Where(a => !a.IsActive || a.CDate.Date < today);
                        break;
                }
            }

            // 4. 動態產生類別下拉選單的選項 (選取資料庫現有的不重複 Category)
            ViewBag.DepartmentOptions = await _db.Announcements
                                                 .Select(a => a.Category)
                                                 .Where(c => c != null)
                                                 .Distinct()
                                                 .ToListAsync();

            // 5. 保持 UI 狀態 (避免搜尋後下拉選單與輸入框的內容被清空)
            ViewBag.SelectedCategory = category;
            ViewBag.SelectedStatus = status;
            ViewBag.SelectedDate = date?.ToString("yyyy-MM-dd");
            ViewBag.Keyword = keyword;

            // 6. 排序並執行 SQL 查詢
            var announcements = await query.OrderByDescending(a => a.SDate)
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
            string category, DateTime sDate, DateTime cDate, bool isActive)
        {
            var memberId = HttpContext.Session.GetInt32("MemberId");
            if (memberId == null) return RedirectToAction("Index", "Login");

            if (cDate < sDate)
            {
                cDate = sDate;
            }

            _db.Announcements.Add(new Announcement
            {
                Title = title,
                Content = content,
                Category = category,
                SDate = sDate,
                CDate = cDate,
                IsActive = isActive,
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
            string category, DateTime sDate, DateTime cDate, bool isActive)
        {
            var announcement = await _db.Announcements.FindAsync(id);
            if (announcement == null) return NotFound();

            if (cDate < sDate)
            {
                cDate = sDate;
            }

            announcement.Title = title;
            announcement.Content = content;
            announcement.Category = category;
            announcement.SDate = sDate;
            announcement.CDate = cDate;
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
    }
}