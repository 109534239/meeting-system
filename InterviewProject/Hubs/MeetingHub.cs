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
        //    不是最高主管呼叫這個方法，會被靜默擋下（前端本來就不會顯示開始會議按鈕給非主持人，
        //    這裡是後端再把關一次，避免有人繞過前端直接呼叫 hub）
        public async Task StartMeeting(string roomCode)
        {
            var room = await GetAuthorizedHostRoomAsync(roomCode);
            if (room == null) return;

            if (room.MeetingStatus != "InProgress")
            {
                room.MeetingStatus = "InProgress";
                room.StartAt = DateTime.Now; // 🎯 這裡才是真正的開始時間，不是排程時預先填的
                await _db.SaveChangesAsync();
            }

            await Clients.Group(roomCode).SendAsync("MeetingStarted");
        }

        // 🎯 只有主持人能結束會議，結束的當下把 EndAt 寫回資料庫
        public async Task EndMeeting(string roomCode)
        {
            var room = await GetAuthorizedHostRoomAsync(roomCode);
            if (room == null) return;

            room.MeetingStatus = "Ended";
            room.EndAt = DateTime.Now;
            await _db.SaveChangesAsync();

            await Clients.Group(roomCode).SendAsync("MeetingEnded");
        }

        // ✅ 任何人偵測到聲音 → 廣播給所有人重置冷場計時
        public async Task BroadcastSpeech(string roomCode)
            => await Clients.Group(roomCode).SendAsync("SpeechDetected");

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
    }
}
