using InterviewProject.Data;
using InterviewProject.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
            var now = DateTime.Now;
            var query = _context.Jobs
                .Where(x => x.IsActive && x.Deadline.AddDays(1) > now)
                .AsQueryable();

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
            // 🎯 Requirements 欄位已刪除，改用 SkillTags 子表搜尋（一筆一個標籤）
            if (!string.IsNullOrEmpty(keyword))
            {
                query = query.Where(x => x.Title.Contains(keyword) ||
                                    x.Description.Contains(keyword) ||
                                    x.SkillTags.Any(t => t.Tag.Contains(keyword)));
            }

            var data = query.OrderByDescending(x => x.CreatedAt).ToList();

            // 把目前的篩選條件存起來，等一下讓前端可以「保留選取狀態」
            ViewBag.SelectedCategory = category;
            ViewBag.SelectedLocation = location;
            ViewBag.SelectedType = type;
            ViewBag.Keyword = keyword;

            return View(data);
        }

        // 2. 職缺詳細頁 (整合已投遞判斷)
        public async Task<IActionResult> Job_detail(int id)
        {
            if (id <= 0) return NotFound();
            
            ViewBag.IsLoggedIn = HttpContext.Session.GetInt32("MemberId").HasValue;
            ViewBag.MemberId   = HttpContext.Session.GetInt32("MemberId") ?? 0;

            // 1. 抓取職缺資料（🎯 帶出 Manager 與正規化後的三張子表資料）
            var job = await _context.Jobs
                .Include(x => x.Manager)
                .Include(x => x.MajorRequirements)
                .Include(x => x.LanguageRequirements)
                .Include(x => x.SkillTags)
                .FirstOrDefaultAsync(x => x.Id == id);
            if (job == null) return NotFound();

            // 2. 檢查使用者是否已經投過履歷
            int? userId = HttpContext.Session.GetInt32("MemberId");
            bool hasApplied = false;

            if (userId.HasValue)
            {
                // 🎯 判斷 Resume 表中是否已有該 UserId 且 Position (JobId) 等於目前的 id
                hasApplied = await _context.Resumes.AnyAsync(r => r.MembersId == userId.Value && r.JobsId == id);
            }

            // 3. 將狀態傳給 View
            ViewBag.HasApplied = hasApplied;

            return View(job);
        }

        // 3. 獲取已儲存的職位列表 (供 Modal 下拉選單使用)
        [HttpGet]
        public async Task<IActionResult> GetSavedPositions()
        {
            int? userId = HttpContext.Session.GetInt32("MemberId");
            if (!userId.HasValue) return Json(new List<object>());

            // 🎯 必須有 Include(r => r.Job)
            var savedPositions = await _context.Resumes
                .Include(r => r.Job)
                .Where(x => x.MembersId == userId.Value)
                .Select(x => new
                {
                    id = x.JobsId, // JobId
                                     // 如果 Job 是 null，這裡會抓不到 Title
                    title = x.Job != null ? x.Job.Title : "職位 ID: " + x.JobsId
                })
                .Distinct()
                .ToListAsync();

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