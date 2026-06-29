using InterviewProject.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace InterviewProject.Controllers
{
    public class AdminHomeController : Controller
    {
        private readonly AppDbContext _db;

        public AdminHomeController(AppDbContext db)
        {
            _db = db;
        }

        // 🎯 方法名改為 Dashboard，完美對應 Dashboard.cshtml 檔案
        // GET: /AdminHome/Dashboard
        public async Task<IActionResult> Dashboard()
        {
            var memberId = HttpContext.Session.GetInt32("MemberId");
            var role = HttpContext.Session.GetString("MemberRole")?.ToLower() ?? "";
            var memberName = HttpContext.Session.GetString("MemberName") ?? "使用者";

            // 🎯 防踢與權限檢查
            if (memberId == null || (role != "hr" && role != "manager" && role != "director"))
            {
                return RedirectToAction("Index", "Login");
            }

            var currentEmployee = await _db.Employees.FirstOrDefaultAsync(e => e.Id == memberId);
            if (currentEmployee == null)
            {
                HttpContext.Session.Clear();
                return RedirectToAction("Index", "Login");
            }

            ViewData["EmployeeName"] = memberName;
            ViewData["RoleLabel"] = role == "director" ? "部門最高主管" : role == "manager" ? "部門主管" : "人資系統管理員";

            string? department = currentEmployee.Department;

            // 🎯 建立履歷查詢基底
            var resumeQuery = _db.Resumes
                .Include(r => r.Job)
                .AsQueryable();

            // 🎯 核心分流邏輯
            if (role == "hr")
            {
                ViewData["ViewScope"] = "全公司 (All Departments)";
                ViewData["DepartmentInfo"] = "所有部門數據總覽";
            }
            else if (role == "manager" || role == "director")
            {
                if (string.IsNullOrEmpty(department))
                {
                    department = "未分配部門";
                }

                resumeQuery = resumeQuery.Where(r => r.Job != null && r.Job.Department == department);

                ViewData["ViewScope"] = $"僅限本部門 ({department})";
                ViewData["DepartmentInfo"] = $"您目前正以主管身分審視【{department}】的內部招募數據";
            }

            // 🎯 KPI 統計
            int totalResumes = await resumeQuery.CountAsync();
            int pendingResumes = await resumeQuery.CountAsync(r => r.Status == "待審核");
            int interviewCount = await resumeQuery.CountAsync(r => r.Status == "已安排面試" || r.Status == "面試中");
            int hiredCount = await resumeQuery.CountAsync(r => r.Status == "錄取");
            int rejectedCount = await resumeQuery.CountAsync(r => r.Status == "未通過" || r.Status == "不錄取" || r.Status == "不通過");

            double hireRate = totalResumes == 0 ? 0 : Math.Round(hiredCount * 100.0 / totalResumes, 1);

            ViewData["TotalResumes"] = totalResumes;
            ViewData["PendingResumes"] = pendingResumes;
            ViewData["InterviewCount"] = interviewCount;
            ViewData["HiredCount"] = hiredCount;
            ViewData["RejectedCount"] = rejectedCount;
            ViewData["HireRate"] = hireRate;

            // 🎯 百分比條狀圖用
            ViewData["PendingPercent"] = totalResumes == 0 ? 0 : Math.Round(pendingResumes * 100.0 / totalResumes, 1);
            ViewData["InterviewPercent"] = totalResumes == 0 ? 0 : Math.Round(interviewCount * 100.0 / totalResumes, 1);
            ViewData["HiredPercent"] = totalResumes == 0 ? 0 : Math.Round(hiredCount * 100.0 / totalResumes, 1);
            ViewData["RejectedPercent"] = totalResumes == 0 ? 0 : Math.Round(rejectedCount * 100.0 / totalResumes, 1);

            // 🎯 今日 / 本月履歷
            var today = DateTime.Today;
            var firstDayOfMonth = new DateTime(today.Year, today.Month, 1);

            ViewData["TodayResumes"] = await resumeQuery.CountAsync(r => r.ResumeTime.Date == today);
            ViewData["ThisMonthResumes"] = await resumeQuery.CountAsync(r => r.ResumeTime >= firstDayOfMonth);

            // 🎯 AI 平均分數
            double averageAiScore = await resumeQuery
                .Where(r => r.AiScore != null)
                .AverageAsync(r => (double?)r.AiScore) ?? 0;

            ViewData["AverageAiScore"] = Math.Round(averageAiScore, 1);

            // 🎯 平均面試分數
            var interviewQuery = _db.InterviewSchedules
                .Include(i => i.Resume)
                .ThenInclude(r => r.Job)
                .AsQueryable();

            if (role == "manager" || role == "director")
            {
                interviewQuery = interviewQuery.Where(i => i.Resume != null && i.Resume.Job != null && i.Resume.Job.Department == department);
            }

            double averageInterviewScore = await interviewQuery
                .Where(i => i.ResultScore != null)
                .AverageAsync(i => (double?)i.ResultScore) ?? 0;

            ViewData["AverageInterviewScore"] = Math.Round(averageInterviewScore, 1);

            // 🎯 最新 5 筆履歷
            var latestResumes = await resumeQuery
                .Include(r => r.Member)
                .Include(r => r.Job)
                .OrderByDescending(r => r.ResumeTime)
                .Take(5)
                .ToListAsync();

            ViewData["LatestResumes"] = latestResumes;

            // 💡 這樣呼叫，MVC 就會自動去抓 Views/AdminHome/Dashboard.cshtml
            return View();
        }
    }
}