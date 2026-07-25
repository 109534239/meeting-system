using Microsoft.AspNetCore.SignalR;
using InterviewProject.Data;
using InterviewProject.Models;
using Microsoft.EntityFrameworkCore;

namespace InterviewProject.Hubs
{
    public class MeetingHub : Hub
    {
        private readonly AppDbContext _db;

        public MeetingHub(AppDbContext db)
        {
            _db = db;
        }

        // 加入房間群組（前端連上 hub 後第一件事）
        public async Task JoinRoom(string roomCode)
            => await Groups.AddToGroupAsync(Context.ConnectionId, roomCode);

        // 🎯 只有「這個房間受邀的最高主管」才能真的把會議狀態改成進行中
        public async Task StartMeeting(string roomCode)
        {
            var room = await GetAuthorizedHostRoomAsync(roomCode);
            if (room == null) return;

            if (room.MeetingStatus != "InProgress")
            {
                room.MeetingStatus = "InProgress";
                room.StartAt = DateTime.Now; // 🎯 這裡才是真正的開始時間，不是排程時預先填的

                // 🎯 求職者的面試狀態同步推進到「面試中」
                await UpdateJobseekerInterviewStatusAsync(room.Id, InterviewStatusValues.InProgress, null);

                await _db.SaveChangesAsync();
            }

            await Clients.Group(roomCode).SendAsync("MeetingStarted");
        }

        // 🎯 只有主持人能結束會議，結束的當下把 EndAt 寫回資料庫，並把求職者狀態推進到「面試結束/等待結果中」
        public async Task EndMeeting(string roomCode)
        {
            var room = await GetAuthorizedHostRoomAsync(roomCode);
            if (room == null) return;

            room.MeetingStatus = "Ended";
            room.EndAt = DateTime.Now;

            await UpdateJobseekerInterviewStatusAsync(room.Id, InterviewStatusValues.Ended, AdmissionResultValues.PendingResult);

            await _db.SaveChangesAsync();

            await Clients.Group(roomCode).SendAsync("MeetingEnded");
        }

        // ✅ 任何人偵測到聲音 → 廣播給所有人重置冷場計時（不含逐字稿內容，純粹只是「有人在講話」的訊號）
        public async Task BroadcastSpeech(string roomCode)
            => await Clients.Group(roomCode).SendAsync("SpeechDetected");

        // 🎯 修正：把某人辨識到的逐字稿「內容」廣播給房間裡其他人 —— 每個人的 SpeechRecognition 只能聽到自己的麥克風，
        //    之前只廣播了一個空訊號（BroadcastSpeech），沒有真的傳文字內容，導致主持人只看得到自己說的話。
        //    這裡送給「其他人」就好（自己已經在本機 push 過一次了，不用再收自己的）
        public async Task BroadcastTranscript(string roomCode, string speaker, string text, string time)
            => await Clients.OthersInGroup(roomCode).SendAsync("TranscriptReceived", speaker, text, time);

        // ── 內部小工具：確認呼叫者是不是「這個房間」受邀的最高主管，是的話回傳 Room，不是就回傳 null ──
        private async Task<Room?> GetAuthorizedHostRoomAsync(string roomCode)
        {
            var httpContext = Context.GetHttpContext();
            if (httpContext == null) return null;

            var sessionMemberId = httpContext.Session.GetInt32("MemberId");
            var sessionRole = httpContext.Session.GetString("MemberRole")?.ToLower();
            if (sessionMemberId == null || sessionRole != "director") return null;

            var room = await _db.Rooms.FirstOrDefaultAsync(r => r.JitsiRoomName == roomCode);
            if (room == null) return null;

            var isDirectorOfThisRoom = await _db.RoomParticipants.AnyAsync(p =>
                p.RoomId == room.Id
                && p.Role == ParticipantRole.Director
                && p.EmployeeId == sessionMemberId.Value);

            return isDirectorOfThisRoom ? room : null;
        }

        // ── 內部小工具：把這個房間裡所有求職者的履歷，面試狀態一起推進 ──
        private async Task UpdateJobseekerInterviewStatusAsync(int roomId, string interviewStatus, string? admissionResult)
        {
            var resumeIds = await _db.RoomParticipants
                .Where(p => p.RoomId == roomId && p.Role == ParticipantRole.Jobseeker && p.ResumeId != null)
                .Select(p => p.ResumeId!.Value)
                .ToListAsync();

            if (resumeIds.Count == 0) return;

            var resumes = await _db.Resumes.Where(r => resumeIds.Contains(r.Id)).ToListAsync();
            foreach (var resume in resumes)
            {
                resume.InterviewStatus = interviewStatus;
                if (admissionResult != null) resume.AdmissionResult = admissionResult;
            }
        }
    }
}
