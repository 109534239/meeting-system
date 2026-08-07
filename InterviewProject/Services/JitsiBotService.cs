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
        }

        // 🎯 注入到頁面裡的錄影機腳本：Canvas 畫格 + Web Audio API 混音，錄成一份有畫面又有聲音的 webm
        private const string RecorderInitScript = @"
(function () {
    window.__recChunks = [];
    window.__recStartMs = Date.now();

    const canvas = document.createElement('canvas');
    canvas.width = 1280; canvas.height = 720;
    const ctx = canvas.getContext('2d');

    const audioCtx = new (window.AudioContext || window.webkitAudioContext)();
    const dest = audioCtx.createMediaStreamDestination();
    const connected = new WeakSet();

    function connectAudio(el) {
        if (connected.has(el)) return;
        connected.add(el);
        try {
            const src = audioCtx.createMediaElementSource(el);
            src.connect(dest);
        } catch (e) { /* 某些元素可能不支援或已連過，忽略即可 */ }
    }

    function scanMedia() {
        document.querySelectorAll('video, audio').forEach(connectAudio);
    }
    scanMedia();

    const observer = new MutationObserver(scanMedia);
    observer.observe(document.body, { childList: true, subtree: true });
    window.__mediaObserver = observer;

    function drawFrame() {
        ctx.fillStyle = '#000';
        ctx.fillRect(0, 0, canvas.width, canvas.height);
        const videos = Array.from(document.querySelectorAll('video')).filter(v => v.videoWidth > 0);
        if (videos.length > 0) {
            const cols = Math.ceil(Math.sqrt(videos.length));
            const rows = Math.ceil(videos.length / cols);
            const cellW = canvas.width / cols, cellH = canvas.height / rows;
            videos.forEach((v, i) => {
                const x = (i % cols) * cellW, y = Math.floor(i / cols) * cellH;
                try { ctx.drawImage(v, x, y, cellW, cellH); } catch (e) {}

                // 🎯 盡力而為：嘗試從 Jitsi 的 DOM 找出這個 video 對應的參與者名稱標籤，畫在左下角，
                //    這樣錄下來的檔案才看得出來哪一格是誰（不然每格都只是一個畫面，看不出角色）。
                //    Jitsi 不同版本 DOM 結構可能不完全一樣，抓不到名字就跳過，不影響畫面本身。
                try {
                    let label = '';
                    const container = v.closest('[id^=""participant_""]') || v.closest('.videocontainer') || v.parentElement;
                    if (container) {
                        const nameEl = container.querySelector('.displayname, [class*=""displayName""], [class*=""display-name""]');
                        if (nameEl && nameEl.textContent) label = nameEl.textContent.trim();
                    }
                    if (label) {
                        ctx.font = 'bold 16px sans-serif';
                        const textWidth = ctx.measureText(label).width;
                        ctx.fillStyle = 'rgba(0,0,0,0.65)';
                        ctx.fillRect(x + 6, y + cellH - 28, textWidth + 12, 22);
                        ctx.fillStyle = '#fff';
                        ctx.fillText(label, x + 12, y + cellH - 12);
                    }
                } catch (e) {}
            });
        }
        window.__rafId = requestAnimationFrame(drawFrame);
    }
    drawFrame();

    const canvasStream = canvas.captureStream(15);
    const combined = new MediaStream([
        ...canvasStream.getVideoTracks(),
        ...dest.stream.getAudioTracks()
    ]);

    const mimeCandidates = ['video/webm;codecs=vp8,opus', 'video/webm'];
    let mime = '';
    for (const m of mimeCandidates) { if (MediaRecorder.isTypeSupported(m)) { mime = m; break; } }

    const rec = new MediaRecorder(combined, mime ? { mimeType: mime } : {});
    rec.ondataavailable = e => { if (e.data && e.data.size > 0) window.__recChunks.push(e.data); };
    rec.start(1000);
    window.__mediaRecorder = rec;
})();
";

        // 🎯 停止錄影機、把錄到的內容轉成 base64 字串回傳給 C# 端（頁面關閉前一定要呼叫，不然錄到的內容會直接消失）
        private const string RecorderStopScript = @"
(function () {
    return new Promise((resolve) => {
        if (window.__mediaObserver) { try { window.__mediaObserver.disconnect(); } catch (e) {} }
        if (window.__rafId) { try { cancelAnimationFrame(window.__rafId); } catch (e) {} }

        if (!window.__mediaRecorder || window.__mediaRecorder.state === 'inactive') {
            resolve(null);
            return;
        }
        window.__mediaRecorder.onstop = () => {
            const blob = new Blob(window.__recChunks, { type: 'video/webm' });
            const durationMs = Date.now() - (window.__recStartMs || Date.now());

            function toBase64(finalBlob) {
                const reader = new FileReader();
                reader.onloadend = () => resolve(reader.result); // data:video/webm;base64,....
                reader.onerror = () => resolve(null);
                reader.readAsDataURL(finalBlob);
            }

            // 🐛 MediaRecorder 產出的 webm 預設沒有寫入正確時長，播放器沒辦法拖拉進度條，
            //    用 fix-webm-duration 把正確時長補進檔案標頭再輸出
            if (window.ysFixWebmDuration) {
                ysFixWebmDuration(blob, durationMs, (fixedBlob) => toBase64(fixedBlob || blob));
            } else {
                toBase64(blob);
            }
        };
        window.__mediaRecorder.stop();
    });
})();
";

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
                        "--disable-setuid-sandbox",
                        // 🐛 修正：Docker 容器預設的共享記憶體（/dev/shm）只有 64MB，
                        //    Chromium 不知道這件事，會照平常桌機的用法去用，馬上爆記憶體被系統強制關閉，
                        //    症狀就是啟動幾秒後直接噴 "Target page, context or browser has been closed"，
                        //    這個參數會讓 Chromium 改用 /tmp（吃主程式的記憶體額度）而不是 /dev/shm，避開這個問題
                        "--disable-dev-shm-usage",
                        "--disable-gpu",
                        "--disable-software-rasterizer",
                        "--disable-extensions",
                        "--no-zygote"
                    }
                };

                localBrowser = await localPlaywright.Chromium.LaunchAsync(launchOptions);

                localContext = await localBrowser.NewContextAsync(new BrowserNewContextOptions
                {
                    ViewportSize = new ViewportSize { Width = 1280, Height = 720 }
                    // 🐛 拿掉了 RecordVideoDir：Playwright 官方已知限制，這個內建錄影功能只錄「畫面」，
                    //    完全沒有任何錄音能力（GitHub 上從 2021 年就有人要求加音訊支援，到現在還沒做），
                    //    改成下面用頁面內部注入的自訂錄影機（Canvas 畫面 + Web Audio API 混音）取代
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

                // 🎯 注入自訂錄影機：用 Canvas 每禎把畫面上所有人的 <video> 畫格畫進去（簡單網格排版），
                //    用 Web Audio API 把所有人的 <video>/<audio> 元素的聲音混在一起，
                //    兩個一起餵進同一個 MediaRecorder，才會是「有畫面又有聲音」的完整檔案。
                //    用 MutationObserver 持續偵測新加入的參與者（他們的 <video>/<audio> 元素是動態加進 DOM 的）。
                try
                {
                    // 🐛 MediaRecorder 產出的 webm 預設沒有寫入正確時長中繼資料，播放器沒辦法拖拉進度條，
                    //    跟舊版真人螢幕分享錄影當初踩過的坑一樣，這裡也要注入同一個函式庫來修正
                    await page.AddScriptTagAsync(new PageAddScriptTagOptions
                    {
                        Url = "https://cdn.jsdelivr.net/npm/fix-webm-duration@1.0.6/fix-webm-duration.js"
                    });
                    await page.EvaluateAsync(RecorderInitScript);
                    Console.WriteLine($"[JitsiBot] 房間 {roomCode} 自訂錄影機（畫面+聲音）已啟動。");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[JitsiBot] 啟動自訂錄影機失敗：{ex.Message}（這場面試會沒有錄影）");
                }

                _activeBots[roomCode] = new BotInstance
                {
                    PlaywrightInstance = localPlaywright,
                    BrowserInstance = localBrowser,
                    ContextInstance = localContext,
                    PageInstance = page
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
        //    拿不到錄影（例如根本沒成功加入過、或錄影機沒啟動成功）就回傳 null，呼叫端要自己處理「沒有錄影」的情況
        public async Task<string?> LeaveRoomAsync(string roomCode)
        {
            if (string.IsNullOrEmpty(roomCode)) return null;
            roomCode = roomCode.Trim();

            if (!_activeBots.TryGetValue(roomCode, out var instance)) return null;

            string? videoPath = null;
            try
            {
                // 🎯 一定要在頁面關閉「之前」呼叫，停止錄影機、把錄到的內容轉成 base64 拿出來，
                //    不然頁面一關閉，錄到的內容（存在頁面自己的記憶體裡）就直接消失、什麼都拿不到
                var dataUrl = await instance.PageInstance.EvaluateAsync<string?>(RecorderStopScript);

                if (!string.IsNullOrEmpty(dataUrl) && dataUrl.Contains(","))
                {
                    var base64 = dataUrl.Substring(dataUrl.IndexOf(',') + 1);
                    var bytes = Convert.FromBase64String(base64);

                    var recordDir = Path.Combine(Path.GetTempPath(), "jitsibot_rec_" + roomCode);
                    Directory.CreateDirectory(recordDir);
                    videoPath = Path.Combine(recordDir, "recording.webm");
                    await File.WriteAllBytesAsync(videoPath, bytes);

                    Console.WriteLine($"[JitsiBot] 房間 {roomCode} 錄影已完成（{bytes.Length / 1024 / 1024}MB）：{videoPath}");
                }
                else
                {
                    Console.WriteLine($"[JitsiBot] 房間 {roomCode} 沒有取得錄影內容（錄影機可能沒有成功啟動）。");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[JitsiBot] 取得錄影檔失敗: {ex.Message}");
            }

            try
            {
                await instance.PageInstance.CloseAsync();
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