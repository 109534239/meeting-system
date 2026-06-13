using Microsoft.AspNetCore.SignalR;

namespace InterviewProject.Hubs
{
    public class MeetingHub : Hub
    {
        // 主管：開始會議
        public async Task StartMeeting(string roomCode)
            => await Clients.Group(roomCode).SendAsync("MeetingStarted");

        // 主管：結束會議
        public async Task EndMeeting(string roomCode)
            => await Clients.Group(roomCode).SendAsync("MeetingEnded");

        // ✅ 任何人偵測到聲音 → 廣播給所有人重置冷場計時
        // 這樣「有沒有冷場」是整場會議共同偵測，不是各自獨立
        public async Task BroadcastSpeech(string roomCode)
            => await Clients.Group(roomCode).SendAsync("SpeechDetected");

        // 加入房間群組
        public async Task JoinRoom(string roomCode)
            => await Groups.AddToGroupAsync(Context.ConnectionId, roomCode);
    }
}
