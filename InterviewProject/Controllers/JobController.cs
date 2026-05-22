using Microsoft.AspNetCore.Mvc;
using InterviewProject.Data;
using InterviewProject.Models;
using System;
using System.Linq;

namespace InterviewProject.Controllers
{
    public class JobController : Controller
    {
        private readonly AppDbContext _context;

        public JobController(AppDbContext context)
        {
            _context = context;
        }

        // 1. 職缺搜尋列表
        public IActionResult Job_search(string category, string location, string type, string keyword)
        {
            // 只撈取啟用中 (IsActive == true) 的職缺
            var query = _context.Jobs.Where(x => x.IsActive).AsQueryable();

            // 動態條件篩選 (對應你們的真實欄位)
            if (!string.IsNullOrEmpty(category))
            {
                // 對應前端 value (如 pm, it)，你資料庫如果存的是中文 "專案管理"，要自己轉換，這裡先做包含搜尋
                query = query.Where(x => x.Department.Contains(category));
            }
            if (!string.IsNullOrEmpty(location))
            {
                query = query.Where(x => x.Location.Contains(location));
            }
            if (!string.IsNullOrEmpty(type))
            {
                query = query.Where(x => x.JobType.Contains(type));
            }
            if (!string.IsNullOrEmpty(keyword))
            {
                query = query.Where(x => x.Title.Contains(keyword) || x.Description.Contains(keyword) || x.Requirements.Contains(keyword));
            }

            // 依照建立時間排序
            var data = query.OrderByDescending(x => x.CreatedAt).ToList();

            ViewBag.SelectedCategory = category;
            ViewBag.SelectedLocation = location;
            ViewBag.SelectedType = type;
            ViewBag.Keyword = keyword;

            return View(data);
        }

        // 2. 職缺詳細頁 (改用 Id 查詢)
        public IActionResult Job_detail(int id)
        {
            if (id <= 0)
            {
                return NotFound();
            }

            var job = _context.Jobs.FirstOrDefault(x => x.Id == id);

            if (job == null)
            {
                return NotFound();
            }

            return View(job);
        }

        [HttpGet]
        public IActionResult GetSavedPositions()
        {
            var savedPositions = _context.Resumes
                .Select(x => x.Position)
                .Distinct()
                .Take(5)
                .ToList();

            return Json(savedPositions);
        }
    }
}