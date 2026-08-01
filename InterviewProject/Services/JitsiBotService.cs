using Microsoft.Playwright;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace InterviewProject.Services
{
    public class JitsiBotService
    {
        private class BotInstance
        {
            public IPlaywright PlaywrightInstance { get; set; } = null!;
            public IBrowser BrowserInstance { get; set; } = null!;
            public IBrowserContext ContextInstance { get; set; } = null!;
            public IPage PageInstance { get; set; } = null!;
            public string RecordDir { get; set; } = "";
        }

        private static readonly Dictionary<string, BotInstance> _activeBots = new();
        private readonly JaasJwtService _jaasJwt;

        public JitsiBotService(JaasJwtService jaasJwt)
        {
            _jaasJwt = jaasJwt;
        }

        public async Task JoinRoomAsync(string roomCode, string videoPath)
        {
            if (string.IsNullOrEmpty(roomCode)) return;
            roomCode = roomCode.Trim();

            if (_activeBots.ContainsKey(roomCode))
            {
                Console.WriteLine($"[JitsiBot] 房間 {roomCode} 內已有 AI 面試官，跳過。");
                return;
            }

            if (!File.Exists(videoPath))
            {
                Console.WriteLine($"[JitsiBot Error] 找不到 y4m 視訊檔案: {videoPath}");
                return;
            }

            IPlaywright localPlaywright = null;
            IBrowser localBrowser = null;
            IBrowserContext localContext = null;

            try
            {
                Console.WriteLine($"[JitsiBot] 正在為房間 {roomCode} 建立全隔離的 Playwright 驅動...");
                localPlaywright = await Playwright.CreateAsync();

                var launchOptions = new BrowserTypeLaunchOptions
                {
                    Headless = true, // Render 環境必須為 true
                    Args = new[]
                    {
                        "--use-fake-ui-for-media-stream",
                        "--use-fake-device-for-media-stream",
                        $"--use-file-for-fake-video-capture={videoPath}",
                        "--no-sandbox",
                        "--disable-setuid-sandbox"
                    }
                };

                localBrowser = await localPlaywright.Chromium.LaunchAsync(launchOptions);

                // 🎯 這場面試專屬的暫存錄影資料夾（結束時會把裡面的 .webm 讀出來上傳到 R2，之後就可以刪掉）
                var recordDir = Path.Combine(Path.GetTempPath(), "jitsibot_rec_" + roomCode);
                Directory.CreateDirectory(recordDir);

                localContext = await localBrowser.NewContextAsync(new BrowserNewContextOptions
                {
                    ViewportSize = new ViewportSize { Width = 1280, Height = 720 },
                    RecordVideoDir = recordDir,
                    RecordVideoSize = new RecordVideoSize { Width = 1280, Height = 720 }
                });
                var page = await localContext.NewPageAsync();

                // 🚀 換成你自己申請的 JaaS AppID（原本那組是別人的示範帳號）
                string jitsiDomain = "https://8x8.vc";
                string tenantId = "vpaas-magic-cookie-c12aeb6abc7a4349bc799bb8cb31436a";

                // 🎯 JaaS 需要 JWT 才能真的加入會議，AI 面試官不是主持人，moderator=false
                string botJwt = _jaasJwt.GenerateToken(roomCode, "ai-interviewer", "AI 面試官（王大明）", isModerator: false);

                // 組合完整的 URL 並注入跳過確認畫面參數 + JWT
                //   🎯 disableTileView：讓畫面固定用「目前誰在說話就放大顯示、其他人縮圖排在旁邊」的排版，
                //      不要讓 Jitsi 切成棋盤格 tile view（新版 Jitsi 在人數少時有時會預設用 tile view）
                //   🎯 prejoinPageEnabled=false + prejoinConfig.enabled=false：兩個都帶，
                //      因為新版 Jitsi 把這個設定從舊的扁平 key 改成巢狀的 prejoinConfig.enabled，
                //      只帶舊的 key 在新版 JaaS 上可能完全沒作用，畫面還是會卡在「預備加入」那頁
                string targetUrl = $"{jitsiDomain}/{tenantId}/{roomCode}?jwt={botJwt}#config.startWithAudioMuted=true&config.startWithVideoMuted=false&config.prejoinPageEnabled=false&config.prejoinConfig.enabled=false&config.lobby.enableLobby=false&config.disableTileView=true";

                Console.WriteLine($"[JitsiBot 導航] AI 面試官正在前往官方雲端會議室：{targetUrl}");

                await page.GotoAsync(targetUrl, new PageGotoOptions { Timeout = 60000 });

                // 🎯 保險：就算上面兩個設定都帶了，還是可能因為 JaaS 那邊的版本/設定鎖死而繼續顯示「預備加入」畫面，
                //    這裡再主動找一次常見的「加入會議」按鈕，找得到就點掉；找不到就當作正常直接進了會議，不影響流程
                try
                {
                    var joinButton = page.Locator(
                        "[data-testid='prejoin.joinMeeting'], " +
                        "button:has-text('加入會議'), button:has-text('Join Meeting'), button:has-text('Join meeting')"
                    );
                    await joinButton.First.ClickAsync(new LocatorClickOptions { Timeout = 8000 });
                    Console.WriteLine($"[JitsiBot] 房間 {roomCode} 偵測到「預備加入」畫面，已自動點擊加入。");
                    await page.WaitForTimeoutAsync(1500); // 給一點時間讓畫面真的切換進會議室
                }
                catch
                {
                    Console.WriteLine($"[JitsiBot] 房間 {roomCode} 沒有偵測到「預備加入」畫面（正常情況，代表已直接進入會議）。");
                }

                // 🎯 除錯用：不管有沒有點到按鈕，都存一張目前畫面的截圖，
                //    下次如果錄影內容還是不對，直接看這張截圖就知道機器人卡在哪個畫面，不用再靠猜的
                try
                {
                    var debugDir = Path.Combine(Path.GetTempPath(), "jitsibot_debug");
                    Directory.CreateDirectory(debugDir);
                    var debugPath = Path.Combine(debugDir, $"{roomCode}_after_join.png");
                    await page.ScreenshotAsync(new PageScreenshotOptions { Path = debugPath });
                    Console.WriteLine($"[JitsiBot] 除錯截圖已存到：{debugPath}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[JitsiBot] 存除錯截圖失敗：{ex.Message}");
                }

                _activeBots[roomCode] = new BotInstance
                {
                    PlaywrightInstance = localPlaywright,
                    BrowserInstance = localBrowser,
                    ContextInstance = localContext,
                    PageInstance = page,
                    RecordDir = recordDir
                };

                Console.WriteLine($"[JitsiBot Success] 房間 {roomCode} 的 AI 面試官已成功常駐會議，並開始錄影！");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[JitsiBot Error] 啟動失敗: {ex.Message}");
                if (localContext != null) { try { await localContext.CloseAsync(); } catch { } }
                if (localBrowser != null) await localBrowser.CloseAsync();
                localPlaywright?.Dispose();
                _activeBots.Remove(roomCode);
                throw;
            }
        }

        // 🎯 離開會議室，並回傳這場會議的本機錄影檔路徑（呼叫端負責讀取、上傳到 R2，再自行刪除暫存檔）
        //    拿不到錄影（例如根本沒成功加入過）就回傳 null，呼叫端要自己處理「沒有錄影」的情況
        public async Task<string?> LeaveRoomAsync(string roomCode)
        {
            if (string.IsNullOrEmpty(roomCode)) return null;
            roomCode = roomCode.Trim();

            if (!_activeBots.TryGetValue(roomCode, out var instance)) return null;

            string? videoPath = null;
            try
            {
                // Playwright 的錄影要等「頁面關閉」之後才會真的把檔案寫完、才拿得到最終路徑
                await instance.PageInstance.CloseAsync();
                if (instance.PageInstance.Video != null)
                {
                    videoPath = await instance.PageInstance.Video.PathAsync();
                }
                Console.WriteLine($"[JitsiBot] 房間 {roomCode} 錄影已完成：{videoPath ?? "（沒有錄到檔案）"}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[JitsiBot] 取得錄影檔失敗: {ex.Message}");
            }

            try
            {
                await instance.ContextInstance.CloseAsync();
                await instance.BrowserInstance.CloseAsync();
                instance.PlaywrightInstance.Dispose();
                Console.WriteLine($"[JitsiBot] 房間 {roomCode} 的 AI 面試官已成功離開並釋放資源。");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[JitsiBot] 釋放資源失敗: {ex.Message}");
            }
            finally
            {
                _activeBots.Remove(roomCode);
            }

            return videoPath;
        }
    }
}