using InterviewProject.Data;
using InterviewProject.Models;
using Microsoft.EntityFrameworkCore;

namespace InterviewProject.Services
{
    /// <summary>
    /// 職缺下架後，自動判斷該職缺的履歷是否都已審核完畢；
    /// 若都有結果，自動建立面試房間，並邀請已通過的求職者、職員、主持人、AI面試官。
    /// </summary>
    public class AutoInterviewSchedulingService
    {
        private readonly AppDbContext _db;

        public AutoInterviewSchedulingService(AppDbContext db)
        {
            _db = db;
        }

        /// <summary>
        /// 🎯 改版：面試房間不再自動建立，改由 HR 在「面試管理」手動安排（見 CreateRoomAsync）。
        /// 這個方法保留只是因為還有幾個地方在呼叫它（職缺下架、履歷審核），一律直接不做事。
        /// </summary>
        public Task<bool> TryAutoScheduleAsync(int jobId) => Task.FromResult(false);

        /// <summary>某職缺可以由 HR 安排面試的條件：已下架、還沒建過房間、送出的履歷全部審完且至少一位通過。</summary>
        public async Task<bool> IsJobReadyAsync(Job job)
        {
            if (job.IsActive) return false;
            if (await _db.Rooms.AnyAsync(r => r.JobsId == job.Id)) return false;

            // 「暫存」是求職者還沒送出的草稿，不算進需要審核的履歷
            var submitted = await _db.Resumes
                .Where(r => r.JobsId == job.Id && r.Status != "暫存")
                .ToListAsync();
            if (submitted.Count == 0) return false;
            if (submitted.Any(r => string.IsNullOrEmpty(r.Status) || r.Status == "待審核")) return false;
            return submitted.Any(r => r.Status == "已通過");
        }

        /// <summary>這個職缺所屬部門的職員（預設邀請名單）。職員只有 manager，不含主持人(director)。</summary>
        public Task<List<Employee>> GetDeptStaffAsync(Job job)
        {
            return _db.Employees
                .Where(e => e.Department == job.Department && e.Role == "manager")
                .OrderBy(e => e.Name)
                .ToListAsync();
        }

        public class MeetingSaveResult
        {
            public bool Success { get; set; }
            public string? Error { get; set; }
            public Room? Room { get; set; }
            public bool TimeChanged { get; set; }
            public bool HostChanged { get; set; }
            public List<Resume> Jobseekers { get; set; } = new();
            public List<Employee> Staff { get; set; } = new();
            public int HostEmployeeId { get; set; }
            public List<Resume> RemovedJobseekers { get; set; } = new();
            public List<Employee> RemovedStaff { get; set; } = new();
            public List<int> NewJobseekerIds { get; set; } = new();
            public List<int> NewStaffIds { get; set; } = new();
        }

        private static MeetingSaveResult Fail(string msg) => new() { Success = false, Error = msg };

        /// <summary>HR 手動建立面試房間：一個職缺一間房，求職者與職員可複選，職員中指定一位主持人，AI 固定加入。</summary>
        public async Task<MeetingSaveResult> CreateRoomAsync(int jobId, DateTime scheduledAt,
            List<int> resumeIds, List<int> staffIds, int hostEmployeeId)
        {
            var job = await _db.Jobs.FindAsync(jobId);
            if (job == null) return Fail("找不到這個職缺");
            if (!await IsJobReadyAsync(job))
                return Fail("這個職缺目前不能安排面試（需已下架、履歷全部審完、至少一位通過，且還沒建立過面試）");
            if (scheduledAt <= DateTime.Now) return Fail("面試時間只能選現在之後");

            var err = await ValidateAsync(job, resumeIds, staffIds, hostEmployeeId);
            if (err != null) return Fail(err);

            var room = new Room
            {
                JobsId = job.Id,
                RoomName = $"{job.Title}－面試會議室",
                JitsiRoomName = Guid.NewGuid().ToString("N")[..10],
                CreatedTime = DateTime.Now,
                ScheduledAt = scheduledAt,
                StartAt = null,
                EndAt = null,
                IsActive = true,
                MeetingStatus = "NotStarted",
                HostEmployeeId = hostEmployeeId
            };
            _db.Rooms.Add(room);
            await _db.SaveChangesAsync();

            var resumes = await _db.Resumes.Include(r => r.Member).Where(r => resumeIds.Contains(r.Id)).ToListAsync();
            foreach (var resume in resumes)
            {
                _db.RoomParticipants.Add(new RoomParticipant
                {
                    RoomId = room.Id,
                    Role = ParticipantRole.Jobseeker,
                    ResumeId = resume.Id,
                    Status = ParticipantStatus.Invited
                });
                await RecomputeInterviewStatusAsync(resume, hasRoomInvite: true);
            }

            var staff = await _db.Employees.Where(e => staffIds.Contains(e.Id)).ToListAsync();
            foreach (var st in staff)
            {
                _db.RoomParticipants.Add(new RoomParticipant
                {
                    RoomId = room.Id,
                    Role = st.Id == hostEmployeeId ? ParticipantRole.Director : ParticipantRole.Manager,
                    EmployeeId = st.Id,
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

            return new MeetingSaveResult
            {
                Success = true,
                Room = room,
                TimeChanged = true,
                Jobseekers = resumes,
                Staff = staff,
                HostEmployeeId = hostEmployeeId,
                NewJobseekerIds = resumes.Select(r => r.Id).ToList(),
                NewStaffIds = staff.Select(s => s.Id).ToList()
            };
        }

        /// <summary>面試還沒開始前，HR 可以修改時間、求職者、職員、主持人（只會有一位）。</summary>
        public async Task<MeetingSaveResult> UpdateRoomAsync(int roomId, DateTime scheduledAt,
            List<int> resumeIds, List<int> staffIds, int hostEmployeeId)
        {
            var room = await _db.Rooms.Include(r => r.Participants).FirstOrDefaultAsync(r => r.Id == roomId);
            if (room == null || room.JobsId == null) return Fail("找不到這場面試");
            if (room.MeetingStatus != "NotStarted") return Fail("會議已經開始或結束，無法再修改");

            var job = await _db.Jobs.FindAsync(room.JobsId.Value);
            if (job == null) return Fail("找不到這場面試的職缺");

            var timeChanged = room.ScheduledAt == null || room.ScheduledAt.Value != scheduledAt;
            if (timeChanged && scheduledAt <= DateTime.Now) return Fail("面試時間只能選現在之後");

            var err = await ValidateAsync(job, resumeIds, staffIds, hostEmployeeId);
            if (err != null) return Fail(err);

            var oldHostId = room.HostEmployeeId ?? room.Participants.FirstOrDefault(p => p.Role == ParticipantRole.Director)?.EmployeeId;
            var result = new MeetingSaveResult
            {
                Success = true,
                Room = room,
                TimeChanged = timeChanged,
                HostEmployeeId = hostEmployeeId,
                HostChanged = oldHostId != hostEmployeeId
            };

            // ── 求職者 ──
            var jsRows = room.Participants.Where(p => p.Role == ParticipantRole.Jobseeker && p.ResumeId != null).ToList();
            var existingResumeIds = jsRows.Select(p => p.ResumeId!.Value).ToList();

            var removedResumeIds = existingResumeIds.Except(resumeIds).ToList();
            foreach (var row in jsRows.Where(p => removedResumeIds.Contains(p.ResumeId!.Value)))
                _db.RoomParticipants.Remove(row);

            var addedResumeIds = resumeIds.Except(existingResumeIds).ToList();
            foreach (var rid in addedResumeIds)
            {
                _db.RoomParticipants.Add(new RoomParticipant
                {
                    RoomId = room.Id,
                    Role = ParticipantRole.Jobseeker,
                    ResumeId = rid,
                    Status = ParticipantStatus.Invited
                });
            }

            // ── 職員 ──
            var staffRows = room.Participants
                .Where(p => (p.Role == ParticipantRole.Manager || p.Role == ParticipantRole.Director) && p.EmployeeId != null)
                .ToList();
            var existingStaffIds = staffRows.Select(p => p.EmployeeId!.Value).ToList();

            var removedStaffIds = existingStaffIds.Except(staffIds).ToList();
            foreach (var row in staffRows.Where(p => removedStaffIds.Contains(p.EmployeeId!.Value)))
                _db.RoomParticipants.Remove(row);

            // 留下來的人：主持人只會有一位，其餘一律是 Manager
            foreach (var row in staffRows.Where(p => staffIds.Contains(p.EmployeeId!.Value)))
                row.Role = row.EmployeeId == hostEmployeeId ? ParticipantRole.Director : ParticipantRole.Manager;

            var addedStaffIds = staffIds.Except(existingStaffIds).ToList();
            foreach (var sid in addedStaffIds)
            {
                _db.RoomParticipants.Add(new RoomParticipant
                {
                    RoomId = room.Id,
                    Role = sid == hostEmployeeId ? ParticipantRole.Director : ParticipantRole.Manager,
                    EmployeeId = sid,
                    Status = ParticipantStatus.Invited
                });
            }

            if (timeChanged) room.ScheduledAt = scheduledAt;
            room.HostEmployeeId = hostEmployeeId;
            await _db.SaveChangesAsync();

            // ── 面試狀態重算 ──
            var currentResumes = await _db.Resumes.Include(r => r.Member).Where(r => resumeIds.Contains(r.Id)).ToListAsync();
            foreach (var r in currentResumes.Where(r => addedResumeIds.Contains(r.Id)))
                await RecomputeInterviewStatusAsync(r, hasRoomInvite: true);

            var removedResumes = await _db.Resumes.Include(r => r.Member).Where(r => removedResumeIds.Contains(r.Id)).ToListAsync();
            foreach (var r in removedResumes)
            {
                // 不再被排進房間：做過測驗 → 等待安排面試；沒做過 → 清空
                var hasTest = await _db.AptitudeTestResults.AnyAsync(t => t.ResumeId == r.Id);
                r.InterviewStatus = hasTest ? InterviewStatusValues.WaitingSchedule : null;
            }
            await _db.SaveChangesAsync();

            result.Jobseekers = currentResumes;
            result.Staff = await _db.Employees.Where(e => staffIds.Contains(e.Id)).ToListAsync();
            result.RemovedJobseekers = removedResumes;
            result.RemovedStaff = await _db.Employees.Where(e => removedStaffIds.Contains(e.Id)).ToListAsync();
            result.NewJobseekerIds = addedResumeIds;
            result.NewStaffIds = addedStaffIds;
            return result;
        }

        private async Task<string?> ValidateAsync(Job job, List<int> resumeIds, List<int> staffIds, int hostEmployeeId)
        {
            if (resumeIds == null || resumeIds.Count == 0) return "請至少選擇一位求職者";
            if (staffIds == null || staffIds.Count == 0) return "請至少選擇一位職員";
            if (!staffIds.Contains(hostEmployeeId)) return "請從勾選的職員中指定一位主持人";

            var approvedIds = await _db.Resumes
                .Where(r => r.JobsId == job.Id && r.Status == "已通過")
                .Select(r => r.Id).ToListAsync();
            if (resumeIds.Any(id => !approvedIds.Contains(id))) return "選到了不屬於這個職缺或未通過的求職者";

            var deptStaffIds = (await GetDeptStaffAsync(job)).Select(e => e.Id).ToList();
            if (staffIds.Any(id => !deptStaffIds.Contains(id))) return "選到了不屬於這個職缺部門的職員";

            return null;
        }

        /// <summary>
        /// 求職者完成適性測驗後呼叫：重新計算他的 InterviewStatus。
        /// </summary>
        public async Task<bool> OnAptitudeTestCompletedAsync(int resumeId)
        {
            var resume = await _db.Resumes.FindAsync(resumeId);
            if (resume == null) return false;
            if (resume.Status != "已通過") return false; // 不是「已通過」不用管（待審核/未通過都不該有測驗）

            var isInvited = await _db.RoomParticipants.AnyAsync(p => p.ResumeId == resumeId);
            await RecomputeInterviewStatusAsync(resume, hasRoomInvite: isInvited);
            await _db.SaveChangesAsync();
            return true;
        }

        /// <summary>
        /// 核心狀態機：依「測驗有沒有完成」跟「有沒有被排進房間」這兩項，算出 InterviewStatus。
        /// 兩項都到齊 → 已安排面試；只到一項 → 等待安排面試；兩項都沒有 → null（維持已通過原樣）。
        /// </summary>
        private async Task RecomputeInterviewStatusAsync(Resume resume, bool hasRoomInvite)
        {
            var hasCompletedTest = await _db.AptitudeTestResults.AnyAsync(t => t.ResumeId == resume.Id);

            if (hasCompletedTest && hasRoomInvite)
            {
                resume.InterviewStatus = InterviewStatusValues.Scheduled; // 已安排面試
            }
            else if (hasCompletedTest || hasRoomInvite)
            {
                resume.InterviewStatus = InterviewStatusValues.WaitingSchedule; // 等待安排面試
            }
            // 兩項都沒有就不動，維持 null（履歷已通過，但還沒開始這個流程）
        }
    }
}