using InterviewProject.Data;
using InterviewProject.Models;
using InterviewProject.Hubs;
using InterviewProject.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace InterviewProject.Controllers
{
    public class InterviewController : Controller
    {
        private readonly AppDbContext _db;
        private readonly IHubContext<MeetingHub> _meetingHub;
        private readonly R2StorageService _storage;
        private readonly AutoInterviewSchedulingService _scheduler;
        private readonly EmailService _email;

        public InterviewController(AppDbContext db, IHubContext<MeetingHub> meetingHub, R2StorageService storage,
            AutoInterviewSchedulingService scheduler, EmailService email)
        {
            _db = db;
            _meetingHub = meetingHub;
            _storage = storage;
            _scheduler = scheduler;
            _email = email;
        }

        // 🎯 「主持人」是每場面試由HR指定的，只看 Rooms.HostEmployeeId，跟帳號角色無關
        private async Task<bool> IsRoomHostAsync(int roomId, int empId)
        {
            return await _db.Rooms.AnyAsync(r => r.Id == roomId && r.HostEmployeeId == empId);
        }

        private bool IsEmployee()
        {
            var role = HttpContext.Session.GetString("MemberRole")?.ToLower();
            return role == "hr" || role == "manager";
        }

        private int GetEmployeeId() => HttpContext.Session.GetInt32("MemberId") ?? 0;
        private int GetMemberId() => HttpContext.Session.GetInt32("MemberId") ?? 0;

        // ── 職員專用：面試房間列表（新版，資料來自 Rooms + RoomParticipants） ──
        //    🎯 HR 不參與會議，不開放這個頁面（即使直接打網址也一樣擋）
        public async Task<IActionResult> Index()
        {
            if (!IsEmployee()) return RedirectToAction("Index", "Login");

            var role = HttpContext.Session.GetString("MemberRole")?.ToLower();

            // 🎯 改版：HR 有自己的面試管理頁（安排面試、修改時間人員主持人、通知錄取結果）
            if (role == "hr") return await HrIndexAsync();

            if (role != "manager")
            {
                TempData["Error"] = "此頁面僅供職員使用";
                return RedirectToAction("Dashboard", "AdminHome");
            }

            int empId = GetEmployeeId();

            // 🎯 只列「新系統」自動建立的面試房間（有掛 JobsId 的），舊的手動測試房間不顯示
            var query = _db.Rooms
                .Include(r => r.Job)
                .Include(r => r.Participants)
                    .ThenInclude(p => p.Resume)
                        .ThenInclude(res => res!.Member)
                .Where(r => r.JobsId != null)
                // 職員：只看自己被邀請（RoomParticipants.EmployeeId）的房間
                .Where(r => r.Participants.Any(p => p.EmployeeId == empId));

            var rooms = await query
               .OrderByDescending(r => r.ScheduledAt)
               .ToListAsync();

            // 🎯 面試評分頁要用：這個人針對每份履歷，是否已經評過分
            var roomIds = rooms.Select(r => r.Id).ToList();
            var allEvaluations = await _db.InterviewEvaluations
                .Include(e => e.EvaluatorEmployee)
                .Where(e => roomIds.Contains(e.RoomId))
                .ToListAsync();

            var myEvaluations = allEvaluations.Where(e => e.EvaluatorEmployeeId == empId).ToList();
            ViewBag.MyEvaluations = myEvaluations.ToDictionary(e => e.ResumeId, e => e);

            // 🎯 主持人要能看到「職員」的評分紀錄作為參考，同時也用來判斷「職員是否已評分」
            // 🎯 改版：主持人也是職員，所以「其他職員的評分」改成看評分者在「該場面試」的參與者角色是 Manager
            //    （不是主持人），而不是看帳號角色
            var participantRoleLookup = rooms
                .SelectMany(r => r.Participants ?? new List<RoomParticipant>())
                .Where(p => p.EmployeeId.HasValue)
                .GroupBy(p => (p.RoomId, p.EmployeeId!.Value))
                .ToDictionary(g => g.Key, g => g.First().Role);

            ViewBag.ManagerEvaluationsByResume = allEvaluations
                .Where(e => e.EvaluatorEmployee != null
                            && participantRoleLookup.TryGetValue((e.RoomId, e.EvaluatorEmployeeId), out var pRole)
                            && pRole == ParticipantRole.Manager)
                .GroupBy(e => e.ResumeId)
                .ToDictionary(g => g.Key, g => g.OrderBy(e => e.EvaluatorEmployee!.Name).ToList());

            ViewBag.CurrentRole = role;
            ViewBag.CurrentEmployeeId = empId;

            // 🎯 改版：哪些房間「我」是主持人（每場各自指定）
            ViewBag.HostRoomIds = rooms
                .Where(r => r.HostEmployeeId == empId)
                .Select(r => r.Id)
                .ToHashSet();

            // 🎯 面試評分頁要用：列出每個「已結束」房間目前有哪些 AI 分析檔案
            //    （現在是一位求職者一份檔案，不是單一檔名，所以要用前綴去 R2 列出來，不能只認一個欄位）
            var aiAnalysisByRoom = new Dictionary<int, List<string>>();
            foreach (var r in rooms.Where(r => r.MeetingStatus == "Ended"))
            {
                var baseName = RoomController.BuildFileName(r, "txt"); // 面試會議_date_job.txt
                var prefix = "AI分析/" + baseName.Substring(0, baseName.Length - 4) + "_"; // 去掉副檔名，加底線當前綴
                var keys = await _storage.ListKeysAsync(prefix);
                aiAnalysisByRoom[r.Id] = keys.Select(k => k.Substring("AI分析/".Length)).ToList();
            }
            ViewBag.AiAnalysisByRoom = aiAnalysisByRoom;

            // 🎯 改版：評分頁加上求職者的適性測驗結果（跟逐字稿/影片/AI分析一起當參考）
            var resumeIdsInRooms = rooms
                .SelectMany(r => r.Participants ?? new List<RoomParticipant>())
                .Where(p => p.Role == ParticipantRole.Jobseeker && p.ResumeId.HasValue)
                .Select(p => p.ResumeId!.Value)
                .Distinct()
                .ToList();
            ViewBag.AptitudeResultsByResume = await _db.AptitudeTestResults
                .Where(t => resumeIdsInRooms.Contains(t.ResumeId))
                .ToDictionaryAsync(t => t.ResumeId, t => t);

            return View("~/Views/Interview/Index.cshtml", rooms);
        }

        // 🎯 職員針對某位求職者送出這場面試的評分評語
        [HttpPost]
        public async Task<IActionResult> SubmitEvaluation(int roomId, int resumeId, int score, string? comment)
        {
            if (!IsEmployee()) return RedirectToAction("Index", "Login");
            var role = HttpContext.Session.GetString("MemberRole")?.ToLower();
            if (role != "manager")
                return Json(new { success = false, message = "只有職員能評分" });

            if (score < 1 || score > 5)
                return Json(new { success = false, message = "請選擇分數（1~5分）" });

            // 🎯 評語必填，不可空白
            if (string.IsNullOrWhiteSpace(comment))
                return Json(new { success = false, message = "評語為必填，請輸入評語" });

            int empId = GetEmployeeId();

            // 一定要是這場面試受邀的職員才能評
            var isInvited = await _db.RoomParticipants.AnyAsync(p =>
                p.RoomId == roomId && p.EmployeeId == empId
                && p.Role == ParticipantRole.Manager);
            if (!isInvited) return Json(new { success = false, message = "你不是這場面試受邀的職員" });

            var existing = await _db.InterviewEvaluations.FirstOrDefaultAsync(e =>
                e.RoomId == roomId && e.ResumeId == resumeId && e.EvaluatorEmployeeId == empId);

            if (existing != null)
            {
                existing.Score = score;
                existing.Comment = comment;
                existing.CreatedAt = DateTime.Now;
            }
            else
            {
                _db.InterviewEvaluations.Add(new InterviewEvaluation
                {
                    RoomId = roomId,
                    ResumeId = resumeId,
                    EvaluatorEmployeeId = empId,
                    Score = score,
                    Comment = comment
                });
            }

            await _db.SaveChangesAsync();
            return Json(new { success = true });
        }

        // 🎯 只有主持人能定案錄取/未錄取，且這場面試「所有」受邀的職員都要先評完分
        [HttpPost]
        public async Task<IActionResult> SetAdmission(int roomId, int resumeId, string result)
        {
            if (!IsEmployee()) return RedirectToAction("Index", "Login");

            int empId = GetEmployeeId();
            // 🎯 改版：只有「這場面試的主持人」能決定錄取結果；不用等其他職員評完分，職員評分只是參考
            if (!await IsRoomHostAsync(roomId, empId))
                return Json(new { success = false, message = "只有這場面試的主持人能決定錄取結果" });

            if (result != AdmissionResultValues.Admitted && result != AdmissionResultValues.Rejected)
                return Json(new { success = false, message = "結果只能是錄取或未錄取" });

            var room = await _db.Rooms.FindAsync(roomId);
            if (room == null || room.MeetingStatus != "Ended")
                return Json(new { success = false, message = "面試結束後才能決定錄取結果" });
            if (room.ResultSubmittedAt != null)
                return Json(new { success = false, message = "已經送出給 HR 了，無法再修改" });

            var inRoom = await _db.RoomParticipants.AnyAsync(p =>
                p.RoomId == roomId && p.Role == ParticipantRole.Jobseeker && p.ResumeId == resumeId);
            if (!inRoom) return Json(new { success = false, message = "這位求職者不在這場面試裡" });

            var resume = await _db.Resumes.FindAsync(resumeId);
            if (resume == null) return Json(new { success = false, message = "找不到這份履歷" });

            // 🎯 先暫存，求職者還看不到；全部決定好後由主持人「送出」並通知 HR，再由 HR 通知求職者
            resume.PendingAdmissionResult = result;
            await _db.SaveChangesAsync();

            return Json(new { success = true });
        }

        // 主持人決定好這場面試每位求職者的結果後，按「送出並通知 HR」：寄 email 給 HR，HR 才能通知求職者
        [HttpPost]
        public async Task<IActionResult> SubmitResults(int roomId)
        {
            if (!IsEmployee()) return RedirectToAction("Index", "Login");

            int empId = GetEmployeeId();
            if (!await IsRoomHostAsync(roomId, empId))
                return Json(new { success = false, message = "只有這場面試的主持人能送出錄取結果" });

            var room = await _db.Rooms.Include(r => r.Job).FirstOrDefaultAsync(r => r.Id == roomId);
            if (room == null || room.MeetingStatus != "Ended")
                return Json(new { success = false, message = "面試結束後才能送出錄取結果" });
            if (room.ResultSubmittedAt != null)
                return Json(new { success = false, message = "已經送出給 HR 了" });

            var resumes = await _db.RoomParticipants
                .Where(p => p.RoomId == roomId && p.Role == ParticipantRole.Jobseeker && p.Resume != null)
                .Select(p => p.Resume!)
                .Include(r => r.Member)
                .ToListAsync();
            if (resumes.Any(r => r.PendingAdmissionResult != AdmissionResultValues.Admitted
                              && r.PendingAdmissionResult != AdmissionResultValues.Rejected))
                return Json(new { success = false, message = "還有求職者沒決定，請全部決定錄取或未錄取後再送出" });

            room.ResultSubmittedAt = DateTime.Now;
            await _db.SaveChangesAsync();

            // 寄 email 給所有 HR
            var jobTitle = System.Net.WebUtility.HtmlEncode(room.Job?.Title ?? "");
            var lines = string.Join("", resumes.Select(r =>
                $"<li>{System.Net.WebUtility.HtmlEncode(r.Member?.Name ?? "")}：<b>{r.PendingAdmissionResult}</b></li>"));
            var body = $"<h3>人資您好：</h3><p>「{jobTitle}」的面試已由主持人決定錄取結果：</p><ul>{lines}</ul>" +
                       "<p>請登入系統到「面試管理」確認後通知求職者。</p><br><p>此信件由系統自動寄出，請勿直接回覆。</p>";
            var hrs = await _db.Employees.Where(e => e.Role == "hr").ToListAsync();
            await Task.WhenAll(hrs.Select(h => _email.SendAsync(EmployeeEmail(h), "面試錄取結果待通知", body)));

            return Json(new { success = true, message = "已送出，並通知 HR" });
        }

        // 🎯 只有這場面試的主持人能改自己主持的房間的預計面試時間，會議還沒開始才能改
        [HttpPost]
        public async Task<IActionResult> UpdateScheduledAt(int roomId, DateTime newTime)
        {
            if (!IsEmployee()) return Json(new { success = false, message = "請先登入" });

            int empId = GetEmployeeId();

            var room = await _db.Rooms.FirstOrDefaultAsync(r => r.Id == roomId);
            if (room == null)
                return Json(new { success = false, message = "找不到這個房間" });

            var isHostOfThisRoom = await IsRoomHostAsync(roomId, empId);
            if (!isHostOfThisRoom)
                return Json(new { success = false, message = "你不是這場面試的主持人" });

            if (room.MeetingStatus != "NotStarted")
                return Json(new { success = false, message = "會議已經開始或結束，無法再改時間" });

            if (newTime <= DateTime.Now)
                return Json(new { success = false, message = "時間只能選現在之後" });

            room.ScheduledAt = newTime;
            await _db.SaveChangesAsync();

            // 🎯 即時通知：正在這個房間頁面（等待畫面）的人會立刻收到最新時間
            //    不在頁面上的人（例如還沒打開應徵管理／面試管理），下次打開頁面時本來就會看到資料庫裡的新時間
            await _meetingHub.Clients.Group(room.JitsiRoomName).SendAsync(
                "ScheduledAtChanged",
                newTime.ToString("yyyy-MM-dd HH:mm:ss"));

            return Json(new { success = true, newTime = newTime.ToString("yyyy-MM-dd HH:mm:ss") });
        }

        // ═══════════════════════════════════════════════════════════
        //  HR 面試管理（改版新增）
        //  等待安排面試：職缺已下架 + 送出的履歷全部審完 + 至少一位通過 + 還沒建過會議室
        //  已安排面試：會議開始前可以修改時間、求職者、職員、主持人（只會有一位）
        //  錄取結果：面試結束後看主持人的決定，由 HR 通知求職者（同時寄 email）
        //  HR 不參與面試，也看不到逐字稿／影片／AI 分析
        // ═══════════════════════════════════════════════════════════
        private async Task<IActionResult> HrIndexAsync()
        {
            var vm = new HrInterviewVm();

            var rooms = await _db.Rooms
                .Include(r => r.Job)
                .Include(r => r.Participants).ThenInclude(p => p.Resume).ThenInclude(res => res!.Member)
                .Include(r => r.Participants).ThenInclude(p => p.Employee)
                .Where(r => r.JobsId != null)
                .OrderByDescending(r => r.ScheduledAt)
                .ToListAsync();

            // ── 等待安排面試的職缺 ──
            var jobIdsWithRoom = rooms.Select(r => r.JobsId!.Value).ToHashSet();
            var inactiveJobs = await _db.Jobs.Where(j => !j.IsActive).OrderBy(j => j.Title).ToListAsync();
            foreach (var job in inactiveJobs.Where(j => !jobIdsWithRoom.Contains(j.Id)))
            {
                if (!await _scheduler.IsJobReadyAsync(job)) continue;
                vm.ReadyJobs.Add(new HrReadyJobVm
                {
                    Job = job,
                    ApprovedResumes = await _db.Resumes.Include(r => r.Member)
                        .Where(r => r.JobsId == job.Id && r.Status == "已通過").OrderBy(r => r.Id).ToListAsync(),
                    Staff = await _scheduler.GetDeptStaffAsync(job)
                });
            }

            // ── 已建立的會議室 ──
            var endedRoomIds = rooms.Where(r => r.MeetingStatus == "Ended").Select(r => r.Id).ToList();
            var evals = await _db.InterviewEvaluations.Where(e => endedRoomIds.Contains(e.RoomId)).ToListAsync();

            foreach (var room in rooms)
            {
                var roomVm = new HrRoomVm
                {
                    Room = room,
                    Candidates = room.Participants.Where(p => p.Role == ParticipantRole.Jobseeker && p.Resume != null)
                        .Select(p => p.Resume!).ToList(),
                    StaffInRoom = room.Participants
                        .Where(p => p.Role == ParticipantRole.Manager && p.Employee != null)
                        .Select(p => p.Employee!).ToList(),
                    HostEmployeeId = room.HostEmployeeId,
                    // 職員評分進度不含主持人（主持人是決定結果的人）
                    StaffCount = room.Participants.Count(p => p.Role == ParticipantRole.Manager && p.EmployeeId != room.HostEmployeeId)
                };

                if (room.MeetingStatus == "NotStarted" && room.Job != null)
                {
                    roomVm.ApprovedResumes = await _db.Resumes.Include(r => r.Member)
                        .Where(r => r.JobsId == room.Job.Id && r.Status == "已通過").OrderBy(r => r.Id).ToListAsync();
                    roomVm.DeptStaff = await _scheduler.GetDeptStaffAsync(room.Job);
                }

                if (room.MeetingStatus == "Ended")
                {
                    var managerIds = room.Participants.Where(p => p.Role == ParticipantRole.Manager && p.EmployeeId != null && p.EmployeeId != room.HostEmployeeId)
                        .Select(p => p.EmployeeId!.Value).ToHashSet();
                    roomVm.EvalCountByResume = evals.Where(e => e.RoomId == room.Id && managerIds.Contains(e.EvaluatorEmployeeId))
                        .GroupBy(e => e.ResumeId).ToDictionary(g => g.Key, g => g.Select(e => e.EvaluatorEmployeeId).Distinct().Count());
                }

                vm.Rooms.Add(roomVm);
            }

            return View("~/Views/Interview/HrIndex.cshtml", vm);
        }

        private static string? EmployeeEmail(Employee e) =>
            !string.IsNullOrWhiteSpace(e.Account) && e.Account.Contains('@') ? e.Account.Trim() : null;

        private string RoomLink(Room room) => $"{Request.Scheme}://{Request.Host}/Room/Join?code={room.JitsiRoomName}";

        private static string MeetingMailBody(string greeting, string headline, Room room, Job? job, string link, string extra)
        {
            var time = room.ScheduledAt?.ToString("yyyy-MM-dd HH:mm") ?? "未定";
            return $@"
                <h3>{System.Net.WebUtility.HtmlEncode(greeting)}</h3>
                <p>{headline}</p>
                <p>職缺：<b>{System.Net.WebUtility.HtmlEncode(job?.Title ?? "")}</b><br>
                   面試時間：<b>{time}</b></p>
                {extra}
                <p>進入會議室連結（請先登入系統）：<a href='{link}'>{link}</a></p>
                <br><p>此信件由系統自動寄出，請勿直接回覆。</p>";
        }

        private async Task SendMeetingEmailsAsync(AutoInterviewSchedulingService.MeetingSaveResult r, bool isCreate)
        {
            var room = r.Room!;
            var job = await _db.Jobs.FindAsync(room.JobsId);
            var link = RoomLink(room);
            var tasks = new List<Task>();

            bool anyChange = isCreate || r.TimeChanged || r.HostChanged
                             || r.NewJobseekerIds.Any() || r.NewStaffIds.Any()
                             || r.RemovedJobseekers.Any() || r.RemovedStaff.Any();
            if (!anyChange) return;

            foreach (var resume in r.Jobseekers)
            {
                var isNew = isCreate || r.NewJobseekerIds.Contains(resume.Id);
                var extra = "<p>※ 進入面試前請先到「應徵管理」完成適性測驗，未完成無法進入會議室。</p>";
                tasks.Add(_email.SendAsync(resume.Member?.Email,
                    isNew ? "面試通知" : "面試安排已更新",
                    MeetingMailBody($"{resume.Member?.Name} 您好：",
                        isNew ? "您已被安排參加面試。" : "您的面試安排已更新，請以此信為準。",
                        room, job, link, extra)));
            }

            foreach (var st in r.Staff)
            {
                var isNew = isCreate || r.NewStaffIds.Contains(st.Id);
                var isHost = st.Id == r.HostEmployeeId;
                var extra = isHost ? "<p>您在這場面試擔任 <b>主持人</b>，負責開始／結束會議，並在面試後決定錄取結果。</p>" : "";
                tasks.Add(_email.SendAsync(EmployeeEmail(st),
                    isNew ? "面試通知" : "面試安排已更新",
                    MeetingMailBody($"{st.Name} 您好：",
                        isNew ? "您已被安排參與一場面試。" : "這場面試的安排已更新，請以此信為準。",
                        room, job, link, extra)));
            }

            foreach (var resume in r.RemovedJobseekers)
                tasks.Add(_email.SendAsync(resume.Member?.Email, "面試安排異動",
                    $"<h3>{System.Net.WebUtility.HtmlEncode(resume.Member?.Name ?? "")} 您好：</h3><p>您原本在「{System.Net.WebUtility.HtmlEncode(job?.Title ?? "")}」的面試安排已取消，如有疑問請洽人資。</p>"));

            foreach (var st in r.RemovedStaff)
                tasks.Add(_email.SendAsync(EmployeeEmail(st), "面試安排異動",
                    $"<h3>{System.Net.WebUtility.HtmlEncode(st.Name)} 您好：</h3><p>您已不在「{System.Net.WebUtility.HtmlEncode(job?.Title ?? "")}」這場面試的參與名單中。</p>"));

            await Task.WhenAll(tasks);
        }

        [HttpPost]
        public async Task<IActionResult> HrCreateMeeting(int jobId, DateTime scheduledAt,
            List<int>? resumeIds, List<int>? staffIds, int hostEmployeeId)
        {
            if (HttpContext.Session.GetString("MemberRole")?.ToLower() != "hr")
                return RedirectToAction("Index", "Login");

            var result = await _scheduler.CreateRoomAsync(jobId, scheduledAt,
                resumeIds ?? new List<int>(), staffIds ?? new List<int>(), hostEmployeeId);

            if (!result.Success)
            {
                TempData["Error"] = result.Error;
                return RedirectToAction("Index");
            }

            await SendMeetingEmailsAsync(result, isCreate: true);
            TempData["Success"] = "已建立面試會議室，並寄出 email 通知";
            return RedirectToAction("Index");
        }

        [HttpPost]
        public async Task<IActionResult> HrUpdateMeeting(int roomId, DateTime scheduledAt,
            List<int>? resumeIds, List<int>? staffIds, int hostEmployeeId)
        {
            if (HttpContext.Session.GetString("MemberRole")?.ToLower() != "hr")
                return RedirectToAction("Index", "Login");

            var result = await _scheduler.UpdateRoomAsync(roomId, scheduledAt,
                resumeIds ?? new List<int>(), staffIds ?? new List<int>(), hostEmployeeId);

            if (!result.Success)
            {
                TempData["Error"] = result.Error;
                return RedirectToAction("Index");
            }

            // 🎯 跟原本主持人改時間一樣，正在等待畫面的人要即時收到新時間
            if (result.TimeChanged && result.Room?.ScheduledAt != null)
            {
                await _meetingHub.Clients.Group(result.Room.JitsiRoomName).SendAsync(
                    "ScheduledAtChanged", result.Room.ScheduledAt.Value.ToString("yyyy-MM-dd HH:mm:ss"));
            }

            await SendMeetingEmailsAsync(result, isCreate: false);
            TempData["Success"] = "已更新面試安排，並寄出 email 通知";
            return RedirectToAction("Index");
        }

        // HR 收到主持人送出的結果後，按下通知求職者：這時候才會寫進求職者看得到的 AdmissionResult，並寄 email
        [HttpPost]
        public async Task<IActionResult> HrNotifyRoom(int roomId)
        {
            if (HttpContext.Session.GetString("MemberRole")?.ToLower() != "hr")
                return Json(new { success = false, message = "只有 HR 能通知求職者" });

            var room = await _db.Rooms.Include(r => r.Job).FirstOrDefaultAsync(r => r.Id == roomId);
            if (room == null) return Json(new { success = false, message = "找不到這場面試" });
            if (room.ResultSubmittedAt == null)
                return Json(new { success = false, message = "主持人還沒送出錄取結果" });

            var resumes = await _db.RoomParticipants
                .Where(p => p.RoomId == roomId && p.Role == ParticipantRole.Jobseeker && p.Resume != null)
                .Select(p => p.Resume!)
                .Include(r => r.Member)
                .ToListAsync();

            var toNotify = resumes.Where(r =>
                (r.PendingAdmissionResult == AdmissionResultValues.Admitted || r.PendingAdmissionResult == AdmissionResultValues.Rejected)
                && r.AdmissionResult != AdmissionResultValues.Admitted && r.AdmissionResult != AdmissionResultValues.Rejected).ToList();
            if (!toNotify.Any()) return Json(new { success = false, message = "沒有需要通知的求職者" });

            foreach (var r in toNotify) r.AdmissionResult = r.PendingAdmissionResult;
            await _db.SaveChangesAsync();

            var jobTitle = System.Net.WebUtility.HtmlEncode(room.Job?.Title ?? "");
            var results = await Task.WhenAll(toNotify.Select(r =>
            {
                var name = System.Net.WebUtility.HtmlEncode(r.Member?.Name ?? "");
                var admitted = r.AdmissionResult == AdmissionResultValues.Admitted;
                var body = admitted
                    ? $"<h3>{name} 您好：</h3><p>恭喜您通過「{jobTitle}」的面試，<b>錄取</b>！後續報到事宜人資將另行與您聯繫。</p>"
                    : $"<h3>{name} 您好：</h3><p>感謝您參加「{jobTitle}」的面試，很遺憾這次的結果為<b>未錄取</b>。感謝您的投入，期待未來有合作機會。</p>";
                return _email.SendAsync(r.Member?.Email, admitted ? "錄取通知" : "面試結果通知",
                    body + "<br><p>此信件由系統自動寄出，請勿直接回覆。</p>");
            }));

            var failed = results.Count(x => !x);
            return Json(new
            {
                success = true,
                message = failed == 0 ? $"已通知 {toNotify.Count} 位求職者，並寄出 email"
                                      : $"已通知 {toNotify.Count} 位求職者（其中 {failed} 封 email 寄送失敗，請確認信箱）"
            });
        }
    }
}
