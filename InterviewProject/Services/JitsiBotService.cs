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
    const connectedTrackIds = new Set(); // 🎯 記錄「已經透過任何管道接上的原始音軌 id」，避免同一個人的聲音被接兩次造成疊音

    function connectAudio(el) {
        if (connected.has(el)) return;
        // 🎯 如果這個 <video>/<audio> 元素背後的音軌，已經透過下面 connectJitsiTracks() 直接接過了，
        //    這裡就不要再重複接一次，不然同一個人的聲音會疊兩層、錄出來音量異常/有回音
        try {
            const stream = el.srcObject;
            if (stream && typeof stream.getAudioTracks === 'function') {
                const tracks = stream.getAudioTracks();
                if (tracks.length > 0 && tracks.every(t => connectedTrackIds.has(t.id))) {
                    connected.add(el);
                    return;
                }
            }
        } catch (e) {}

        connected.add(el);
        try {
            const src = audioCtx.createMediaElementSource(el);
            src.connect(dest);
            try {
                const stream = el.srcObject;
                if (stream && typeof stream.getAudioTracks === 'function') {
                    stream.getAudioTracks().forEach(t => connectedTrackIds.add(t.id));
                }
            } catch (e) {}
        } catch (e) { /* 某些元素可能不支援或已連過，忽略即可 */ }
    }

    function scanMedia() {
        document.querySelectorAll('video, audio').forEach(connectAudio);
    }

    // 🎯 這輪新增：不要只靠掃描 DOM 上「看得到的」<video>/<audio> 元素接聲音——
    //    使用者這輪回報「不確定有沒有錄到所有人的聲音」，根本原因可能是 Jitsi 某些版本/設定下，
    //    遠端participant的聲音是透過內部的 Web Audio 混音處理、沒有對應到一個 DOM 上找得到的
    //    <audio> 元素（或者對應的 <video> 元素被設成 muted，聲音改由我們抓不到的地方播放），
    //    這種情況下單靠掃 DOM 會漏掉那個人的聲音。
    //    改成「雙管齊下」：優先直接從 Jitsi 內部的會議物件（window.APP.conference）拿到每個人
    //    最原始的音訊 MediaStreamTrack，直接接進我們的錄音圖裡，不管 DOM 上看不看得到、
    //    有沒有被 muted，都能拿到真正的原始聲音。DOM 掃描機制still保留當作備援（萬一內部 API 抓不到)。
    function connectJitsiTracks() {
        try {
            const room = window.APP && window.APP.conference && window.APP.conference._room;
            if (!room || typeof room.getParticipants !== 'function') return;

            const nativeTracks = [];
            room.getParticipants().forEach(p => {
                try {
                    const tracks = typeof p.getTracks === 'function' ? p.getTracks() : [];
                    tracks.forEach(t => {
                        if (t && typeof t.isAudioTrack === 'function' && t.isAudioTrack() && typeof t.getTrack === 'function') {
                            const nt = t.getTrack();
                            if (nt) nativeTracks.push(nt);
                        }
                    });
                } catch (e) {}
            });
            // 本地（AI 面試官自己）的音軌理論上是靜音狀態，但一起處理，不特別排除，反正靜音軌接了也不會有聲音
            try {
                const localAudio = window.APP.conference.localAudio;
                if (localAudio && typeof localAudio.getTrack === 'function') {
                    const nt = localAudio.getTrack();
                    if (nt) nativeTracks.push(nt);
                }
            } catch (e) {}

            nativeTracks.forEach(track => {
                if (!track || connectedTrackIds.has(track.id)) return;
                try {
                    const stream = new MediaStream([track]);
                    const src = audioCtx.createMediaStreamSource(stream);
                    src.connect(dest);
                    connectedTrackIds.add(track.id);
                } catch (e) {}
            });
        } catch (e) {}
    }

    // 🐛 這輪新增：AI 面試官自己的畫面（男性面試官.y4m 那個假攝影機）在錄影裡永遠只顯示「未開啟鏡頭」佔位格，
    //    從沒真的錄到內容——實測發現 Jitsi 對「自己本人」的鏡頭畫面，不一定會渲染出一個畫面上抓得到的
    //    <video> 元素（不像遠端參與者一定看得到），單純掃 DOM 完全抓不到自己的畫面。
    //    修正方式：比照上面 connectJitsiTracks() 抓聲音軌道的做法，直接從 Jitsi 內部的會議物件拿到
    //    每個人（含 AI 面試官自己）最原始的視訊 MediaStreamTrack，自己動手建一個「藏起來、畫面外」的
    //    <video> 元素把這條原始軌道接上去，不管 Jitsi 自己的畫面有沒有渲染出東西，我們都能拿到真正的畫面。
    //    這些自己建的 video 元素會記錄對應的參與者 id 跟名字，畫格時直接用，不用再靠 DOM 去猜是誰。
    const syntheticVideoTrackIds = new Set();
    const syntheticVideoCells = []; // { el, participantId, label }

    function connectJitsiVideoTracks() {
        try {
            const room = window.APP && window.APP.conference && window.APP.conference._room;
            if (!room || typeof room.getParticipants !== 'function') return;

            const nameMap = getParticipantNameMap();
            const candidates = []; // { track, participantId }

            room.getParticipants().forEach(p => {
                try {
                    const tracks = typeof p.getTracks === 'function' ? p.getTracks() : [];
                    tracks.forEach(t => {
                        if (t && typeof t.isVideoTrack === 'function' && t.isVideoTrack() && typeof t.getTrack === 'function') {
                            const nt = t.getTrack();
                            if (nt) candidates.push({ track: nt, participantId: p.id || null });
                        }
                    });
                } catch (e) {}
            });
            // 本地（AI 面試官自己）的視訊軌道——這個就是「男性面試官.y4m」那個假攝影機畫面，
            // 這輪修正的重點就是它，一定要抓到
            try {
                const localVideo = window.APP.conference.localVideo;
                if (localVideo && typeof localVideo.getTrack === 'function') {
                    const nt = localVideo.getTrack();
                    const localId = (window.APP.conference.getMyUserId && window.APP.conference.getMyUserId()) || 'local';
                    if (nt) candidates.push({ track: nt, participantId: localId });
                }
            } catch (e) {}

            candidates.forEach(({ track, participantId }) => {
                if (!track || syntheticVideoTrackIds.has(track.id)) return;
                try {
                    const v = document.createElement('video');
                    v.autoplay = true; v.muted = true; v.playsInline = true;
                    v.style.position = 'fixed'; v.style.left = '-9999px'; v.style.top = '-9999px'; // 藏起來，不用真的顯示在畫面上
                    v.srcObject = new MediaStream([track]);
                    document.body.appendChild(v);

                    syntheticVideoTrackIds.add(track.id);
                    const label = (participantId && nameMap[participantId]) ? nameMap[participantId] : '';
                    syntheticVideoCells.push({ el: v, participantId, label, trackId: track.id });
                } catch (e) {}
            });
        } catch (e) {}
    }

    scanMedia();
    connectJitsiTracks();
    connectJitsiVideoTracks();

    const observer = new MutationObserver(() => { scanMedia(); connectJitsiTracks(); connectJitsiVideoTracks(); });
    observer.observe(document.body, { childList: true, subtree: true });
    window.__mediaObserver = observer;
    // 🎯 DOM 變動事件不一定會在「新的音軌/視訊軌道加入會議」的當下觸發（軌道可能是動態加進已存在的元素，不算 DOM 變動），
    //    保險起見每 2 秒也主動掃一次 Jitsi 內部的軌道清單，確保晚加入或晚開鏡頭/麥克風的人也不會被漏掉
    window.__trackPollInterval = setInterval(() => { connectJitsiTracks(); connectJitsiVideoTracks(); }, 2000);

    // 🎯 盡力而為：從 Jitsi 的畫面找出每個 video 對應的參與者名字，畫在左下角當標籤。
    //    這輪修正的重點：上一輪只有「DOM 容器內找」+「DOM 全域用 id 找」+「靠順序用猜的」三層，
    //    使用者這輪回報「其中一位主管的畫面被錯認成 AI 面試官」——很可能就是「靠順序猜」那層猜錯了
    //    （AI 面試官剛好也叫王大明，跟其中一位人類主管同名，順序稍微對不齊就會猜到別人身上，
    //    而且猜錯成「AI 面試官」這種特定角色又特別容易造成混淆，比完全沒標籤更糟）。
    //    這輪把「靠順序猜」整個拿掉，改成最優先直接查 Jitsi 內部的參與者資料（window.APP 的 redux store），
    //    這是最權威的資料來源——不管這個 video 現在是不是主畫面、DOM 結構長怎樣，都查得到正確的名字，
    //    查不到才退回原本的 DOM 容器/全域搜尋這兩層；三層都查不到，寧可留空、不要用猜的。
    function getParticipantNameMap() {
        const map = {};
        try {
            const state = window.APP && window.APP.store && typeof window.APP.store.getState === 'function'
                ? window.APP.store.getState() : null;
            const participantsState = state && state['features/base/participants'];
            if (!participantsState) return map;

            const collect = (obj) => {
                if (!obj) return;
                const arr = Array.isArray(obj) ? obj : Object.values(obj);
                arr.forEach(p => { if (p && p.id && p.name) map[p.id] = p.name; });
            };
            collect(participantsState.remote);
            if (participantsState.local && participantsState.local.id) {
                map[participantsState.local.id] = participantsState.local.name || map[participantsState.local.id] || '';
            }
        } catch (e) {}
        return map;
    }

    function extractParticipantId(el) {
        if (!el) return null;
        const idAttr = el.id || '';
        // 常見樣式：id=""participant_<id>""、id=""remoteVideo_<id>""、id=""video_<id>""、data-participant-id=""<id>""
        let m = idAttr.match(/(?:participant|remoteVideo|video)_([A-Za-z0-9]+)/);
        if (m) return m[1];
        if (el.dataset && el.dataset.participantId) return el.dataset.participantId;
        return null;
    }

    function findLabelForVideo(v, nameMap) {
        // 第一層（最可信）：video 元素自己（或它的祖先，最多往上找 5 層）身上的參與者 id，
        //    直接去 Jitsi 內部的權威資料（redux store）查名字——不依賴畫面上有沒有渲染出可見的文字標籤
        try {
            let idHolder = v;
            let pid = extractParticipantId(idHolder);
            let hops = 0;
            while (!pid && idHolder && idHolder.parentElement && hops < 5) {
                idHolder = idHolder.parentElement;
                pid = extractParticipantId(idHolder);
                hops++;
            }
            if (pid && nameMap[pid]) return nameMap[pid];
        } catch (e) {}

        // 第二層：這個 video 最近的容器裡面直接找看得到的名字標籤文字（範圍限定在同一個容器內，
        //    不會誤配到別人身上）
        try {
            const container = v.closest('[id^=""participant_""]') || v.closest('.videocontainer') || v.parentElement;
            if (container) {
                const nameEl = container.querySelector('.displayname, [class*=""displayName""], [class*=""display-name""]');
                if (nameEl && nameEl.textContent && nameEl.textContent.trim()) return nameEl.textContent.trim();
            }
        } catch (e) {}

        // 🐛 這輪拿掉了原本的「第三層：用擷取到的 id 片段去整個文件範圍做子字串比對」——
        //    這個做法用的是 `[id*=""...""]`（包含比對，不是完全比對），如果擷取到的 id 片段剛好很短、
        //    或者跟別人的 id 有重疊部分，就會誤配到別的參與者身上，這正是「有位主管的畫面被誤標成
        //    AI 面試官」的根本原因（兩人剛好都叫王大明，一旦誤配到彼此的 id，名字就直接連過去了）。
        //    寧可查不到就留空，也不要用這種容易誤配的方式硬猜。
        return '';
    }

    function drawFrame() {
        ctx.fillStyle = '#000';
        ctx.fillRect(0, 0, canvas.width, canvas.height);

        const nameMap = getParticipantNameMap();

        // 🐛 這輪修正的問題：
        // 1. 「同一張臉出現兩次」：Jitsi 在 speaker/stage view 底下，同一位參與者的畫面常常會同時存在
        //    兩份 DOM（主畫面的大 video + filmstrip 縮圖列的小 video），兩個 <video> 元素背後接的是
        //    同一條 MediaStreamTrack。原本沒有去重，兩個都被畫進格子，看起來就像同一個人重複出現。
        //    改成用 <video> 元素背後 srcObject 的視訊軌道 id 當唯一鍵去重，同一條軌道只畫一次。
        // 2. 「沒開鏡頭的人完全看不到、也沒有名字標籤」：原本只掃「畫面上找得到的 <video> 元素」，
        //    沒開鏡頭的人根本沒有 <video> 元素可抓，整個人就這樣從錄影裡消失，連名字都不會出現。
        //    改成額外比對 Jitsi 內部權威的參與者名單（getParticipantNameMap()），
        //    把「有名字、但沒有對應到任何一格畫面」的人，補一格灰底＋姓名的佔位格，
        //    至少看得出這場面試「誰在場但沒開鏡頭」，不是憑空消失。
        // 3. 「AI 面試官自己的畫面錄不到」：改用 connectJitsiVideoTracks() 直接接上的隱藏 video 元素
        //    （syntheticVideoCells），這些元素我們自己建立時就已經知道正確的參與者 id/名字，
        //    優先信任這批、再補上畫面上原生找得到的 <video> 元素，兩邊一樣用軌道 id 去重避免重複。
        const seenKeys = new Set();
        const cells = []; // { type: 'video', el, label, participantId? } | { type: 'placeholder', label }

        syntheticVideoCells.forEach(sc => {
            if (!sc.el || sc.el.videoWidth <= 0) return; // 軌道還沒真的有畫面（例如剛連上），這輪先跳過
            const dedupeKey = 'track:' + sc.trackId;
            if (seenKeys.has(dedupeKey)) return;
            seenKeys.add(dedupeKey);
            const label = sc.label || nameMap[sc.participantId] || '';
            cells.push({ type: 'video', el: sc.el, label, participantId: sc.participantId });
        });

        const rawVideos = Array.from(document.querySelectorAll('video')).filter(v => v.videoWidth > 0);
        rawVideos.forEach(v => {
            let dedupeKey = null;
            try {
                const stream = v.srcObject;
                if (stream && typeof stream.getVideoTracks === 'function') {
                    const t = stream.getVideoTracks()[0];
                    if (t) dedupeKey = 'track:' + t.id;
                }
            } catch (e) {}
            if (!dedupeKey) {
                // 拿不到底層軌道 id 的極少數情況，退回用這個 video 元素自己的身份當 key，
                // 至少同一輪畫格不會被同一個元素重複疊加
                if (!v.__aiRecorderCellKey) v.__aiRecorderCellKey = 'el:' + Math.random().toString(36).slice(2);
                dedupeKey = v.__aiRecorderCellKey;
            }
            if (seenKeys.has(dedupeKey)) return; // 已經被上面的 syntheticVideoCells 畫過同一條軌道了
            seenKeys.add(dedupeKey);

            const label = findLabelForVideo(v, nameMap);
            let participantId = null;
            try {
                let idHolder = v, pid = extractParticipantId(idHolder), hops = 0;
                while (!pid && idHolder && idHolder.parentElement && hops < 5) {
                    idHolder = idHolder.parentElement; pid = extractParticipantId(idHolder); hops++;
                }
                participantId = pid;
            } catch (e) {}

            cells.push({ type: 'video', el: v, label, participantId });
        });

        const coveredIds = new Set(cells.map(c => c.participantId).filter(Boolean));
        Object.keys(nameMap).forEach(pid => {
            if (coveredIds.has(pid)) return;
            const name = nameMap[pid];
            if (!name) return;
            cells.push({ type: 'placeholder', label: name });
        });

        if (cells.length > 0) {
            const cols = Math.ceil(Math.sqrt(cells.length));
            const rows = Math.ceil(cells.length / cols);
            const cellW = canvas.width / cols, cellH = canvas.height / rows;
            cells.forEach((cell, i) => {
                const x = (i % cols) * cellW, y = Math.floor(i / cols) * cellH;

                if (cell.type === 'video') {
                    try { ctx.drawImage(cell.el, x, y, cellW, cellH); } catch (e) {}
                } else {
                    // 佔位格：沒開鏡頭的人，畫灰底 + 提示文字，不留白、也不讓這個人整個消失
                    ctx.fillStyle = '#2a2a2a';
                    ctx.fillRect(x, y, cellW, cellH);
                    ctx.fillStyle = '#888';
                    ctx.font = 'bold 14px sans-serif';
                    ctx.textAlign = 'center';
                    ctx.fillText('（未開啟鏡頭）', x + cellW / 2, y + cellH / 2);
                    ctx.textAlign = 'left';
                }

                // 這樣錄下來的檔案才看得出來哪一格是誰（不然每格都只是一個畫面，看不出角色）。
                // 兩種格子（有畫面/佔位格）都要畫名字標籤，佔位格才不會只有「未開啟鏡頭」幾個字看不出是誰。
                try {
                    const label = cell.label;
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
        if (window.__trackPollInterval) { try { clearInterval(window.__trackPollInterval); } catch (e) {} }

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