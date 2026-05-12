using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using InterviewProject.Data;
using InterviewProject.Models;

namespace InterviewProject.Hubs
{
    public class ChatHub : Hub
    {
        private readonly AppDbContext _context;
        private static int OnlineCount = 0;

        // 加入建構函式進行依賴注入
        public ChatHub(AppDbContext context)
        {
            _context = context;
        }

        // 使用者連線
        public override async Task OnConnectedAsync()
        {
            OnlineCount++;

            await Clients.All.SendAsync(
                "UpdateOnlineCount",
                OnlineCount);

            await base.OnConnectedAsync();
        }

        // 使用者離線 記錄離開時間
        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            OnlineCount--;

            var latestAttendance = _context.Attendances
                .Where(x => x.LeaveTime == null)
                .OrderByDescending(x => x.JoinTime)
                .FirstOrDefault();

            if (latestAttendance != null)
            {
                latestAttendance.LeaveTime = DateTime.Now;

                _context.SaveChanges();
            }

            await Clients.All.SendAsync(
                "UpdateOnlineCount",
                OnlineCount);

            await base.OnDisconnectedAsync(exception);
        }

        // 聊天
        public async Task SendMessage(string user, string message)
        {
            await Clients.All.SendAsync(
                "ReceiveMessage",
                user,
                message);
        }

        // 加入通知
        public async Task UserJoined(string user)
        {
            await Clients.All.SendAsync(
                "UserJoinedMessage",
                $"{user} 加入了會議");
        }

        //記錄加入時間
        public async Task JoinRoom(string user, int roomId)
        {
            var attendance = new Attendance
            {
                UserName = user,
                RoomId = roomId,
                JoinTime = DateTime.Now
            };

            _context.Attendances.Add(attendance);

            _context.SaveChanges();

            await Clients.All.SendAsync(
                "UserJoinedMessage",
                $"{user} 加入了會議");
        }        
    }
}