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
            public IPlaywright PlaywrightInstance { get; set; }
            public IBrowser BrowserInstance { get; set; }
        }

        private static readonly Dictionary<string, BotInstance> _activeBots = new();

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
                var context = await localBrowser.NewContextAsync();
                var page = await context.NewPageAsync();

                // 🚀 【官方標準網址結構】：https://8x8.vc/你的AppID/房間代碼
                string jitsiDomain = "https://8x8.vc"; 
                string tenantId = "vpaas-magic-cookie-00203058b8f244a0a520be67d341b527";

                // 組合完整的 URL 並注入跳過確認畫面參數
                string targetUrl = $"{jitsiDomain}/{tenantId}/{roomCode}#config.startWithAudioMuted=true&config.startWithVideoMuted=false&config.prejoinPageEnabled=false&config.lobby.enableLobby=false";

                Console.WriteLine($"[JitsiBot 導航] AI 面試官正在前往官方雲端會議室：{targetUrl}");
                
                await page.GotoAsync(targetUrl, new PageGotoOptions { Timeout = 60000 });

                _activeBots[roomCode] = new BotInstance
                {
                    PlaywrightInstance = localPlaywright,
                    BrowserInstance = localBrowser
                };

                Console.WriteLine($"[JitsiBot Success] 房間 {roomCode} 的 AI 面試官已成功常駐會議！");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[JitsiBot Error] 啟動失敗: {ex.Message}");
                if (localBrowser != null) await localBrowser.CloseAsync();
                localPlaywright?.Dispose();
                _activeBots.Remove(roomCode);
                throw;
            }
        }

        public async Task LeaveRoomAsync(string roomCode)
        {
            if (string.IsNullOrEmpty(roomCode)) return;
            roomCode = roomCode.Trim();

            if (_activeBots.TryGetValue(roomCode, out var instance))
            {
                try
                {
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
            }
        }
    }
}