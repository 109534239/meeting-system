using InterviewProject.Data;
using InterviewProject.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace InterviewProject.Controllers
{
    public class DirectorManagerController : Controller
    {
        private readonly AppDbContext _db;
        public DirectorManagerController(AppDbContext db)
        {
            _db = db;
        }

        // 部門清單（與 Jobs 表一致）
        private static readonly List<string> Departments = new()
        {
            "專案管理", "資訊技術", "人力資源", "行銷企劃", "財務會計", "業務銷售"
        };

        // ==============================
        // 主管列表：只顯示自己部門的主管
        // ==============================
        public async Task<IActionResult> Index()
        {
            var memberId = HttpContext.Session.GetInt32("MemberId");
            if (memberId == null) return RedirectToAction("Index", "Login");

            var role = HttpContext.Session.GetString("MemberRole")?.ToLower();
            if (role != "director") return RedirectToAction("Index", "Home");

            // 💡 取得目前登入的 director 的部門
            var director = await _db.Employees.FindAsync(memberId);
            if (director == null) return RedirectToAction("Index", "Login");

            var myDepartment = director.Department;

            // 💡 只撈自己部門的主管
            var managers = await _db.Employees
                .Where(e => e.Role == "manager" && e.Department == myDepartment)
                .OrderBy(e => e.Name)
                .ToListAsync();

            ViewBag.MyDepartment = myDepartment;
            return View(managers);
        }

        // ==============================
        // 新增主管（GET）：部門固定為自己的部門
        // ==============================
        public async Task<IActionResult> Create()
        {
            var memberId = HttpContext.Session.GetInt32("MemberId");
            if (memberId == null) return RedirectToAction("Index", "Login");

            var role = HttpContext.Session.GetString("MemberRole")?.ToLower();
            if (role != "director") return RedirectToAction("Index", "Home");

            // 💡 取得 director 的部門，固定只能新增這個部門的主管
            var director = await _db.Employees.FindAsync(memberId);
            if (director == null) return RedirectToAction("Index", "Login");

            ViewBag.MyDepartment = director.Department;
            return View();
        }

        // 新增主管（POST）
        [HttpPost]
        public async Task<IActionResult> Create(string account, string name, string password)
        {
            var memberId = HttpContext.Session.GetInt32("MemberId");
            if (memberId == null) return RedirectToAction("Index", "Login");

            var director = await _db.Employees.FindAsync(memberId);
            if (director == null) return RedirectToAction("Index", "Login");

            // 💡 部門固定用 director 自己的部門，不從前端接收
            var department = director.Department;

            if (await _db.Employees.AnyAsync(e => e.Account == account))
            {
                TempData["Error"] = "此帳號已存在";
                ViewBag.MyDepartment = department;
                return View();
            }

            _db.Employees.Add(new Employee
            {
                Account      = account,
                PasswordHash = HashPassword(password),
                Name         = name,
                Role         = "manager",
                Department   = department, // 💡 固定用 director 的部門
                CreatedAt    = DateTime.UtcNow
            });

            await _db.SaveChangesAsync();
            TempData["Success"] = $"已新增「{department}」部門主管";
            return RedirectToAction("Index");
        }

        // ==============================
        // 編輯主管（GET）
        // ==============================
        public async Task<IActionResult> Edit(int id)
        {
            var memberId = HttpContext.Session.GetInt32("MemberId");
            if (memberId == null) return RedirectToAction("Index", "Login");

            var role = HttpContext.Session.GetString("MemberRole")?.ToLower();
            if (role != "director") return RedirectToAction("Index", "Home");

            var director = await _db.Employees.FindAsync(memberId);
            if (director == null) return RedirectToAction("Index", "Login");

            var manager = await _db.Employees.FindAsync(id);

            // 💡 只能編輯自己部門的主管
            if (manager == null || manager.Role != "manager" || manager.Department != director.Department)
                return NotFound();

            ViewBag.MyDepartment = director.Department;
            return View(manager);
        }

        // 編輯主管（POST）
        [HttpPost]
        public async Task<IActionResult> Edit(int id, string name, string? newPassword)
        {
            var memberId = HttpContext.Session.GetInt32("MemberId");
            if (memberId == null) return RedirectToAction("Index", "Login");

            var director = await _db.Employees.FindAsync(memberId);
            if (director == null) return RedirectToAction("Index", "Login");

            var manager = await _db.Employees.FindAsync(id);
            if (manager == null || manager.Role != "manager" || manager.Department != director.Department)
                return NotFound();

            manager.Name = name;
            // 部門不允許修改
            if (!string.IsNullOrEmpty(newPassword))
                manager.PasswordHash = HashPassword(newPassword);

            await _db.SaveChangesAsync();
            TempData["Success"] = "主管資料已更新";
            return RedirectToAction("Index");
        }

        // ==============================
        // 刪除主管（POST）
        // ==============================
        [HttpPost]
        public async Task<IActionResult> Delete(int id)
        {
            var memberId = HttpContext.Session.GetInt32("MemberId");
            if (memberId == null) return RedirectToAction("Index", "Login");

            var director = await _db.Employees.FindAsync(memberId);
            if (director == null) return RedirectToAction("Index", "Login");

            var manager = await _db.Employees.FindAsync(id);

            // 💡 只能刪除自己部門的主管
            if (manager != null && manager.Role == "manager" && manager.Department == director.Department)
            {
                _db.Employees.Remove(manager);
                await _db.SaveChangesAsync();
                TempData["Success"] = "主管已刪除";
            }

            return RedirectToAction("Index");
        }

        private static string HashPassword(string password)
        {
            var bytes = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(password));
            return Convert.ToHexString(bytes).ToLower();
        }
    }
}