using InterviewProject.Data;
using InterviewProject.Models;
using InterviewProject.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace InterviewProject.Controllers
{
    public class AptitudeTestController : Controller
    {
        private readonly AppDbContext _db;
        private readonly AutoInterviewSchedulingService _scheduler;

        public AptitudeTestController(AppDbContext db, AutoInterviewSchedulingService scheduler)
        {
            _db = db;
            _scheduler = scheduler;
        }

        private int CurrentUserId => HttpContext.Session.GetInt32("MemberId") ?? 0;

        // 🎯 供彈窗開啟時呼叫：回傳目前這份履歷的測驗狀態（已完成就回填答案，沒完成就給題目）
        [HttpGet]
        public async Task<IActionResult> GetStatus(int resumeId)
        {
            var userId = CurrentUserId;
            if (userId == 0) return Json(new { success = false, message = "請重新登入" });

            var resume = await _db.Resumes.FirstOrDefaultAsync(r => r.Id == resumeId);
            if (resume == null || resume.MembersId != userId)
                return Json(new { success = false, message = "查無此履歷資料" });

            var result = await _db.AptitudeTestResults.FirstOrDefaultAsync(t => t.ResumeId == resumeId);
            if (result != null)
            {
                return Json(new
                {
                    success = true,
                    completed = true,
                    submittedAt = result.SubmittedAt.ToString("yyyy/MM/dd HH:mm")
                });
            }

            return Json(new
            {
                success = true,
                completed = false,
                questions = AptitudeTestBank.Questions.Select(q => new { q.Id, q.Text })
            });
        }

        // 🎯 送出測驗作答
        [HttpPost]
        public async Task<IActionResult> Submit([FromBody] AptitudeSubmitDto dto)
        {
            var userId = CurrentUserId;
            if (userId == 0) return Json(new { success = false, message = "請重新登入" });

            var resume = await _db.Resumes.FirstOrDefaultAsync(r => r.Id == dto.ResumeId);
            if (resume == null || resume.MembersId != userId)
                return Json(new { success = false, message = "查無此履歷資料" });

            // 不可重複測驗
            var already = await _db.AptitudeTestResults.AnyAsync(t => t.ResumeId == dto.ResumeId);
            if (already)
                return Json(new { success = false, message = "您已經完成過適性測驗了" });

            // 每一題都要有作答，且分數要在 1~5 之間
            var validIds = AptitudeTestBank.Questions.Select(q => q.Id).ToHashSet();
            var answered = dto.Answers?.Where(a => validIds.Contains(a.QuestionId) && a.Score >= 1 && a.Score <= 5).ToList()
                           ?? new List<AptitudeAnswerDto>();

            if (answered.Select(a => a.QuestionId).Distinct().Count() != validIds.Count)
                return Json(new { success = false, message = "請完成所有題目再送出" });

            double Avg(string dim) => answered
                .Where(a => AptitudeTestBank.Questions.First(q => q.Id == a.QuestionId).Dimension == dim)
                .Average(a => a.Score);

            var result = new AptitudeTestResult
            {
                ResumeId = dto.ResumeId,
                AnswersJson = System.Text.Json.JsonSerializer.Serialize(answered),
                StressToleranceScore = Avg(AptitudeTestBank.DimStress),
                TeamworkScore = Avg(AptitudeTestBank.DimTeamwork),
                ProactivenessScore = Avg(AptitudeTestBank.DimProactive),
                ReliabilityScore = Avg(AptitudeTestBank.DimReliability),
                CommunicationScore = Avg(AptitudeTestBank.DimCommunication),
                SubmittedAt = DateTime.Now
            };

            _db.AptitudeTestResults.Add(result);
            await _db.SaveChangesAsync();

            // 🎯 重新計算這份履歷的面試狀態（有沒有同時滿足「測驗完成」+「已被排進房間」）
            await _scheduler.OnAptitudeTestCompletedAsync(dto.ResumeId);

            return Json(new { success = true });
        }
    }

    public class AptitudeSubmitDto
    {
        public int ResumeId { get; set; }
        public List<AptitudeAnswerDto> Answers { get; set; } = new();
    }
}
