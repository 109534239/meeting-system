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
            // 💡 把 Session 值傳給 View
            ViewBag.IsLoggedIn = HttpContext.Session.GetInt32("MemberId").HasValue;
            ViewBag.MemberId   = HttpContext.Session.GetInt32("MemberId") ?? 0;
            
            // 只撈取啟用中的職缺
            var query = _context.Jobs.Where(x => x.IsActive).AsQueryable();

            // 💡 1. 職缺類別對照 (英文 Value 轉成資料庫中文)
            if (!string.IsNullOrEmpty(category))
            {
                string categoryName = category switch
                {
                    "pm" => "專案管理",
                    "it" => "資訊技術",
                    "hr" => "人力資源",
                    "mkt" => "行銷企劃",
                    "fin" => "財務會計",
                    "sales" => "業務銷售",
                    _ => category
                };
                query = query.Where(x => x.Department.Contains(categoryName));
            }

            // 💡 2. 工作地址對照
            if (!string.IsNullOrEmpty(location))
            {
                string locationName = location switch
                {
                    "taipei" => "台北內湖",
                    "xinbei" => "新北汐止",
                    "hsinchu1" => "新竹竹北",
                    "hsinchu2" => "新竹湖口",
                    _ => location
                };
                query = query.Where(x => x.Location.Contains(locationName));
            }

            // 💡 3. 工作性質對照 (前端 full 轉成 Model 預設的 fulltime)
            if (!string.IsNullOrEmpty(type))
            {
                string typeName = type switch
                {
                    "full" => "fulltime",
                    "part" => "parttime",
                    "intern" => "intern",
                    _ => type
                };
                query = query.Where(x => x.JobType == typeName);
            }

            // 4. 關鍵字搜尋
            if (!string.IsNullOrEmpty(keyword))
            {
                query = query.Where(x => x.Title.Contains(keyword) || 
                                    x.Description.Contains(keyword) || 
                                    x.Requirements.Contains(keyword));
            }

            var data = query.OrderByDescending(x => x.CreatedAt).ToList();

            // 把目前的篩選條件存起來，等一下讓前端可以「保留選取狀態」
            ViewBag.SelectedCategory = category;
            ViewBag.SelectedLocation = location;
            ViewBag.SelectedType = type;
            ViewBag.Keyword = keyword;

            return View(data);
        }

        // 2. 職缺詳細頁 (改用 Id 查詢)
        public IActionResult Job_detail(int id)
        {
            ViewBag.IsLoggedIn = HttpContext.Session.GetInt32("MemberId").HasValue;
            ViewBag.MemberId   = HttpContext.Session.GetInt32("MemberId") ?? 0;
            
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

        // 💡 新增：Favorites 頁面用來批次查詢職缺資料
        [HttpGet]
        public IActionResult GetJobsByIds(string ids)
        {
            if (string.IsNullOrEmpty(ids))
                return Json(new List<object>());

            var idList = ids.Split(',')
                            .Select(s => int.TryParse(s.Trim(), out var n) ? n : 0)
                            .Where(n => n > 0)
                            .ToList();

            var jobs = _context.Jobs
                .Where(j => idList.Contains(j.Id) && j.IsActive)
                .Select(j => new {
                    j.Id,
                    j.Title,
                    j.Department,
                    j.Location,
                    j.JobType,
                    j.ExperienceRequired,
                    j.EducationRequired
                })
                .ToList();

            return Json(jobs);
        }
    }
}