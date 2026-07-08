using InterviewProject.Data;
using InterviewProject.Models;
using Microsoft.EntityFrameworkCore;

namespace InterviewProject.Services
{
    /// <summary>
    /// 職缺下架後，自動判斷該職缺的履歷是否都已審核完畢；
    /// 若都有結果，自動建立面試房間，並邀請已通過的求職者、部門主管、最高主管、AI面試官。
    /// </summary>
    public class AutoInterviewSchedulingService
    {
        private readonly AppDbContext _db;

        public AutoInterviewSchedulingService(AppDbContext db)
        {
            _db = db;
        }

        /// <returns>true = 這次呼叫真的建立了新房間；false = 條件不符合或已經排過了</returns>
        public async Task<bool> TryAutoScheduleAsync(int jobId)
        {
            var job = await _db.Jobs.FindAsync(jobId);
            if (job == null) return false;

            // 職缺還在上架中，不用自動安排
            if (job.IsActive) return false;

            // 已經幫這個職缺排過房間了，不要重複建立
            var alreadyScheduled = await _db.Rooms.AnyAsync(r => r.JobsId == jobId);
            if (alreadyScheduled) return false;

            var resumes = await _db.Resumes
                .Where(r => r.JobsId == jobId)
                .ToListAsync();

            if (resumes.Count == 0) return false;

            // 只要還有一筆「待審核」（或空值），代表結果還沒出齊，先不排
            if (resumes.Any(r => string.IsNullOrEmpty(r.Status) || r.Status == "待審核"))
                return false;

            var approved = resumes.Where(r => r.Status == "已通過").ToList();
            if (approved.Count == 0) return false; // 全部未通過，沒人要面試

            var manager = await _db.Employees
                .FirstOrDefaultAsync(e => e.Name == job.EmployeesName);

            var director = await _db.Employees
                .FirstOrDefaultAsync(e => e.Department == job.Department && e.Role == "director");

            var room = new Room
            {
                JobsId = job.Id,
                RoomName = $"{job.Title}－面試會議室",
                JitsiRoomName = Guid.NewGuid().ToString("N")[..10],
                CreatedTime = DateTime.Now,
                StartAt = DateTime.Now.AddDays(1),
                EndAt = null,
                IsActive = true,
                MeetingStatus = "NotStarted"
            };
            _db.Rooms.Add(room);
            await _db.SaveChangesAsync(); // 先存檔拿到 room.Id

            foreach (var resume in approved)
            {
                _db.RoomParticipants.Add(new RoomParticipant
                {
                    RoomId = room.Id,
                    Role = ParticipantRole.Jobseeker,
                    ResumeId = resume.Id,
                    Status = ParticipantStatus.Invited
                });
                resume.Status = "已安排面試";
            }

            if (manager != null)
            {
                _db.RoomParticipants.Add(new RoomParticipant
                {
                    RoomId = room.Id,
                    Role = ParticipantRole.Manager,
                    EmployeeId = manager.Id,
                    Status = ParticipantStatus.Invited
                });
            }

            if (director != null)
            {
                _db.RoomParticipants.Add(new RoomParticipant
                {
                    RoomId = room.Id,
                    Role = ParticipantRole.Director,
                    EmployeeId = director.Id,
                    Status = ParticipantStatus.Invited
                });
            }

            _db.RoomParticipants.Add(new RoomParticipant
            {
                RoomId = room.Id,
                Role = ParticipantRole.AI,
                Status = ParticipantStatus.Invited
            });

            await _db.SaveChangesAsync();
            return true;
        }
    }
}