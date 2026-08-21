using Microsoft.Playwright;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
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

        // 🐛 這輪新增（AI 面試官虛擬人畫面/嘴型，接 Simli）：
        //    ⚠️ 這整段是這個專案目前風險最高、最需要實機除錯的一段程式碼——涉及 Simli 的 JS SDK 載入方式、
        //    事件名稱、串流綁定方式，以及攔截 getUserMedia 的時機，全部都無法在這個環境離線驗證，
        //    只能依照 Simli 官方文件跟現有程式的架構風格盡力寫，實際部署後很可能需要對照瀏覽器 console
        //    的錯誤訊息再調整。
        //
        //    🐛 這輪重大修正：simli-client 的正式版本是 3.0.2，內部已經改用 livekit-client 重寫，
        //    連線方式（要先換 session token）、建構子參數、事件名稱都跟舊版文件不一樣，
        //    上一輪照舊版文件寫的完全對不上，這裡已經整個對照官方最新文件重寫。
        //
        //    做法：
        //    1. 用動態 import() 從 esm.sh 載入 simli-client（不管原始套件是不是 ESM 格式，都能在瀏覽器直接 import）
        //    2. 跟 Simli 換一個 session token，建立 SimliClient（livekit 傳輸模式），
        //       把連線後拿到的視訊/音訊串流，包成一個假的 MediaStream
        //    3. 攔截 navigator.mediaDevices.getUserMedia，Jitsi 跟它要攝影機/麥克風時，
        //       就把 Simli 的即時串流冒充成「攝影機」還給它，取代原本 Chromium 的假攝影機檔案
        //    4. 提供 window.__simliSpeak(base64Pcm24k)，之後由 C# 呼叫，把 Gemini TTS 產生的
        //       24000Hz PCM16 音訊，在瀏覽器端降取樣成 Simli 要的 16000Hz 再送進去
        //
        //    這段一定要在 Jitsi 自己的程式碼開始跑「之前」就佈署好，所以用 Playwright 的
        //    AddInitScriptAsync（在 GotoAsync 導航之前呼叫），保證每次載入新文件時最先執行，
        //    搶在 Jitsi 呼叫 getUserMedia 之前把攔截裝好。
        private const string SimliInitScript = @"
(function () {
    window.__simliReady = null;   // 會變成一個 resolve 出 MediaStream 的 Promise
    window.__simliClient = null;
    window.__simliSpeak = null;   // function(base64Pcm24k) -> 把音訊送進 Simli 講出來

    async function initSimli() {
        try {
            // 🐛 這輪修正：問題不是 CDN 載入方式，是 API 版本整個換了。
            //    simli-client 現在的正式版本是 3.0.2，內部改用 livekit-client 重寫，
            //    連建立連線的方式都完全不一樣（要先跟 Simli 換一個 session token，
            //    再用不同的建構子參數），這是照官方最新文件重新對過的版本。
            const mod = await import(window.__SIMLI_BUNDLE_URL__);
            const SimliClient = mod.SimliClient;
            const LogLevel = mod.LogLevel;
            const generateSimliSessionToken = mod.generateSimliSessionToken;
            if (!SimliClient || !generateSimliSessionToken) throw new Error('simli-client 模組載入了，但找不到需要的匯出（SimliClient / generateSimliSessionToken）');

            const videoEl = document.createElement('video');
            videoEl.autoplay = true; videoEl.playsInline = true; videoEl.muted = true;
            videoEl.style.position = 'fixed'; videoEl.style.left = '-9999px'; videoEl.style.top = '-9999px';
            const audioEl = document.createElement('audio');
            audioEl.autoplay = true;
            audioEl.style.position = 'fixed'; audioEl.style.left = '-9999px'; audioEl.style.top = '-9999px';
            document.body.appendChild(videoEl);
            document.body.appendChild(audioEl);

            console.log('[Simli] 正在跟 Simli 換取 session token...');
            const tokenResp = await generateSimliSessionToken({
                apiKey: window.__SIMLI_API_KEY__,
                config: {
                    faceId: window.__SIMLI_FACE_ID__,
                    handleSilence: true,
                    maxSessionLength: 3600,
                    maxIdleTime: 300
                }
            });
            const sessionToken = tokenResp && tokenResp.session_token;
            if (!sessionToken) throw new Error('拿不到 session_token，回應內容：' + JSON.stringify(tokenResp));
            console.log('[Simli] 已取得 session token');

            // 用 livekit 傳輸模式（Simli 官方建議，對防火牆較嚴格的網路環境相容性比 p2p 模式好，
            // 而且不需要另外準備 ICE servers）
            const client = new SimliClient(
                sessionToken,
                videoEl,
                audioEl,
                null,
                (LogLevel && LogLevel.DEBUG) || 'debug',
                'livekit'
            );
            window.__simliClient = client;

            window.__simliReady = new Promise((resolve, reject) => {
                client.on('start', () => {
                    console.log('[Simli] WebRTC 已連線（start 事件），等待畫面/聲音串流就緒...');
                    // 連上之後，Simli 會把畫面/聲音接到我們給的 video/audio 元素的 srcObject 上，
                    // 稍等一下讓 srcObject 真的被賦值，再從這兩個元素身上把 MediaStream 撈出來組成一個假串流
                    setTimeout(() => {
                        try {
                            const vStream = videoEl.srcObject;
                            const aStream = audioEl.srcObject;
                            const tracks = [];
                            if (vStream) tracks.push(...vStream.getVideoTracks());
                            if (aStream) tracks.push(...aStream.getAudioTracks());
                            else if (vStream) tracks.push(...vStream.getAudioTracks());
                            if (tracks.length === 0) { reject(new Error('Simli 已連線但抓不到 MediaStream track')); return; }
                            console.log('[Simli] 成功組出虛擬人 MediaStream，共 ' + tracks.length + ' 條軌道');
                            resolve(new MediaStream(tracks));
                        } catch (e) { reject(e); }
                    }, 800);
                });
                client.on('error', (e) => reject(new Error('SimliClient 發生錯誤：' + e)));
                client.on('startup_error', (msg) => reject(new Error('SimliClient 啟動失敗（常見原因：Face ID 無效、或 Simli 額度用完）：' + msg)));
            });
            // 🐛 window.__simliReady 如果最後是 rejected 狀態、卻沒有任何人「立刻」去 .catch() 它，
            //    瀏覽器會在下一個 microtask 就判定成「未處理的 Promise rejection」，噴出不必要的雜訊 log。
            //    這裡加一個空的 .catch() 把這個「已經處理過了」的訊號送出去，不影響下面 getUserMedia 覆寫那邊
            //    之後照樣能 await/catch 到同一個 Promise 的真正結果（同一個 Promise 可以被多個地方分別
            //    await/catch，互不影響）。
            window.__simliReady.catch(() => {});

            await client.start();

            window.__simliSpeak = async function (base64Pcm24k) {
                try {
                    const raw = atob(base64Pcm24k);
                    const bytes = new Uint8Array(raw.length);
                    for (let i = 0; i < raw.length; i++) bytes[i] = raw.charCodeAt(i);
                    const int16 = new Int16Array(bytes.buffer, bytes.byteOffset, Math.floor(bytes.byteLength / 2));
                    const float32 = new Float32Array(int16.length);
                    for (let i = 0; i < int16.length; i++) float32[i] = int16[i] / 32768;

                    // Gemini TTS 出來的音訊是 24000Hz，Simli 要 16000Hz，這裡用離線 AudioContext 做降取樣
                    const offlineCtx = new OfflineAudioContext(1, Math.ceil(float32.length * 16000 / 24000), 16000);
                    const srcBuffer = offlineCtx.createBuffer(1, float32.length, 24000);
                    srcBuffer.copyToChannel(float32, 0);
                    const src = offlineCtx.createBufferSource();
                    src.buffer = srcBuffer;
                    src.connect(offlineCtx.destination);
                    src.start();
                    const rendered = await offlineCtx.startRendering();
                    const resampled = rendered.getChannelData(0);

                    const outInt16 = new Int16Array(resampled.length);
                    for (let i = 0; i < resampled.length; i++) {
                        const s = Math.max(-1, Math.min(1, resampled[i]));
                        outInt16[i] = s < 0 ? s * 32768 : s * 32767;
                    }
                    const outBytes = new Uint8Array(outInt16.buffer);
                    if (window.__simliClient) window.__simliClient.sendAudioData(outBytes);
                } catch (e) { console.error('[Simli] speak 失敗：', e); }
            };
        } catch (e) {
            console.error('[Simli] 初始化失敗：', e);
            window.__simliReady = Promise.reject(e);
            window.__simliReady.catch(() => {}); // 同上，避免噴出不必要的「未處理 rejection」雜訊
        }
    }
    initSimli();

    // 🎯 攔截 getUserMedia：Jitsi 進會議時會呼叫這個要攝影機/麥克風，
    //    把 Simli 產生的即時串流冒充成「攝影機畫面」還給它，取代原本 Chromium 的假攝影機檔案。
    //    如果 Simli 連線還沒好、或整個失敗，就退回原本的假攝影機檔案（不要讓 AI 面試官完全連不進會議）。
    try {
        const originalGetUserMedia = navigator.mediaDevices.getUserMedia.bind(navigator.mediaDevices);
        navigator.mediaDevices.getUserMedia = async function (constraints) {
            console.log('[Simli] getUserMedia 被呼叫，constraints=' + JSON.stringify(constraints));
            try {
                if (window.__simliReady) {
                    const simliStream = await window.__simliReady;
                    if (simliStream) {
                        console.log('[Simli] 回傳虛擬人串流給 getUserMedia，視訊軌道數=' + simliStream.getVideoTracks().length + '，音訊軌道數=' + simliStream.getAudioTracks().length);
                        return simliStream;
                    }
                }
            } catch (e) {
                console.error('[Simli] 拿不到虛擬人串流，退回原本的假攝影機檔案：', e);
            }
            const fallbackStream = await originalGetUserMedia(constraints);
            console.log('[Simli] 退回原生假攝影機，視訊軌道數=' + fallbackStream.getVideoTracks().length + '，音訊軌道數=' + fallbackStream.getAudioTracks().length);
            return fallbackStream;
        };
    } catch (e) { console.error('[Simli] 攔截 getUserMedia 失敗：', e); }
})();
";

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
        private readonly IConfiguration _config;
        private readonly IServiceScopeFactory _scopeFactory;

        // 🎯 這個服務是 Singleton（一個程序裡只有一份，見 Program.cs），但 GeminiService 是 Scoped，
        //    不能直接建構子注入（會是所謂的 captive dependency，DI 容器啟動時會直接噴錯）——
        //    改成注入 IServiceScopeFactory，要用的時候自己開一個新的 scope 去要 GeminiService，
        //    跟 MeetingHub 背景工作那邊處理 AppDbContext 的做法是同一套模式
        public JitsiBotService(JaasJwtService jaasJwt, IConfiguration config, IServiceScopeFactory scopeFactory)
        {
            _jaasJwt = jaasJwt;
            _config = config;
            _scopeFactory = scopeFactory;
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
                        "--no-zygote",
                        // 🐛 這輪新增：修正 Simli 資源載入被擋的問題。
                        //    Chrome 的 Private Network Access（私有網路存取）機制會擋掉「公開網站
                        //    （https://8x8.vc）要跟私有位址（本機測試時是 http://localhost:5216）要東西」
                        //    這種請求，導致 AI 面試官的頁面連不到自己伺服器上的 simli-client.bundle.js。
                        //    這個瀏覽器是我們自己完全控制的專用瀏覽器（不是給真人瀏覽的一般瀏覽器），
                        //    關掉這個檢查沒有資安疑慮。
                        //    ⚠️ 這個問題本質上只會在「本機測試」時發生（App:BaseUrl 是 localhost 這種私有位址）；
                        //    部署到 Render 之後 App:BaseUrl 會是公開網址，理論上不會再踩到這個限制，
                        //    但保留這個參數不影響正式環境運作，所以兩邊都留著。
                        "--disable-features=BlockInsecurePrivateNetworkRequests,PrivateNetworkAccessSendPreflights,PrivateNetworkAccessRespectPreflightResults"
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

                // 🐛 這輪新增：這個瀏覽器是無頭（headless）在伺服器上跑的，沒有人能打開開發者工具看 console，
                //    之前完全沒有轉發任何瀏覽器端的 log/錯誤到我們看得到的地方，等於在「盲測」——
                //    尤其這輪剛接的 Simli 虛擬人牽涉一大段全新的 JS（動態載入 SDK、WebRTC 連線、
                //    攔截 getUserMedia），任何一步出錯我們原本完全看不到。
                //    把頁面的 console 訊息（含 [Simli] 開頭的、還有一般 JS 錯誤）全部轉發到伺服器自己的
                //    Console.WriteLine，這樣下次測試只要看伺服器的 log 就能看到瀏覽器端實際發生了什麼。
                page.Console += (_, msg) =>
                {
                    try
                    {
                        Console.WriteLine($"[JitsiBot Console:{roomCode}][{msg.Type}] {msg.Text}");
                    }
                    catch { /* log 轉發本身失敗就算了，不能讓這個影響到主流程 */ }
                };
                page.PageError += (_, err) =>
                {
                    Console.WriteLine($"[JitsiBot PageError:{roomCode}] {err}");
                };

                // 🎯 AI 面試官虛擬人（Simli）：這段一定要在 GotoAsync 導航「之前」用 AddInitScriptAsync 佈署，
                //    才能保證搶在 Jitsi 自己呼叫 getUserMedia 之前，把攔截裝好。
                //    如果沒設定 Simli:ApiKey / Simli:FaceId（例如還在測試、還沒申請），就跳過這段，
                //    AI 面試官會照舊用原本的靜態 .y4m 假攝影機檔案，不影響其他功能。
                //
                //    🐛 這輪修正：simli-client 這個套件不再從任何第三方 CDN 動態載入（jsDelivr、esm.sh
                //    都會因為套件本身的大小寫 bug 在 Linux 環境打包失敗），改成用本地 esbuild 預先打包好、
                //    修過那個 bug 的版本，放在自己的 wwwroot/js/simli-client.bundle.js，
                //    AI 面試官的頁面直接跟自己的伺服器要這個檔案。
                //    App:BaseUrl 沒設定的話預設用 http://localhost:5216（本機開發最常見的預設值），
                //    如果部署到 Render 等正式環境，記得在 appsettings.json 補上正確的公開網址。
                var simliApiKey = _config["Simli:ApiKey"];
                var simliFaceId = _config["Simli:FaceId"];
                if (!string.IsNullOrEmpty(simliApiKey) && !string.IsNullOrEmpty(simliFaceId))
                {
                    try
                    {
                        var appBaseUrl = (_config["App:BaseUrl"] ?? "http://localhost:5216").TrimEnd('/');
                        var simliBundleUrl = $"{appBaseUrl}/js/simli-client.bundle.js";
                        var simliConfigScript =
                            $"window.__SIMLI_API_KEY__ = {JsonSerializer.Serialize(simliApiKey)};\n" +
                            $"window.__SIMLI_FACE_ID__ = {JsonSerializer.Serialize(simliFaceId)};\n" +
                            $"window.__SIMLI_BUNDLE_URL__ = {JsonSerializer.Serialize(simliBundleUrl)};";
                        await page.AddInitScriptAsync(simliConfigScript);
                        await page.AddInitScriptAsync(SimliInitScript);
                        Console.WriteLine($"[JitsiBot] 房間 {roomCode} 已佈署 Simli 虛擬人初始化腳本（套件檔案來源：{simliBundleUrl}）。");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[JitsiBot] 房間 {roomCode} 佈署 Simli 腳本失敗，AI 面試官將退回使用靜態假攝影機檔案：{ex.Message}");
                    }
                }
                else
                {
                    Console.WriteLine($"[JitsiBot] 房間 {roomCode} 沒有設定 Simli:ApiKey / Simli:FaceId，AI 面試官使用原本的靜態假攝影機檔案。");
                }

                // 🐛 這輪修正：這裡原本也是寫死一組 JaaS AppID，跟 Join.cshtml 犯了同一個問題——
                //    不管 appsettings.json 換成什麼新帳號，AI 面試官實際連過去的還是這組寫死的舊帳號，
                //    導致「換新 JaaS 帳號，額度還是顯示用完」，因為根本沒真的連到新帳號。
                //    改成統一從設定檔讀（跟 _jaasJwt 簽發 JWT 時讀的是同一個設定值），兩邊才會一致。
                string jitsiDomain = "https://8x8.vc";
                string tenantId = _config["JaaS:AppId"]
                    ?? throw new InvalidOperationException("appsettings.json 缺少 JaaS:AppId 設定");

                // 🎯 JaaS 需要 JWT 才能真的加入會議，AI 面試官不是主持人，moderator=false
                string botJwt = _jaasJwt.GenerateToken(roomCode, "ai-interviewer", "AI 面試官", isModerator: false);

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

        // 🐛 這輪新增：讓 AI 面試官「真的開口說話」——把文字轉成語音（Gemini TTS），
        //    再送進這個房間裡已經連好的 Simli 虛擬人，讓畫面嘴型跟著動。
        //    這是給「冷場提問」那條邏輯呼叫的（偵測到冷場、Gemini 生成一句問句之後，改叫這個方法，
        //    而不是像以前那樣只在某個工作人員自己的瀏覽器裡用 SpeechSynthesis 講給自己聽）。
        //    ⚠️ 沒接 Simli（沒設定 ApiKey/FaceId）或 Simli 連線失敗時，這裡就只是安靜地不做事，
        //    不會讓面試流程掛掉——冷場提問的文字內容還是會透過原本的邏輯顯示在畫面上給大家看。
        public async Task SpeakAsync(string roomCode, string text)
        {
            if (string.IsNullOrEmpty(roomCode) || string.IsNullOrWhiteSpace(text)) return;
            roomCode = roomCode.Trim();

            if (!_activeBots.TryGetValue(roomCode, out var instance))
            {
                Console.WriteLine($"[JitsiBot] SpeakAsync：房間 {roomCode} 沒有正在運作的 AI 面試官，略過。");
                return;
            }

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var gemini = scope.ServiceProvider.GetRequiredService<GeminiService>();
                var audioBytes = await gemini.SynthesizeSpeechAsync(text);
                if (audioBytes == null || audioBytes.Length == 0)
                {
                    Console.WriteLine($"[JitsiBot] SpeakAsync：房間 {roomCode} 的語音合成失敗或沒有內容，略過。");
                    return;
                }

                var base64Audio = Convert.ToBase64String(audioBytes);
                var speakScript = "(async () => { if (window.__simliSpeak) { await window.__simliSpeak(" +
                                   JsonSerializer.Serialize(base64Audio) +
                                   "); } else { console.error('[Simli] __simliSpeak 尚未就緒'); } })();";

                await instance.PageInstance.EvaluateAsync(speakScript);
                Console.WriteLine($"[JitsiBot] 房間 {roomCode} 的 AI 面試官已送出語音：「{text}」");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[JitsiBot] SpeakAsync 失敗（房間 {roomCode}）：{ex.Message}");
            }
        }
    }
}