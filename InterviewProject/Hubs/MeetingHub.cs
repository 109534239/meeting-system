using Microsoft.AspNetCore.SignalR;

namespace InterviewProject.Hubs
{
    public class MeetingHub : Hub
    {
        // 主管：開始會議 → 廣播給同房間所有人
        public async Task StartMeeting(string roomCode)
        {
            await Clients.Group(roomCode).SendAsync("MeetingStarted");
        }

        // 主管：結束會議 → 廣播給同房間所有人
        public async Task EndMeeting(string roomCode)
        {
            await Clients.Group(roomCode).SendAsync("MeetingEnded");
        }

        // 加入房間群組
        public async Task JoinRoom(string roomCode)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, roomCode);
        }
    }
}
