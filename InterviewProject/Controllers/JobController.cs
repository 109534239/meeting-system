using InterviewProject.Data;
using InterviewProject.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

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
        public async Task<IActionResult> Job_search(string category, string location, string type, string keyword)
        {
            // 💡 傳遞 Session 登入狀態
            ViewBag.IsLoggedIn = HttpContext.Session.GetInt32("MemberId").HasValue;
            ViewBag.MemberId = HttpContext.Session.GetInt32("MemberId") ?? 0;

            var now = DateTime.Now;

            // 基礎查詢：只抓取啟用中且未過期的職缺
            var baseQuery = _context.Jobs
                .Where(x => x.IsActive && x.Deadline.AddDays(1) > now);

            // 💡 動態抓取資料庫中現有的下拉選單選項 (Distinct)
            ViewBag.Categories = await baseQuery
                .Where(x => !string.IsNullOrEmpty(x.Department))
                .Select(x => x.Department)
                .Distinct()
                .OrderBy(x => x)
                .ToListAsync();

            ViewBag.Locations = await baseQuery
                .Where(x => !string.IsNullOrEmpty(x.Location))
                .Select(x => x.Location)
                .Distinct()
                .OrderBy(x => x)
                .ToListAsync();

            ViewBag.JobTypes = await baseQuery
                .Where(x => !string.IsNullOrEmpty(x.JobType))
                .Select(x => x.JobType)
                .Distinct()
                .OrderBy(x => x)
                .ToListAsync();

            // 進行條件篩選 (動態資料直接精準比對)
            var query = baseQuery.AsQueryable();

            // 1. 職缺類別 (Department)
            if (!string.IsNullOrEmpty(category))
            {
                query = query.Where(x => x.Department == category);
            }

            // 2. 工作地址 (Location)
            if (!string.IsNullOrEmpty(location))
            {
                query = query.Where(x => x.Location == location);
            }

            // 3. 工作性質 (JobType)
            if (!string.IsNullOrEmpty(type))
            {
                query = query.Where(x => x.JobType == type);
            }

            // 4. 關鍵字搜尋 (搜尋 Title, Description, SkillTags)
            if (!string.IsNullOrEmpty(keyword))
            {
                query = query.Where(x => x.Title.Contains(keyword) ||
                                         x.Description.Contains(keyword) ||
                                         x.SkillTags.Any(t => t.Tag.Contains(keyword)));
            }

            // 📌 【主要修正點】依據截止日期（Deadline）由近到遠排序 (昇順 OrderBy)
            // 若截止日期相同，則再以最新建立的 (CreatedAt) 優先排序
            var data = await query
                .OrderBy(x => x.Deadline)
                .ThenByDescending(x => x.CreatedAt)
                .ToListAsync();

            // 保留已選擇的搜尋條件給前端 View 呈現
            ViewBag.SelectedCategory = category;
            ViewBag.SelectedLocation = location;
            ViewBag.SelectedType = type;
            ViewBag.Keyword = keyword;

            return View(data);
        }

        // 2. 職缺詳細頁
        public async Task<IActionResult> Job_detail(int id, int? resumeId)
        {
            if (id <= 0) return NotFound();

            int? userId = HttpContext.Session.GetInt32("MemberId");

            ViewBag.IsLoggedIn = userId.HasValue;
            ViewBag.MemberId = userId ?? 0;

            var job = await _context.Jobs
                .Include(x => x.Manager)
                .Include(x => x.MajorRequirements)
                .Include(x => x.LanguageRequirements)
                .Include(x => x.SkillTags)
                .FirstOrDefaultAsync(x => x.Id == id);

            if (job == null) return NotFound();

            bool hasApplied = false;

            if (userId.HasValue)
            {
                // 🎯 1. 修正已投遞判定：排除「暫存」狀態，只有正式送出的履歷才算已投遞
                hasApplied = await _context.Resumes.AnyAsync(r =>
                    r.MembersId == userId.Value &&
                    r.JobsId == id &&
                    r.Status != "暫存");

                // 🎯 2. 撈取該會員此職缺的「暫存」履歷（包含詳細子表）
                var draftResumeQuery = _context.Resumes
                    .Include(r => r.Educations)
                    .Include(r => r.WorkExperiences)
                    .Include(r => r.Portfolios)
                    .Where(r => r.MembersId == userId.Value && r.JobsId == id && r.Status == "暫存");

                // 如果前端有傳遞特定的 resumeId 就精確抓，否則預設抓該職缺最新的暫存
                var draftResume = resumeId.HasValue
                    ? await draftResumeQuery.FirstOrDefaultAsync(r => r.Id == resumeId.Value)
                    : await draftResumeQuery.OrderByDescending(r => r.ResumeTime).FirstOrDefaultAsync();

                // 🎯 3. 將暫存履歷存入 ViewBag 傳給 View 進行表單預填
                ViewBag.DraftResume = draftResume;
            }

            ViewBag.HasApplied = hasApplied;

            return View(job);
        }

        // 3. 獲取已儲存的職位列表
        [HttpGet]
        public async Task<IActionResult> GetSavedPositions()
        {
            int? userId = HttpContext.Session.GetInt32("MemberId");
            if (!userId.HasValue) return Json(new List<object>());

            var savedPositions = await _context.Resumes
                .Include(r => r.Job)
                .Where(x => x.MembersId == userId.Value)
                .Select(x => new
                {
                    id = x.JobsId,
                    title = x.Job != null ? x.Job.Title : "職位 ID: " + x.JobsId
                })
                .Distinct()
                .ToListAsync();

            return Json(savedPositions);
        }

        // 4. 根據 ID 列表批次抓取職缺
        [HttpGet]
        public async Task<IActionResult> GetJobsByIds(string ids)
        {
            if (string.IsNullOrEmpty(ids))
                return Json(new List<object>());

            var idList = ids.Split(',')
                            .Select(s => int.TryParse(s.Trim(), out var n) ? n : 0)
                            .Where(n => n > 0)
                            .ToList();

            var jobs = await _context.Jobs
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
                .ToListAsync();

            return Json(jobs);
        }
    }
}