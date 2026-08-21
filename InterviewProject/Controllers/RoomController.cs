using Microsoft.AspNetCore.Mvc;
using InterviewProject.Data;
using InterviewProject.Models;
using InterviewProject.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace InterviewProject.Controllers
{
    public class RoomController : Controller
    {
        private readonly AppDbContext _context;
        private readonly JitsiBotService _botService;
        private readonly IWebHostEnvironment _env;
        private readonly R2StorageService _storage;
        private readonly GeminiService _gemini;

        public RoomController(AppDbContext context, JitsiBotService botService, IWebHostEnvironment env, R2StorageService storage, GeminiService gemini)
        {
            _context = context;
            _botService = botService;
            _env = env;
            _storage = storage;
            _gemini = gemini;
        }

        private bool IsEmployee()
        {
            var role = HttpContext.Session.GetString("MemberRole")?.ToLower();
            return role == "hr" || role == "manager" || role == "director";
        }

        // 🎯 修正：將同步改成非同步，提升高並發時的效能
        public async Task<IActionResult> Index()
        {
            if (!IsEmployee()) return RedirectToAction("Index", "Login");

            var rooms = await _context.Rooms.OrderByDescending(r => r.CreatedTime).ToListAsync();
            return View(rooms);
        }

        [HttpGet]
        public IActionResult Create()
        {
            if (!IsEmployee()) return RedirectToAction("Index", "Login");
            return View();
        }

        [HttpPost]
        public async Task<IActionResult> Create(string roomName, DateTime? startAt, DateTime? endAt,
                                                int maxParticipants = 20, string? description = null)
        {
            if (!IsEmployee()) return RedirectToAction("Index", "Login");
            if (string.IsNullOrWhiteSpace(roomName)) { ModelState.AddModelError("", "房間名稱不能為空"); return View(); }

            // 🎯 防呆機制：防止排程時間前後顛倒
            if (startAt.HasValue && endAt.HasValue && endAt.Value <= startAt.Value)
            {
                ModelState.AddModelError("", "面試結束時間必須晚於開始時間！");
                return View();
            }

            var room = new Room
            {
                RoomName = roomName,
                CreatedTime = DateTime.Now,
                JitsiRoomName = Guid.NewGuid().ToString("N")[..10],
                StartAt = startAt,
                EndAt = endAt,
                IsActive = true
            };

            _context.Rooms.Add(room);
            await _context.SaveChangesAsync();
            TempData["Success"] = $"房間「{roomName}」已建立，代碼：{room.JitsiRoomName}";
            return RedirectToAction("Index");
        }

        [HttpGet]
        public async Task<IActionResult> Edit(int id)
        {
            if (!IsEmployee()) return RedirectToAction("Index", "Login");
            var room = await _context.Rooms.FindAsync(id);
            if (room == null) return NotFound();
            return View(room);
        }

        [HttpPost]
        public async Task<IActionResult> Edit(int id, string roomName, DateTime? startAt, DateTime? endAt,
                                             int maxParticipants, bool isActive, string? description)
        {
            if (!IsEmployee()) return RedirectToAction("Index", "Login");

            if (string.IsNullOrWhiteSpace(roomName)) { ModelState.AddModelError("", "房間名稱不能為空"); return View(); }

            // 🎯 防呆機制：編輯時也要防止時間顛倒
            if (startAt.HasValue && endAt.HasValue && endAt.Value <= startAt.Value)
            {
                ModelState.AddModelError("", "面試結束時間必須晚於開始時間！");
                var currentRoom = await _context.Rooms.FindAsync(id);
                return View(currentRoom);
            }

            var room = await _context.Rooms.FindAsync(id);
            if (room == null) return NotFound();

            room.RoomName = roomName;
            room.StartAt = startAt;
            room.EndAt = endAt;
            room.IsActive = isActive;

            await _context.SaveChangesAsync();
            TempData["Success"] = "房間設定已更新";
            return RedirectToAction("Index");
        }

        [HttpGet]
        public IActionResult EnterCode() => View();

        // 🎯 修正：輸入房間代碼也改為非同步查詢，避免點擊加入時網頁卡死
        [HttpPost]
        public async Task<IActionResult> EnterCode(string roomCode)
        {
            if (string.IsNullOrWhiteSpace(roomCode)) { ViewBag.ErrorMessage = "請輸入房間代碼"; return View(); }

            // 🎯 Step C：必須先登入（求職者或員工皆可），才能查代碼
            var sessionMemberId = HttpContext.Session.GetInt32("MemberId");
            var sessionRole = HttpContext.Session.GetString("MemberRole")?.ToLower();
            if (sessionMemberId == null || string.IsNullOrEmpty(sessionRole))
            {
                return RedirectToAction("Index", "Login");
            }

            var room = await _context.Rooms.FirstOrDefaultAsync(x => x.JitsiRoomName == roomCode.Trim());
            if (room == null) { ViewBag.ErrorMessage = "找不到此房間代碼"; return View(); }

            if (!room.CanEnter())
            {
                ViewBag.Room = room;
                ViewBag.StatusText = room.StatusText();
                return View("RoomNotAvailable");
            }

            // 🎯 Step C：白名單檢查——只有 RoomParticipants 裡受邀的人才能進
            //    （沒有受邀名單的房間，代表是舊有/手動建立的一般房間，維持原本「登入即可進」的行為）
            var hasParticipantList = await _context.RoomParticipants.AnyAsync(p => p.RoomId == room.Id);
            if (hasParticipantList)
            {
                var participant = await FindParticipantAsync(room, sessionMemberId.Value, sessionRole);
                if (participant == null)
                {
                    ViewBag.ErrorMessage = "您不是這場會議受邀的人員，無法進入";
                    return View();
                }

                // 🎯 求職者一定要先完成適性測驗，才能真的進入會議室（就算已經受邀、知道代碼也一樣）
                //    改成直接導回應徵管理頁、自動彈出測驗彈窗，不管是點「查看」按鈕還是這裡手動輸入代碼進來都一樣
                if (participant.Role == ParticipantRole.Jobseeker)
                {
                    var hasTest = await _context.AptitudeTestResults.AnyAsync(t => t.ResumeId == participant.ResumeId);
                    if (!hasTest)
                    {
                        return Redirect($"/Member/Application?openTest={participant.ResumeId}");
                    }
                }
            }

            return RedirectToAction("Join", new { code = room.JitsiRoomName });
        }

        // 🎯 修正：加入安全性阻擋與非同步優化
        // 🎯 保底輪詢用：不管 SignalR 廣播有沒有送達，前端每隔幾秒問一次「會議現在到底是什麼狀態」，
        //    避免長時間閒置等待時 SignalR 群組悄悄失效，導致畫面永遠卡在等待畫面
        [HttpGet]
        public async Task<IActionResult> GetMeetingStatus(string code)
        {
            var sessionMemberId = HttpContext.Session.GetInt32("MemberId");
            if (sessionMemberId == null) return Json(new { meetingStatus = "" });

            var room = await _context.Rooms.AsNoTracking().FirstOrDefaultAsync(x => x.JitsiRoomName == code);
            if (room == null) return Json(new { meetingStatus = "" });

            return Json(new { meetingStatus = room.MeetingStatus });
        }

        public async Task<IActionResult> Join(string code)
        {
            // 安全限制：至少必須是登入的使用者（求職者或員工皆可）才可以進去
            var sessionMemberId = HttpContext.Session.GetInt32("MemberId");
            var sessionRole = HttpContext.Session.GetString("MemberRole")?.ToLower();
            if (sessionMemberId == null || string.IsNullOrEmpty(sessionRole))
                return RedirectToAction("Index", "Login");

            var room = await _context.Rooms.FirstOrDefaultAsync(x => x.JitsiRoomName == code);
            if (room == null) return Content("房間不存在");

            if (!room.CanEnter())
            {
                ViewBag.Room = room;
                ViewBag.StatusText = room.StatusText();
                return View("RoomNotAvailable");
            }

            // 🎯 Step C：白名單檢查，並記錄受邀者的進場狀態
            var hasParticipantList = await _context.RoomParticipants.AnyAsync(p => p.RoomId == room.Id);
            if (hasParticipantList)
            {
                var participant = await FindParticipantAsync(room, sessionMemberId.Value, sessionRole);
                if (participant == null)
                {
                    ViewBag.Room = room;
                    ViewBag.ErrorMessage = "您不是這場會議受邀的人員，無法進入";
                    return View("RoomNotAvailable");
                }

                // 🎯 先記住這次 request 進來之前，DB 裡原本的 JoinedAt/LeftAt，
                //    候審室判斷要用「原本」的值，不能用下面（等真的放行才會設定的）新值
                var originalJoinedAt = participant.JoinedAt;
                var originalLeftAt = participant.LeftAt;

                // 🎯 求職者一定要先完成適性測驗，才能真的進入會議室（就算已經受邀、知道代碼也一樣）
                //    改成直接導回應徵管理頁、自動彈出測驗彈窗，而不是顯示一個死路錯誤畫面
                //    （不管是點「查看」按鈕進來、還是直接在網址列打會議代碼手動進來，都會一樣被擋在這裡）
                if (participant.Role == ParticipantRole.Jobseeker)
                {
                    var hasTest = await _context.AptitudeTestResults.AnyAsync(t => t.ResumeId == participant.ResumeId);
                    if (!hasTest)
                    {
                        return Redirect($"/Member/Application?openTest={participant.ResumeId}");
                    }
                }

                // 🎯 候審室機制（原始需求 6）：只要會議「已經開始」，非主持人每一次「重新需要授權」的進場都要候審——
                //    包含這場會議第一次打開連結、以及中途退出（LeftAt 較新）之後想再進來。
                //    但「剛被最高主管同意、Lobby 頁面自動重新整理進來」這次不能再擋一次，
                //    不然核准後永遠卡在候審室，變成無限迴圈——用「原本」的 JoinedAt 跟 LeftAt 誰比較新來分辨：
                //      JoinedAt 比較新（或還沒 LeftAt）＝ 目前處於「已核准、正要進場」的狀態，放行
                //      LeftAt 比較新（或從沒被核准過）＝ 需要重新候審
                //    唯一不受影響的情況：會議開始「當下」本來就在等待畫面、跟主持人同步進場的人——
                //    那批人的 Join() 是在 MeetingStatus 還是 NotStarted 的時候就執行過了，不會走到這裡。
                //    🎯 最高主管本人一律排除在候審機制之外——他是核准別人的人，不能把自己也卡住（不然沒人能核准他）
                var currentlyAuthorized = originalJoinedAt.HasValue
                    && (!originalLeftAt.HasValue || originalJoinedAt > originalLeftAt);

                if (room.MeetingStatus == "InProgress" && participant.Role != ParticipantRole.Director && !currentlyAuthorized)
                {
                    // 🐛 修正：這個分支下面的 SaveChangesAsync 只能存 Status=Pending，
                    //    絕對不能連 JoinedAt 都一起存進去——不然候審者的 JoinedAt 會被污染成「有值」，
                    //    下次如果被拒絕（Denied）後又重試，會被誤判成「已經核准過」而直接跳過候審室
                    participant.Status = ParticipantStatus.Pending;
                    await _context.SaveChangesAsync();

                    ViewBag.Room = room;
                    ViewBag.ParticipantId = participant.Id;
                    return View("Lobby");
                }

                // 🎯 注意：這裡只做資格檢查，不寫入資料庫。
                //    Status=Admitted、JoinedAt 要等使用者在 Jitsi 畫面真的按下「加入會議」才算數，
                //    由前端 videoConferenceJoined 事件呼叫 /Room/MarkJoined 來記錄（見下方 MarkJoined action）。
                participant.Status = ParticipantStatus.Admitted;
                participant.JoinedAt = DateTime.Now;

                ViewBag.ParticipantRole = participant.Role;
            }

            ViewBag.Room = room;
            return View();
        }

        // 🎯 候審室機制：卡在候審畫面的人，每隔幾秒問一次「我被同意了嗎」
        [HttpGet]
        public async Task<IActionResult> GetMyAdmissionStatus(string code)
        {
            var sessionMemberId = HttpContext.Session.GetInt32("MemberId");
            var sessionRole = HttpContext.Session.GetString("MemberRole")?.ToLower();
            if (sessionMemberId == null || string.IsNullOrEmpty(sessionRole))
                return Json(new { status = "" });

            var room = await _context.Rooms.AsNoTracking().FirstOrDefaultAsync(x => x.JitsiRoomName == code);
            if (room == null) return Json(new { status = "" });

            var participant = await FindParticipantAsync(room, sessionMemberId.Value, sessionRole);
            if (participant == null) return Json(new { status = "" });

            return Json(new { status = participant.Status });
        }

        // 🎯 候審室機制：最高主管專用，列出這場會議目前候審中（Pending）的人，主持人畫面靠這個每隔幾秒刷新名單
        [HttpGet]
        public async Task<IActionResult> GetPendingParticipants(string code)
        {
            var sessionRole = HttpContext.Session.GetString("MemberRole")?.ToLower();
            if (sessionRole != "director") return Json(new List<object>());

            var room = await _context.Rooms.AsNoTracking().FirstOrDefaultAsync(x => x.JitsiRoomName == code);
            if (room == null) return Json(new List<object>());

            var pending = await _context.RoomParticipants
                .Where(p => p.RoomId == room.Id && p.Status == ParticipantStatus.Pending)
                .Include(p => p.Resume).ThenInclude(r => r!.Member)
                .Include(p => p.Employee)
                .ToListAsync();

            var result = pending.Select(p => new
            {
                id = p.Id,
                name = p.Role == ParticipantRole.Jobseeker
                    ? (p.Resume?.Member?.Name ?? "求職者")
                    : (p.Employee?.Name ?? "員工"),
                role = p.Role
            });

            return Json(result);
        }

        // 🎯 候審室機制：最高主管按下「同意」或「拒絕」
        [HttpPost]
        public async Task<IActionResult> AdmitParticipant(int participantId, bool approve)
        {
            var sessionMemberId = HttpContext.Session.GetInt32("MemberId");
            var sessionRole = HttpContext.Session.GetString("MemberRole")?.ToLower();
            if (sessionMemberId == null || sessionRole != "director")
                return Json(new { success = false, message = "只有最高主管能同意或拒絕候審申請" });

            var participant = await _context.RoomParticipants.FirstOrDefaultAsync(p => p.Id == participantId);
            if (participant == null) return Json(new { success = false, message = "找不到這位候審中的人" });

            // 🎯 一定要確認這個最高主管真的是「這位候審者所在房間」受邀的主持人，不能跨房間操作別人的候審名單
            var isDirectorOfThisRoom = await _context.RoomParticipants.AnyAsync(p =>
                p.RoomId == participant.RoomId
                && p.Role == ParticipantRole.Director
                && p.EmployeeId == sessionMemberId.Value);
            if (!isDirectorOfThisRoom)
                return Json(new { success = false, message = "你不是這場面試的主持人" });

            participant.Status = approve ? ParticipantStatus.Admitted : ParticipantStatus.Denied;
            if (approve) participant.JoinedAt = DateTime.Now;

            await _context.SaveChangesAsync();

            return Json(new { success = true });
        }

        // 🎯 只有前端在 Jitsi 真的觸發 videoConferenceJoined（使用者按下「加入會議」）時才會呼叫這裡，
        //    這樣 RoomParticipants.JoinedAt 記錄的才是「真的進了視訊會議」的時間，不是「打開這個頁面」的時間。
        [HttpPost]
        public async Task<IActionResult> MarkJoined(string code)
        {
            var sessionMemberId = HttpContext.Session.GetInt32("MemberId");
            var sessionRole = HttpContext.Session.GetString("MemberRole")?.ToLower();
            if (sessionMemberId == null || string.IsNullOrEmpty(sessionRole))
                return Json(new { success = false });

            var room = await _context.Rooms.FirstOrDefaultAsync(x => x.JitsiRoomName == code);
            if (room == null) return Json(new { success = false });

            var participant = await FindParticipantAsync(room, sessionMemberId.Value, sessionRole);
            if (participant == null) return Json(new { success = false });

            participant.Status = ParticipantStatus.Admitted;
            participant.JoinedAt = DateTime.Now;
            await _context.SaveChangesAsync();

            return Json(new { success = true });
        }

        // 🎯 對應前端 videoConferenceLeft 事件（不管是自己按掛斷、還是被斷線），記錄離開時間
        [HttpPost]
        public async Task<IActionResult> MarkLeft(string code)
        {
            var sessionMemberId = HttpContext.Session.GetInt32("MemberId");
            var sessionRole = HttpContext.Session.GetString("MemberRole")?.ToLower();
            if (sessionMemberId == null || string.IsNullOrEmpty(sessionRole))
                return Json(new { success = false });

            var room = await _context.Rooms.FirstOrDefaultAsync(x => x.JitsiRoomName == code);
            if (room == null) return Json(new { success = false });

            var participant = await FindParticipantAsync(room, sessionMemberId.Value, sessionRole);
            if (participant == null) return Json(new { success = false });

            participant.LeftAt = DateTime.Now;
            await _context.SaveChangesAsync();

            return Json(new { success = true });
        }

        // 🎯 JaaS 錄影完成後會呼叫這個網址（RECORDING_UPLOADED webhook），把下載連結傳過來
        //    要在 JaaS 後台設定 Webhook 網址是「你的公開網址/Room/RecordingWebhook」才收得到
        //    ⚠️ 目前欄位名稱是照 JaaS 文件推測寫的，實際收到的 JSON 格式要等正式串接時再核對調整
        [HttpPost]
        public async Task<IActionResult> RecordingWebhook()
        {
            using var reader = new System.IO.StreamReader(Request.Body);
            var raw = await reader.ReadToEndAsync();

            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(raw);
                var root = doc.RootElement;

                string? link = null;
                if (root.TryGetProperty("preAuthenticatedLink", out var linkEl))
                {
                    link = linkEl.GetString();
                }
                else if (root.TryGetProperty("data", out var dataEl)
                         && dataEl.TryGetProperty("preAuthenticatedLink", out var linkEl2))
                {
                    link = linkEl2.GetString();
                }

                string? meetingFqn = null;
                if (root.TryGetProperty("meetingFqn", out var fqnEl))
                {
                    meetingFqn = fqnEl.GetString();
                }
                // meetingFqn 格式通常是 "{appId}/{roomCode}"，取最後一段當作 JitsiRoomName 比對
                string? roomCode = meetingFqn?.Split('/').LastOrDefault();

                if (link != null && roomCode != null)
                {
                    var room = await _context.Rooms.FirstOrDefaultAsync(r => r.JitsiRoomName == roomCode);
                    if (room != null)
                    {
                        room.RecordingUrl = link;
                        await _context.SaveChangesAsync();
                    }
                }
            }
            catch
            {
                // 先不擋，避免 JaaS 收到非 200 一直重送；之後正式串接時再依實際 payload 調整解析邏輯
            }

            return Ok();
        }

        // 🎯 逐字稿改成「每個人各自把自己聽到的內容直接送到伺服器」，不再依賴 SignalR 廣播給其他人再由主持人彙整
        //    （SignalR 對這種長時間會議連線不夠可靠，之前發現只有主持人自己的話會被記錄下來，就是廣播失敗造成的）
        //
        //    🐛 這輪修正：暫存區改成存進共用資料庫的 TranscriptChunks 表，不再用記憶體裡的 static 變數。
        //    原因：這個專案的實際測試方式是每個人在自己電腦上各自跑一份程式，只有 Jitsi 視訊跟資料庫是共用的，
        //    ASP.NET 程序本身不是共用的——存在記憶體裡的暫存區，主持人的「結束會議」只看得到
        //    主持人自己那台電腦記憶體裡的內容，這才是「逐字稿一直只有主持人」的真正原因。
        //    改存資料庫後，不管是誰在哪一台電腦送出的內容，主持人結束會議時都查得到。
        public class TranscriptChunkDto
        {
            public string Sp { get; set; } = "";
            public string Tx { get; set; } = "";
            public string Time { get; set; } = "";
        }

        // 🎯 每個人（不管求職者/主管/主持人）自己講的話，直接送這裡，不透過任何人轉傳
        [HttpPost]
        public async Task<IActionResult> SubmitTranscriptChunk([FromQuery] string roomCode, [FromBody] List<TranscriptChunkDto>? lines)
        {
            if (string.IsNullOrEmpty(roomCode) || lines == null || lines.Count == 0)
                return Ok(new { success = true });

            var now = DateTime.UtcNow;
            var records = lines
                .Where(l => !string.IsNullOrWhiteSpace(l.Tx))
                .Select(l => new TranscriptChunkRecord
                {
                    RoomCode = roomCode,
                    Speaker = l.Sp,
                    Text = l.Tx,
                    TimeLabel = l.Time,
                    ReceivedAt = now
                })
                .ToList();

            if (records.Count > 0)
            {
                _context.TranscriptChunks.AddRange(records);
                await _context.SaveChangesAsync();
            }
            return Ok(new { success = true });
        }

        // 🎯 逐字稿改用這個當主要來源：不再依賴瀏覽器原生 SpeechRecognition
        //    （已證實會被 Jitsi 搶走麥克風獨佔權，大部分人只收到 no-speech，整場話完全沒被記錄下來）。
        //    改成每個人自己把整場錄下來的麥克風音檔，直接送給 Gemini 做語音轉文字，結果併進同一個資料庫暫存表。
        [HttpPost]
        [RequestSizeLimit(25_000_000)]
        public async Task<IActionResult> SubmitAudioTranscript([FromQuery] string roomCode, [FromQuery] string speakerName, IFormFile? audio)
        {
            if (string.IsNullOrEmpty(roomCode) || audio == null || audio.Length == 0)
                return Ok(new { success = true });

            byte[] bytes;
            using (var ms = new MemoryStream())
            {
                await audio.CopyToAsync(ms);
                bytes = ms.ToArray();
            }

            var mimeType = string.IsNullOrEmpty(audio.ContentType) ? "audio/webm" : audio.ContentType;
            var text = await _gemini.TranscribeAudioAsync(bytes, mimeType);

            if (string.IsNullOrWhiteSpace(text) || text.Contains("無語音內容"))
                return Ok(new { success = true }); // 轉錄失敗或整段都沒講話，不用存

            var timeLabel = DateTime.Now.ToString("tt h:mm:ss", new System.Globalization.CultureInfo("zh-TW"));
            _context.TranscriptChunks.Add(new TranscriptChunkRecord
            {
                RoomCode = roomCode,
                Speaker = speakerName,
                Text = text,
                TimeLabel = timeLabel,
                ReceivedAt = DateTime.UtcNow
            });
            await _context.SaveChangesAsync();
            return Ok(new { success = true });
        }

        // 🐛 這輪新增：給「結束會議」畫面用的真實狀態查詢，不要再無條件顯示「錄影已完成上傳」。
        //    AI 面試官加入會議、錄影上傳，都是背景執行的（見 MeetingHub.StartMeeting / LeaveRoomAsync 的
        //    fire-and-forget 工作），主持人按下「結束會議」的當下，錄影很可能都還沒上傳完——
        //    所以這裡會等一下（最多 2 分鐘，跟 SaveAiAnalysis 那邊等錄影的邏輯一致，
        //    本機測試時上傳頻寬常常比機房環境慢很多，40 秒不夠用），
        //    真的查到最後結果（成功有檔案 / AI 面試官加入失敗 / 逾時還沒完成）才回傳，讓前端能誠實顯示。
        [HttpGet]
        public async Task<IActionResult> GetRecordingStatus(string roomCode)
        {
            var room = await _context.Rooms.FirstOrDefaultAsync(r => r.JitsiRoomName == roomCode);
            if (room == null) return NotFound();

            for (int i = 0; i < 60; i++)
            {
                if (!string.IsNullOrEmpty(room.RecordingFileName) || !string.IsNullOrEmpty(room.AiBotErrorMessage))
                    break;
                await Task.Delay(2000);
                await _context.Entry(room).ReloadAsync();
            }

            return Ok(new
            {
                success = true,
                recordingFileName = room.RecordingFileName,
                aiBotErrorMessage = room.AiBotErrorMessage
            });
        }

        // 🎯 逐字稿改存到 Cloudflare R2，不再存本機 wwwroot
        //    這樣本機執行跟部署到 Render，讀到的都是同一份雲端檔案，不會再有兩邊結果不一致的問題
        //    🐛 內容不再信任客戶端傳來的單一字串（那是舊架構，只有主持人自己聽到的+SignalR廣播成功的部分）
        //    改成合併 TranscriptChunks 表裡「這個房間所有人各自送來」的片段，依伺服器收到的時間排序
        [HttpPost]
        public async Task<IActionResult> SaveTranscript([FromForm] string roomCode)
        {
            var room = await _context.Rooms.Include(r => r.Job).FirstOrDefaultAsync(r => r.JitsiRoomName == roomCode);
            if (room == null) return NotFound();

            // 🎯 合併這個房間所有人各自送來的逐字稿片段（存資料庫的，不管是誰在哪一台電腦送出的都查得到），
            //    依伺服器收到的時間排序（不是靠客戶端自己拼的順序）
            var chunks = await _context.TranscriptChunks
                .Where(c => c.RoomCode == roomCode)
                .OrderBy(c => c.ReceivedAt)
                .ToListAsync();

            bool isEmpty;
            string content;
            if (chunks.Count > 0)
            {
                // 🐛 防呆：Tx 內容理論上已經在 GeminiService.CleanUpHallucination() 清過，
                //    但保險起見，這裡還是把任何殘留的換行壓成空白，確保輸出的每一行
                //    一定都有「[時間] 講者：」開頭，不會再冒出前面一段有幾十行看不出是誰講的裸行
                content = string.Join("\n", chunks.Select(c =>
                    $"[{c.TimeLabel}] {c.Speaker}：{c.Text.Replace("\r\n", " ").Replace("\n", " ").Trim()}"));
                isEmpty = false;
            }
            else
            {
                content = "（本場會議未收集到逐字稿內容，可能原因：所有參與者的麥克風權限都被 Jitsi 視訊佔用，或麥克風未授權給瀏覽器）";
                isEmpty = true;
            }

            var fileName = BuildFileName(room, "txt");
            try
            {
                await _storage.UploadTextAsync($"逐字稿/{fileName}", content);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, message = "逐字稿上傳到雲端儲存失敗：" + ex.Message });
            }

            if (chunks.Count > 0)
            {
                _context.TranscriptChunks.RemoveRange(chunks); // 合併完成，暫存的可以清掉了
            }

            room.TranscriptFileName = fileName;
            await _context.SaveChangesAsync();

            return Ok(new { success = true, fileName, content, isEmpty });
        }

        // 🎯 錄影錄音改存到 Cloudflare R2
        [HttpPost]
        [RequestSizeLimit(500_000_000)] // 放寬到 500MB，避免長時間會議的錄影檔案被擋掉
        public async Task<IActionResult> SaveRecording([FromForm] string roomCode, IFormFile file)
        {
            if (file == null || file.Length == 0) return BadRequest();

            var room = await _context.Rooms.Include(r => r.Job).FirstOrDefaultAsync(r => r.JitsiRoomName == roomCode);
            if (room == null) return NotFound();

            var fileName = BuildFileName(room, "webm");
            try
            {
                using var stream = file.OpenReadStream();
                await _storage.UploadStreamAsync($"錄影錄音/{fileName}", stream, "video/webm");
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, message = "錄影上傳到雲端儲存失敗：" + ex.Message });
            }

            room.RecordingFileName = fileName;
            await _context.SaveChangesAsync();

            return Ok(new { success = true, fileName });
        }

        // 🎯 AI 面試分析結果改存到 Cloudflare R2，而且改成「每位求職者各自分析、各自存一份檔案」
        //    （不再是所有人塞進同一次 Gemini 呼叫裡分析，那樣字數會被瓜分，容易被截斷到講一半）
        //
        //    🐛 這輪修正兩個問題：
        //    1. 「有沒有參加面試」原本是用 transcript.Contains(candidateName) 判斷，
        //       但逐字稿本來就可能因為轉錄失敗/被過濾掉而缺漏某人的發言，導致明明有參加卻被誤判成沒參加。
        //       改成看 RoomParticipants 這張表的 JoinedAt（他真的進過會議室的紀錄），這才是可信的事實來源。
        //    2. AI 分析不能只靠逐字稿文字——逐字稿只有「說了什麼」，完全看不出語氣、表情。
        //       改成優先用「錄影檔（多模態：畫面表情 + 聲音語氣）+ 逐字稿」一起送給 Gemini 分析；
        //       如果錄影還沒上傳完成（AI 面試官錄影上傳是背景工作，可能還在跑）或上傳/分析失敗，
        //       才退回原本純文字逐字稿的分析方式，確保這個功能不會因為多模態那條路失敗就整個掛掉。
        [HttpPost]
        public async Task<IActionResult> SaveAiAnalysis([FromForm] string roomCode, [FromForm] string transcript)
        {
            var room = await _context.Rooms.Include(r => r.Job).FirstOrDefaultAsync(r => r.JitsiRoomName == roomCode);
            if (room == null) return NotFound();

            // 這場面試受邀的所有求職者（一個房間理論上可能不只一位）
            var jobseekers = await _context.RoomParticipants
                .Where(p => p.RoomId == room.Id && p.Role == ParticipantRole.Jobseeker && p.ResumeId != null)
                .Include(p => p.Resume).ThenInclude(r => r!.Member)
                .ToListAsync();

            if (jobseekers.Count == 0)
                return Ok(new { success = true, files = new List<object>() });

            // 🎯 錄影上傳是背景工作（EndMeeting 觸發後 AI 面試官才離開會議、轉檔、上傳），
            //    很可能還沒做完就走到這裡了。短輪詢等它一下，等不到才放棄多模態、退回純文字分析。
            //    🐛 這輪拉長：原本只等 40 秒，實測發現本機環境（不是部署在機房，上傳頻寬較差）
            //    常常錄影上傳花超過 40 秒還沒好，導致明明錄影後來有成功，AI 分析卻已經先放棄、退回純文字，
            //    白白浪費了多模態分析的機會。拉長到最多等 2 分鐘，比較符合實際本機測試的網路狀況。
            string? recordingFileName = room.RecordingFileName;
            for (int i = 0; i < 60 && string.IsNullOrEmpty(recordingFileName); i++)
            {
                await Task.Delay(2000);
                await _context.Entry(room).ReloadAsync();
                recordingFileName = room.RecordingFileName;
            }
            if (string.IsNullOrEmpty(recordingFileName))
            {
                Console.WriteLine($"[SaveAiAnalysis] 房間 {roomCode} 等了 2 分鐘還是沒等到錄影檔案，退回純文字分析（AiBotErrorMessage：{room.AiBotErrorMessage ?? "(無)"}）");
            }

            // 🎯 把錄影檔上傳到 Gemini File API 一次就好（同一支影片，每位求職者都能重複參照同一個 file_uri），
            //    避免每位求職者都重傳一次整支影片、浪費時間跟流量
            string? geminiFileUri = null;
            string? geminiFileName = null;
            if (!string.IsNullOrEmpty(recordingFileName) && _storage.IsConfigured)
            {
                try
                {
                    var videoBytes = await _storage.DownloadBytesAsync($"錄影錄音/{recordingFileName}");
                    // Gemini File API 單檔上限是 2GB，但免費額度/請求逾時考量下，太大的檔案還是直接放棄多模態、退回文字分析比較穩
                    if (videoBytes.Length > 0 && videoBytes.Length < 200_000_000)
                    {
                        (geminiFileUri, geminiFileName) = await _gemini.UploadFileAsync(videoBytes, "video/webm", recordingFileName);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SaveAiAnalysis] 下載/上傳錄影檔給 Gemini 失敗，退回純文字分析：{ex.Message}");
                }
            }

            var results = new List<object>();

            bool isFirst = true;
            foreach (var jobseeker in jobseekers)
            {
                // 🎯 候選人之間加一點間隔，避免連續呼叫 Gemini API 太快撞到免費額度的速率限制
                //    （這很可能就是「有些候選人分析成功、有些失敗」的真正原因，不是金鑰設定問題——
                //    金鑰是伺服器端統一設定的同一組，不會因為誰登入而不同）
                if (!isFirst) await Task.Delay(1500);
                isFirst = false;

                var candidateName = jobseeker.Resume?.Member?.Name ?? $"求職者{jobseeker.Id}";

                // 🎯 用 RoomParticipants 的實際進場紀錄判斷有沒有參加，而不是硬找逐字稿裡有沒有出現名字
                //    （逐字稿本來就可能缺漏，用字串比對會把「有參加但逐字稿沒收好」的人誤判成沒參加）
                bool actuallyAttended = jobseeker.Status == ParticipantStatus.Admitted && jobseeker.JoinedAt != null;

                if (!actuallyAttended)
                {
                    var noShowMsg = $"求職者{candidateName}並未參加此次面試會議（會議紀錄中沒有他的進場紀錄），無法針對其面試表現進行 AI 分析與評分。";
                    var noShowFileName = BuildFileName(room, "txt", candidateName);
                    try
                    {
                        await _storage.UploadTextAsync($"AI分析/{noShowFileName}", noShowMsg);
                        results.Add(new { candidateName, success = true, fileName = noShowFileName });
                    }
                    catch (Exception ex)
                    {
                        results.Add(new { candidateName, success = false, message = "上傳到雲端儲存失敗：" + ex.Message });
                    }
                    continue;
                }

                string? analysis = null;

                // 🎯 優先走多模態（看畫面表情、聽聲音語氣），只有在錄影不可用時才退回純文字
                //
                // 🐛 這輪修正：maxTokens 原本只有 3000，使用者回報「AI分析並沒有完全分析完就沒了」——
                //    這份分析要求 6 個段落（語氣、表情、答題邏輯、評分、錄取建議、整體評語），
                //    尤其多模態分析還要描述實際觀察到的畫面細節，中文字數換算下來 3000 tokens
                //    很容易在寫到第 5、6 段的時候就被硬生生截斷，不是內容本身有問題，是額度給太少。
                //    拉高到 8000，讓 Gemini 有足夠空間把 6 個段落真的寫完。
                if (!string.IsNullOrEmpty(geminiFileUri))
                {
                    analysis = await _gemini.AnalyzeInterviewVideoAsync(geminiFileUri, "video/webm", candidateName, transcript, maxTokens: 8000);
                    if (analysis == null)
                    {
                        await Task.Delay(2000);
                        analysis = await _gemini.AnalyzeInterviewVideoAsync(geminiFileUri, "video/webm", candidateName, transcript, maxTokens: 8000);
                    }
                }

                if (analysis == null)
                {
                    // 🎯 沒有錄影可用，或多模態分析失敗兩次，退回原本純文字逐字稿分析（確保這功能不會整個掛掉）
                    var prompt =
                        $"以下是一場面試的完整逐字稿（可能包含多位求職者，發言前有標示姓名），" +
                        $"請只針對「{candidateName}」這位求職者的發言與表現進行分析（繁體中文，不用分析其他人）：\n\n{transcript}\n\n" +
                        $"請提供：1.語氣表達 2.答題品質 3.AI 綜合評分（0-100分，格式：AI 綜合評分：XX 分） 4.錄取建議 5.整體評語（完整寫完，不要中途省略）\n" +
                        $"（提醒：這次沒有錄影畫面可看，只能依逐字稿文字判斷，語氣/表情部分請誠實註明「僅依文字內容推測，非實際觀察畫面與聲音」）";

                    analysis = await _gemini.AskAsync(
                        prompt,
                        "你是專業人資顧問，說繁體中文，格式清晰條列，內容要完整寫完，不能寫到一半就停。",
                        maxTokens: 8000);

                    if (analysis == null)
                    {
                        await Task.Delay(2000);
                        analysis = await _gemini.AskAsync(
                            prompt,
                            "你是專業人資顧問，說繁體中文，格式清晰條列，內容要完整寫完，不能寫到一半就停。",
                            maxTokens: 8000);
                    }
                }

                if (analysis == null)
                {
                    results.Add(new { candidateName, success = false, message = "AI 分析失敗（重試一次後仍失敗，可能是 API 額度用完或速率限制）" });
                    continue;
                }

                var fileName = BuildFileName(room, "txt", candidateName);
                try
                {
                    await _storage.UploadTextAsync($"AI分析/{fileName}", analysis);
                    results.Add(new { candidateName, success = true, fileName });
                }
                catch (Exception ex)
                {
                    results.Add(new { candidateName, success = false, message = "上傳到雲端儲存失敗：" + ex.Message });
                }
            }

            // 🎯 這場分析用的暫存影片檔在 Gemini 端可以清掉了，不用等 48 小時自動過期
            if (!string.IsNullOrEmpty(geminiFileName))
            {
                _ = _gemini.DeleteFileAsync(geminiFileName);
            }

            return Ok(new { success = true, files = results });
        }

        // 🎯 檔案總管：列出 R2 上「逐字稿」「錄影錄音」「AI分析」三個前綴底下目前存了哪些檔案，並提供下載連結
        //    只有 HR/主管/最高主管能看（求職者不用看到這個）
        public async Task<IActionResult> Files()
        {
            var role = HttpContext.Session.GetString("MemberRole")?.ToLower();
            if (role != "hr" && role != "manager" && role != "director")
                return RedirectToAction("Index", "Login");

            if (!_storage.IsConfigured)
            {
                ViewBag.NotConfigured = true;
                ViewBag.Transcripts = new List<string?>();
                ViewBag.Recordings = new List<string?>();
                ViewBag.AiAnalyses = new List<string?>();
                return View("~/Views/Room/Files.cshtml");
            }

            var transcriptKeys = await _storage.ListKeysAsync("逐字稿/");
            var recordingKeys = await _storage.ListKeysAsync("錄影錄音/");
            var aiKeys = await _storage.ListKeysAsync("AI分析/");

            ViewBag.Transcripts = transcriptKeys.Select(k => k.Substring("逐字稿/".Length)).ToList();
            ViewBag.Recordings = recordingKeys.Select(k => k.Substring("錄影錄音/".Length)).ToList();
            ViewBag.AiAnalyses = aiKeys.Select(k => k.Substring("AI分析/".Length)).ToList();

            return View("~/Views/Room/Files.cshtml");
        }

        // 下載指定「資料夾」（R2 key 前綴）裡的檔案（folder 只允許這三個固定值，避免被拿去讀取其他任意路徑）
        // 直接 302 導向 R2 的簽名網址，不用把大檔案的位元組再繞經我們自己的伺服器
        public async Task<IActionResult> DownloadFile(string folder, string fileName)
        {
            var role = HttpContext.Session.GetString("MemberRole")?.ToLower();
            if (role != "hr" && role != "manager" && role != "director")
                return RedirectToAction("Index", "Login");

            if (folder != "逐字稿" && folder != "錄影錄音" && folder != "AI分析") return BadRequest();

            var safeFileName = Path.GetFileName(fileName); // 防止路徑跳脫
            var key = $"{folder}/{safeFileName}";

            if (!await _storage.ExistsAsync(key)) return NotFound();

            var url = _storage.GetPresignedUrl(key, TimeSpan.FromMinutes(15), downloadFileName: safeFileName);
            return Redirect(url);
        }

        // 🎯 直接查看檔案內容（不強制下載）
        //    逐字稿/AI分析：檔案小，直接由我們的伺服器讀出文字內容回傳（給前端 fetch() 用，同源不用處理 CORS）
        //    錄影錄音：302 導向 R2 簽名網址，讓 <video> 標籤直接播放（R2 原生支援 Range 請求，拖拉進度條沒問題）
        public async Task<IActionResult> ViewFile(string folder, string fileName)
        {
            var role = HttpContext.Session.GetString("MemberRole")?.ToLower();
            if (role != "hr" && role != "manager" && role != "director")
                return RedirectToAction("Index", "Login");

            if (folder != "逐字稿" && folder != "錄影錄音" && folder != "AI分析") return BadRequest();

            var safeFileName = Path.GetFileName(fileName);
            var key = $"{folder}/{safeFileName}";

            if (!await _storage.ExistsAsync(key)) return NotFound();

            if (folder == "錄影錄音")
            {
                var url = _storage.GetPresignedUrl(key, TimeSpan.FromMinutes(15));
                return Redirect(url);
            }

            try
            {
                var text = await _storage.DownloadTextAsync(key);
                return Content(text, "text/plain; charset=utf-8", System.Text.Encoding.UTF8);
            }
            catch (Exception ex)
            {
                return StatusCode(500, "讀取雲端檔案失敗：" + ex.Message);
            }
        }

        // 🎯 依逐字稿內容產生 WebVTT 字幕檔，讓影片播放器的「CC 字幕」按鈕可以顯示逐字稿
        //    ⚠️ 時間軸是用逐字稿裡每行的時間戳，相對「第一句話」往後換算出來的估算值，
        //       不是跟錄影檔案逐格對齊（錄影是使用者按下畫面分享授權才真正開始錄，跟第一句話開口的時間可能有幾秒落差），
        //       僅供對照參考，不保證完全同步
        public async Task<IActionResult> TranscriptVtt(int roomId)
        {
            var role = HttpContext.Session.GetString("MemberRole")?.ToLower();
            if (role != "hr" && role != "manager" && role != "director")
                return RedirectToAction("Index", "Login");

            var room = await _context.Rooms.FirstOrDefaultAsync(r => r.Id == roomId);
            if (room == null || string.IsNullOrEmpty(room.TranscriptFileName))
                return Content("WEBVTT\n", "text/vtt", Encoding.UTF8);

            var key = $"逐字稿/{room.TranscriptFileName}";
            if (!await _storage.ExistsAsync(key))
                return Content("WEBVTT\n", "text/vtt", Encoding.UTF8);

            var content = await _storage.DownloadTextAsync(key);
            var lines = content.Split('\n');
            var vtt = BuildVttFromTranscript(lines);
            return Content(vtt, "text/vtt", Encoding.UTF8);
        }

        // 🐛 這輪修正：逐字稿實際的時間格式是「[上午/下午HH:MM:SS]」或「[上午/下午 H:MM:SS]」
        //    （中文上午/下午前綴，時跟前綴之間有沒有空白、時是不是補零，兩種來源產生的格式不完全一樣：
        //    伺服器端音檔轉錄用 DateTime.ToString("tt h:mm:ss", zh-TW) 會帶空白、時不補零；
        //    瀏覽器端 SpeechRecognition 備援機制自己組字串則是不帶空白、時有補零），
        //    不是原本規則式假設的純西式「[HH:MM:SS]」24小時制格式——原本的規則式從頭到尾都比對不到任何一行，
        //    所以字幕永遠是空的，不是逐字稿內容或時間計算本身有問題，是這個規則式打從一開始就跟真正的格式對不上。
        private static readonly Regex TranscriptTimeRegex =
            new(@"^\[(上午|下午)\s*(\d{1,2}):(\d{2}):(\d{2})\]\s*(.+)$");

        // 逐字稿固定格式是「[上午/下午 H:MM:SS] 姓名：內容」，用第一句話的時間當基準點（t0 = 00:00.000）往後推算每句話的時間
        private string BuildVttFromTranscript(string[] lines)
        {
            var sb = new StringBuilder();
            sb.AppendLine("WEBVTT");
            sb.AppendLine();

            var cues = new List<(TimeSpan time, string text)>();

            foreach (var raw in lines)
            {
                var line = raw?.Trim();
                if (string.IsNullOrEmpty(line)) continue;

                var m = TranscriptTimeRegex.Match(line);
                if (!m.Success) continue;

                var ampm = m.Groups[1].Value; // 上午 / 下午
                var hour = int.Parse(m.Groups[2].Value);
                var minute = int.Parse(m.Groups[3].Value);
                var second = int.Parse(m.Groups[4].Value);
                var text = m.Groups[5].Value;

                // 中文上午/下午轉成 24 小時制：上午12點是半夜0點，下午12點才是中午12點，其餘下午 +12
                int hour24 = ampm == "上午"
                    ? (hour == 12 ? 0 : hour)
                    : (hour == 12 ? 12 : hour + 12);

                var t = new TimeSpan(0, hour24, minute, second);
                cues.Add((t, text));
            }

            if (!cues.Any()) return sb.ToString();

            var t0 = cues[0].time;
            int index = 1;
            for (int i = 0; i < cues.Count; i++)
            {
                var start = cues[i].time - t0;
                if (start < TimeSpan.Zero) start = TimeSpan.Zero;

                // 每句字幕預設顯示 4 秒，但如果下一句提早出現就提早結束，避免字幕重疊
                var end = start + TimeSpan.FromSeconds(4);
                if (i + 1 < cues.Count)
                {
                    var nextStart = cues[i + 1].time - t0;
                    if (nextStart < end && nextStart > start) end = nextStart;
                }
                if (end <= start) end = start + TimeSpan.FromSeconds(1);

                sb.AppendLine(index.ToString());
                sb.AppendLine($"{FormatVttTime(start)} --> {FormatVttTime(end)}");
                sb.AppendLine(cues[i].text);
                sb.AppendLine();
                index++;
            }

            return sb.ToString();
        }

        private string FormatVttTime(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            return $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}.{t.Milliseconds:D3}";
        }

        // 「面試會議_日期_職缺.副檔名」，職缺名稱裡不能當檔名的符號換成底線
        // 🎯 public static：MeetingHub 結束會議時，AI 面試官錄影上傳也要用同一套命名規則
        //    candidateName 有值時（AI 分析逐位存檔用），檔名會多一段求職者姓名後綴
        public static string BuildFileName(Room room, string ext, string? candidateName = null)
        {
            var jobTitle = room.Job?.Title ?? "未知職缺";
            foreach (var c in Path.GetInvalidFileNameChars())
                jobTitle = jobTitle.Replace(c, '_');
            jobTitle = jobTitle.Replace('/', '_').Replace('－', '_').Replace('-', '_');

            var dateText = (room.StartAt ?? room.ScheduledAt ?? DateTime.Now).ToString("yyyy-MM-dd");

            if (string.IsNullOrWhiteSpace(candidateName))
                return $"面試會議_{dateText}_{jobTitle}.{ext}";

            var safeName = candidateName;
            foreach (var c in Path.GetInvalidFileNameChars())
                safeName = safeName.Replace(c, '_');
            safeName = safeName.Replace('/', '_');

            return $"面試會議_{dateText}_{jobTitle}_{safeName}.{ext}";
        }
        //    求職者：session 存的是 Member.Id，要透過 Resume 反查
        //    員工（manager / director / hr）：session 存的是 Employee.Id，直接比對
        private async Task<RoomParticipant?> FindParticipantAsync(Room room, int sessionMemberId, string role)
        {
            if (role == "jobseeker")
            {
                return await _context.RoomParticipants
                    .Include(p => p.Resume)
                    .FirstOrDefaultAsync(p => p.RoomId == room.Id
                        && p.Role == ParticipantRole.Jobseeker
                        && p.Resume != null
                        && p.Resume.MembersId == sessionMemberId);
            }

            return await _context.RoomParticipants
                .FirstOrDefaultAsync(p => p.RoomId == room.Id && p.EmployeeId == sessionMemberId);
        }

        [HttpGet]
        public async Task<IActionResult> RoomStatus(string code)
        {
            var room = await _context.Rooms.FirstOrDefaultAsync(x => x.JitsiRoomName == code);
            if (room == null) return Json(new { found = false });

            return Json(new
            {
                found = true,
                canEnter = room.CanEnter(),
                statusText = room.StatusText(),
                startAt = room.StartAt?.ToString("yyyy-MM-ddTHH:mm:ss"),
                endAt = room.EndAt?.ToString("yyyy-MM-ddTHH:mm:ss")
            });
        }
    }
}