using InterviewProject.Controllers;

namespace InterviewProject.Services
{
    // 🎯 回答者鎖定的「硬性」那一層：每秒檢查，有指定回答者的房間，就由機器人（moderator）
    //    把其他求職者的麥克風靜音。不依賴各人瀏覽器裡的網頁程式，所以舊版畫面、沒收到廣播也一樣鎖得住。
    public class ResponderMuteService : BackgroundService
    {
        private readonly JitsiBotService _bot;
        public ResponderMuteService(JitsiBotService bot) { _bot = bot; }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await Task.Delay(1000, stoppingToken); } catch { break; }
                try
                {
                    foreach (var code in _bot.ActiveRoomCodes())
                    {
                        var who = RoomController.CurrentResponder(code);
                        if (!string.IsNullOrEmpty(who)) await _bot.MuteJobseekersExceptAsync(code, who);
                    }
                }
                catch { }
            }
        }
    }
}
