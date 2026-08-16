using InterviewProject.Data;
using Microsoft.EntityFrameworkCore;

namespace InterviewProject.Services
{
    // 🐛 這輪修正的根本原因：原本「職缺下架」這件事，只有 HR 在後台手動按「下架」或編輯職缺切成下架
    //    的時候，才會真的把 Jobs.IsActive 改成 false，也才會順便觸發 AutoInterviewSchedulingService
    //    去檢查「這個職缺的履歷是不是都審完了、可以自動排面試房間了」。
    //
    //    但求職者看到的「職缺已下架」其實是另一條完全獨立的邏輯：JobController 公開列表頁只是用
    //    `Deadline.AddDays(1) > now` 把過期的職缺「過濾掉、不顯示」而已，並沒有真的去改 Jobs.IsActive。
    //    這代表：只要沒有人手動去點「下架」，一個職缺就算截止日期早就過了、求職者也看不到它了，
    //    資料庫裡的 IsActive 欄位還是會一直停在 true，AutoInterviewSchedulingService 就永遠不會被觸發，
    //    履歷審核完、適性測驗也做完的求職者就會卡在「等待安排面試」，永遠等不到面試房間被建立。
    //
    //    這裡補上真正「自動」的那一半：背景服務，每隔一段時間主動掃一次資料庫，
    //    把「還是 IsActive=true 但截止日期已經過了」的職缺，*真的*標記成下架，並觸發自動排程檢查。
    //    這樣不管有沒有人登入後台手動操作，職缺過期後都會自己被下架、自己觸發面試安排，
    //    也會順便把「這一輪之前已經卡住」的舊職缺，在服務啟動後第一次掃描時一併補救回來。
    public class JobExpiryBackgroundService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);

        public JobExpiryBackgroundService(IServiceScopeFactory scopeFactory)
        {
            _scopeFactory = scopeFactory;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // 🎯 服務一啟動就先掃一次（不要傻等 5 分鐘），這樣部署上去之後，
            //    之前已經卡住的過期職缺會馬上被補救，不用等使用者剛好觸發某個手動操作
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ExpireOverdueJobsAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    // 🎯 背景服務本身絕對不能因為單次掃描出錯就整個死掉，記錄下來、等下一輪繼續掃就好
                    Console.WriteLine($"[JobExpiryBackgroundService] 掃描過期職缺時發生例外：{ex.Message}");
                }

                try
                {
                    await Task.Delay(CheckInterval, stoppingToken);
                }
                catch (TaskCanceledException)
                {
                    // 應用程式正常關閉時會走到這裡，不用特別處理
                }
            }
        }

        private async Task ExpireOverdueJobsAsync(CancellationToken stoppingToken)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var scheduler = scope.ServiceProvider.GetRequiredService<AutoInterviewSchedulingService>();

            var now = DateTime.Now;

            // 🎯 跟 JobController 公開列表頁「過了截止日 +1 天才真的看不到」的寬限邏輯保持一致，
            //    避免這裡跟前台判斷「職缺是否還算上架中」的標準不一樣，造成「前台看得到但後台已經下架」
            //    這種另一種不一致的情況
            var expiredJobIds = await db.Jobs
                .Where(j => j.IsActive && j.Deadline.AddDays(1) <= now)
                .Select(j => j.Id)
                .ToListAsync(stoppingToken);

            if (expiredJobIds.Count == 0) return;

            foreach (var jobId in expiredJobIds)
            {
                var job = await db.Jobs.FindAsync(new object?[] { jobId }, stoppingToken);
                if (job == null || !job.IsActive) continue; // 保險起見再檢查一次，避免競態下重複處理

                job.IsActive = false;
                job.UpdatedAt = now;
                await db.SaveChangesAsync(stoppingToken);

                Console.WriteLine($"[JobExpiryBackgroundService] 職缺「{job.Title}」（Id={job.Id}）截止日期已過，自動下架，檢查是否可以自動安排面試...");

                try
                {
                    var scheduled = await scheduler.TryAutoScheduleAsync(job.Id);
                    if (scheduled)
                    {
                        Console.WriteLine($"[JobExpiryBackgroundService] 職缺「{job.Title}」（Id={job.Id}）已自動建立面試房間");
                    }
                }
                catch (Exception ex)
                {
                    // 🎯 就算這個職缺的自動排程失敗（例如剛好有人同時在改資料），下架這件事本身已經存檔成功了，
                    //    不要因為排程失敗就讓下架也跟著回滾——下次掃描時 alreadyScheduled 判斷還是會再檢查一次
                    Console.WriteLine($"[JobExpiryBackgroundService] 職缺「{job.Title}」（Id={job.Id}）自動排程面試失敗：{ex.Message}");
                }
            }
        }
    }
}
