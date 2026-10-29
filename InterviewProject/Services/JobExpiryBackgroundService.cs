using InterviewProject.Data;
using Microsoft.EntityFrameworkCore;
using InterviewProject.Models;
using Npgsql;
using System.Data;


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
                    // 新增：處理已下架職缺的 AI 履歷評分與比較分析
                    await ProcessPendingJobAiAnalysisAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"[JobExpiryBackgroundService] AI 分析流程失敗：{ex.Message}");
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

        private async Task ProcessPendingJobAiAnalysisAsync(CancellationToken stoppingToken)
        {
            using var scope = _scopeFactory.CreateScope();

            var db = scope.ServiceProvider
                .GetRequiredService<AppDbContext>();

            var scoringService = scope.ServiceProvider
                .GetRequiredService<ResumeAiScoringService>();

            var comparisonService = scope.ServiceProvider
            .GetRequiredService<ApplicantComparisonService>();

            // 尋找已下架、尚未產生比較報告的職缺
            var jobIds = await db.Jobs
                .AsNoTracking()
                .Where(j =>
                    !j.IsActive &&
                    (j.AiComparisonReport == null ||
                     j.AiComparisonReport == ""))
                .Select(j => j.Id)
                .ToListAsync(stoppingToken);

            foreach (var jobId in jobIds)
            {
                stoppingToken.ThrowIfCancellationRequested();

                NpgsqlConnection? lockConnection = null;
                bool lockAcquired = false;

                try
                {
                    // 使用獨立連線持有 PostgreSQL 工作階段鎖
                    lockConnection = new NpgsqlConnection(
                        db.Database.GetConnectionString());

                    await lockConnection.OpenAsync(stoppingToken);

                    lockAcquired = await TryAcquireAiAnalysisLockAsync(
                        lockConnection,
                        jobId,
                        stoppingToken);

                    if (!lockAcquired)
                    {
                        Console.WriteLine(
                            $"[AI 分析] JobId={jobId} 已有其他背景工作處理，跳過本輪。");

                        continue;
                    }

                    // 取得鎖後再次確認職缺仍需要 AI 分析
                    var needsAnalysis = await db.Jobs
                        .AsNoTracking()
                        .AnyAsync(j =>
                            j.Id == jobId &&
                            !j.IsActive &&
                            (j.AiComparisonReport == null ||
                             j.AiComparisonReport == ""),
                            stoppingToken);

                    if (!needsAnalysis)
                        continue;

                    // 只處理正式投遞的履歷
                    var resumes = await db.Resumes
                        .AsNoTracking()
                        .Where(r =>
                            r.JobsId == jobId &&
                            r.Status != "暫存")
                        .Select(r => new
                        {
                            r.Id,
                            r.AiScore,
                            r.AiComment
                        })
                        .ToListAsync(stoppingToken);

                    // 只評分尚未完成的履歷
                    var pendingIds = resumes
                        .Where(r =>
                            !r.AiScore.HasValue ||
                            string.IsNullOrWhiteSpace(r.AiComment))
                        .Select(r => r.Id)
                        .ToList();

                    bool allSucceeded = true;

                    foreach (var resumeId in pendingIds)
                    {
                        stoppingToken.ThrowIfCancellationRequested();

                        var result =
                            await scoringService.ScoreResumeAsync(resumeId);

                        if (!result.IsSuccess)
                        {
                            allSucceeded = false;

                            Console.WriteLine(
                                $"[AI 評分] ResumeId={resumeId} 失敗：{result.Message}");
                        }
                    }

                    if (!allSucceeded)
                        continue;

                    // 重新查詢，確認每份履歷均已儲存評分結果
                    var allScored = resumes.Count > 0 &&
                    await db.Resumes
                        .AsNoTracking()
                        .Where(r =>
                            r.JobsId == jobId &&
                            r.Status != "暫存")
                        .AllAsync(r =>
                            r.AiScore.HasValue &&
                            r.AiComment != null &&
                            r.AiComment != "",
                            stoppingToken);

                    if (!allScored)
                        continue;

                    // 個別履歷已完成評分，但不足兩位不產生比較報告
                    if (resumes.Count < 2)
                        continue;

                    // 重新取得職缺與完整需求資料
                    var job = await db.Jobs
                        .Include(j => j.SkillTags)
                        .Include(j => j.MajorRequirements)
                        .Include(j => j.LanguageRequirements)
                        .AsSplitQuery()
                        .FirstOrDefaultAsync(j => j.Id == jobId, stoppingToken);

                    if (job == null || job.IsActive ||
                        !string.IsNullOrWhiteSpace(job.AiComparisonReport))
                    {
                        continue;
                    }

                    // 重新讀取所有正式投遞履歷的評分結果
                    var scoredResumes = await db.Resumes
                        .AsNoTracking()
                        .Where(r => r.JobsId == jobId && r.Status != "暫存")
                        .OrderBy(r => r.Id)
                        .Select(r => new
                        {
                            r.Id,
                            r.AiScore,
                            r.AiComment
                        })
                        .ToListAsync(stoppingToken);

                    if (scoredResumes.Count < 2 ||
                        scoredResumes.Any(r =>
                            !r.AiScore.HasValue ||
                            string.IsNullOrWhiteSpace(r.AiComment)))
                    {
                        continue;
                    }

                    // 建立比較分析輸入
                    var input = new ApplicantComparisonInput
                    {
                        JobTitle = job.Title ?? "",
                        JobDescription = job.Description ?? "",
                        JobRequirements = BuildComparisonJobRequirements(job),

                        Candidates = scoredResumes.Select(r =>
                            new ApplicantComparisonCandidate
                            {
                                ResumeId = r.Id,
                                MemberName = $"應徵者 {r.Id}",
                                AiScore = r.AiScore!.Value,
                                AiComment = r.AiComment!
                            }).ToList()
                    };

                    // 呼叫 Gemini 產生比較報告，不重新評分
                    var report = await comparisonService.GenerateComparisonAsync(input);

                    if (string.IsNullOrWhiteSpace(report))
                    {
                        Console.WriteLine(
                            $"[AI 比較分析] JobId={jobId} 回傳空白報告");
                        continue;
                    }

                    // 儲存前再次確認職缺仍為下架狀態
                    await db.Entry(job).ReloadAsync(stoppingToken);

                    if (job.IsActive ||
                        !string.IsNullOrWhiteSpace(job.AiComparisonReport))
                    {
                        continue;
                    }

                    // 重新讀取履歷評分，確認與送給 Gemini 的資料一致
                    var latestResumes = await db.Resumes
                        .AsNoTracking()
                        .Where(r => r.JobsId == jobId && r.Status != "暫存")
                        .OrderBy(r => r.Id)
                        .Select(r => new
                        {
                            r.Id,
                            r.AiScore,
                            r.AiComment
                        })
                        .ToListAsync(stoppingToken);

                    bool resumesUnchanged =
                        latestResumes.Count == scoredResumes.Count &&
                        latestResumes.Zip(scoredResumes, (latest, original) =>
                            latest.Id == original.Id &&
                            latest.AiScore == original.AiScore &&
                            latest.AiComment == original.AiComment)
                        .All(same => same);

                    if (!resumesUnchanged)
                    {
                        Console.WriteLine(
                            $"[AI 比較分析] JobId={jobId} 履歷資料已變動，取消儲存本次報告。");

                        continue;
                    }

                    // 儲存比較分析報告
                    job.AiComparisonReport = report;

                    await db.SaveChangesAsync(stoppingToken);

                    Console.WriteLine(
                        $"[AI 比較分析] JobId={jobId} 比較報告已儲存");
                }
                catch (OperationCanceledException)
                    when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"[AI 分析] JobId={jobId} 處理失敗：{ex.Message}");
                }

                finally
                {
                    if (lockConnection != null)
                    {
                        try
                        {
                            if (lockAcquired &&
                                lockConnection.State == ConnectionState.Open)
                            {
                                await ReleaseAiAnalysisLockAsync(
                                    lockConnection,
                                    jobId);
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine(
                                $"[AI 分析] JobId={jobId} 釋放資料庫鎖失敗：{ex.Message}");
                        }
                        finally
                        {
                            await lockConnection.DisposeAsync();
                        }
                    }
                }
            }
        }

        // 整理職缺條件，提供給 AI 應徵者比較分析使用
        private static string BuildComparisonJobRequirements(Job job)
        {
            var parts = new List<string>();

            // 工作經歷要求
            if (!string.IsNullOrWhiteSpace(job.ExperienceRequired))
                parts.Add("工作經歷要求：" + job.ExperienceRequired);

            // 產業經歷要求
            if (!string.IsNullOrWhiteSpace(job.IndustryExperience))
                parts.Add("產業經歷要求：" + job.IndustryExperience);

            // 學歷要求
            if (!string.IsNullOrWhiteSpace(job.EducationRequired))
                parts.Add("學歷要求：" + job.EducationRequired);

            // 專業技能要求
            if (job.SkillTags != null && job.SkillTags.Any())
                parts.Add("技能要求：" +
                    string.Join("、", job.SkillTags.Select(x => x.Tag)));

            // 科系要求
            if (job.MajorRequirements != null &&
                job.MajorRequirements.Any())
                parts.Add("科系要求：" +
                    string.Join("、",
                        job.MajorRequirements.Select(x => x.Major)));

            // 語言能力要求
            if (job.LanguageRequirements != null &&
                job.LanguageRequirements.Any())
                parts.Add("語言要求：" +
                    string.Join("、",
                        job.LanguageRequirements.Select(x =>
                            $"{x.Language}（{x.Degree}）")));

            // 專業證照要求
            if (!string.IsNullOrWhiteSpace(job.CertRequired))
                parts.Add("證照要求：" + job.CertRequired);

            // 其他職缺條件
            if (!string.IsNullOrWhiteSpace(job.OtherRequirements))
                parts.Add("其他要求：" + job.OtherRequirements);

            return parts.Count > 0
                ? string.Join("\n", parts)
                : "無";
        }

        // AI 分析專用鎖，避免同一職缺被多個背景服務同時處理
        private static async Task<bool> TryAcquireAiAnalysisLockAsync(
            NpgsqlConnection connection,
            int jobId,
            CancellationToken stoppingToken)
        {
            await using var command = new NpgsqlCommand(
                "SELECT pg_try_advisory_lock(@namespace, @jobId)",
                connection);

            command.Parameters.AddWithValue("namespace", 20261029);
            command.Parameters.AddWithValue("jobId", jobId);

            var result = await command.ExecuteScalarAsync(stoppingToken);

            return result is bool acquired && acquired;
        }

        // 釋放 AI 分析專用鎖
        private static async Task ReleaseAiAnalysisLockAsync(
            NpgsqlConnection connection,
            int jobId)
        {
            await using var command = new NpgsqlCommand(
                "SELECT pg_advisory_unlock(@namespace, @jobId)",
                connection);

            command.Parameters.AddWithValue("namespace", 20261029);
            command.Parameters.AddWithValue("jobId", jobId);

            await command.ExecuteScalarAsync();
        }
    }
}
