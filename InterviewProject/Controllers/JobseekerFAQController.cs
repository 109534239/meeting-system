using InterviewProject.Data;
using InterviewProject.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace InterviewProject.Controllers
{
    // ==============================
    // 求職者「我的 Q&A」
    // 權限：求職者（需登入會員）
    // 🎯 與 AdminFAQController 不同：
    //    1. 不分 FAQ 管理／訪客／求職者三個分頁，只有一個列表
    //    2. 列表顯示「與自己相關」的全部 Q&A，待處理、已回覆都在同一頁
    //    3. 沒有回覆功能，只能「查看」，看不到其他人的資料、看不到指派部門欄位
    // ==============================
    public class JobseekerFAQController : Controller
    {
        private readonly AppDbContext _db;

        public JobseekerFAQController(AppDbContext db)
        {
            _db = db;
        }

        // ⚠️ 請依實際系統中「求職者」登入後 Session 存的角色字串調整這裡的比對條件
        //    （目前先比照 AdminFAQController 的寫法，假設是 "jobseeker"）
        private bool IsJobseeker(out int memberId)
        {
            var id = HttpContext.Session.GetInt32("MemberId");
            var role = HttpContext.Session.GetString("MemberRole")?.ToLower();

            memberId = id ?? 0;
            return id != null && role == "jobseeker";
        }

        // ==============================
        // 我的 Q&A 列表
        // 不分分頁籤，一次顯示自己全部的 Q&A（待處理 + 已回覆）
        // 依建立時間新到舊排序
        // ==============================
        public async Task<IActionResult> Index()
        {
            if (!IsJobseeker(out int memberId))
                return RedirectToAction("Index", "Login");

            var myReports = await _db.FAQReports
                .Where(r => r.Role == "求職者" && r.MemberId == memberId)
                .OrderByDescending(r => r.CreatedAt)
                .ToListAsync();

            return View(myReports);
        }

        // ==============================
        // 查看單筆 Q&A 詳細內容（唯讀，不能回覆）
        // 🎯 只能查看自己的資料，避免用網址猜 id 看到別人的 Q&A
        // ==============================
        public async Task<IActionResult> Detail(int id)
        {
            if (!IsJobseeker(out int memberId))
                return RedirectToAction("Index", "Login");

            var report = await _db.FAQReports
                .FirstOrDefaultAsync(r => r.Id == id && r.Role == "求職者" && r.MemberId == memberId);

            if (report == null)
                return NotFound();

            return View(report);
        }
    }
}
