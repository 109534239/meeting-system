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
            ViewData["RoleLabel"] = role == "director" ? "部門最高管理員" : role == "manager" ? "部門主管" : "人資系統管理員";

            // 🎯 核心分流邏輯
            if (role == "hr")
            {
                ViewData["ViewScope"] = "全公司 (All Departments)";
                ViewData["DepartmentInfo"] = "所有部門數據總覽";
            }
            else if (role == "manager" || role == "director")
            {
                string mockDepartmentName = "未分配部門";

                if (currentEmployee.Account == "manager01" || currentEmployee.Name == "王主管")
                {
                    mockDepartmentName = "資訊管理部 (IT)";
                }
                else
                {
                    mockDepartmentName = "通用業務部 (Sales)";
                }
                
                ViewData["ViewScope"] = $"僅限本部門 ({mockDepartmentName})";
                ViewData["DepartmentInfo"] = $"您目前正以主管身分審視【{mockDepartmentName}】的內部招募數據";
            }

            // 💡 這樣呼叫，MVC 就會自動去抓 Views/AdminHome/Dashboard.cshtml
            return View();
        }
    }
}