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

        public RoomController(AppDbContext context, JitsiBotService botService, IWebHostEnvironment env)
        {
            _context = context;
            _botService = botService;
            _env = env;
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
                if (participant.Role == ParticipantRole.Jobseeker)
                {
                    var hasTest = await _context.AptitudeTestResults.AnyAsync(t => t.ResumeId == participant.ResumeId);
                    if (!hasTest)
                    {
                        ViewBag.ErrorMessage = "請先完成適性測驗，才能進入面試";
                        return View();
                    }
                }
            }

            return RedirectToAction("Join", new { code = room.JitsiRoomName });
        }

        // 🎯 修正：加入安全性阻擋與非同步優化
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

                participant.Status = ParticipantStatus.Admitted;
                participant.JoinedAt = DateTime.Now;

                // 🎯 求職者一定要先完成適性測驗，才能真的進入會議室（就算已經受邀、知道代碼也一樣）
                if (participant.Role == ParticipantRole.Jobseeker)
                {
                    var hasTest = await _context.AptitudeTestResults.AnyAsync(t => t.ResumeId == participant.ResumeId);
                    if (!hasTest)
                    {
                        ViewBag.Room = room;
                        ViewBag.ErrorMessage = "請先完成適性測驗，才能進入面試";
                        return View("RoomNotAvailable");
                    }
                }

                // 🎯 注意：這裡只做資格檢查，不寫入資料庫。
                //    Status=Admitted、JoinedAt 要等使用者在 Jitsi 畫面真的按下「加入會議」才算數，
                //    由前端 videoConferenceJoined 事件呼叫 /Room/MarkJoined 來記錄（見下方 MarkJoined action）。

                ViewBag.ParticipantRole = participant.Role;
            }

            ViewBag.Room = room;
            return View();
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

        // 🎯 逐字稿存到專案裡的「逐字稿」資料夾，不是存到瀏覽器下載資料夾
        //    ⚠️ 注意：如果部署在 Render 免費方案，磁碟是「非永久性」的，服務重啟/休眠喚醒後這裡存的檔案會消失，
        //    要保留檔案得另外接雲端儲存或升級成有 Persistent Disk 的方案
        [HttpPost]
        public async Task<IActionResult> SaveTranscript([FromForm] string roomCode, [FromForm] string content)
        {
            var room = await _context.Rooms.Include(r => r.Job).FirstOrDefaultAsync(r => r.JitsiRoomName == roomCode);
            if (room == null) return NotFound();

            var folder = Path.Combine(_env.WebRootPath, "逐字稿");
            Directory.CreateDirectory(folder);

            var fileName = BuildFileName(room, "txt");
            var path = Path.Combine(folder, fileName);
            await System.IO.File.WriteAllTextAsync(path, content, System.Text.Encoding.UTF8);

            room.TranscriptFileName = fileName;
            await _context.SaveChangesAsync();

            return Ok(new { success = true, fileName });
        }

        // 🎯 錄影錄音存到專案裡的「錄影錄音」資料夾
        [HttpPost]
        [RequestSizeLimit(500_000_000)] // 放寬到 500MB，避免長時間會議的錄影檔案被擋掉
        public async Task<IActionResult> SaveRecording([FromForm] string roomCode, IFormFile file)
        {
            if (file == null || file.Length == 0) return BadRequest();

            var room = await _context.Rooms.Include(r => r.Job).FirstOrDefaultAsync(r => r.JitsiRoomName == roomCode);
            if (room == null) return NotFound();

            var folder = Path.Combine(_env.WebRootPath, "錄影錄音");
            Directory.CreateDirectory(folder);

            var fileName = BuildFileName(room, "webm");
            var path = Path.Combine(folder, fileName);
            using (var stream = System.IO.File.Create(path))
            {
                await file.CopyToAsync(stream);
            }

            room.RecordingFileName = fileName;
            await _context.SaveChangesAsync();

            return Ok(new { success = true, fileName });
        }

        // 🎯 AI 面試分析結果也存到專案裡的「AI分析」資料夾
        [HttpPost]
        public async Task<IActionResult> SaveAiAnalysis([FromForm] string roomCode, [FromForm] string content)
        {
            var room = await _context.Rooms.Include(r => r.Job).FirstOrDefaultAsync(r => r.JitsiRoomName == roomCode);
            if (room == null) return NotFound();

            var folder = Path.Combine(_env.WebRootPath, "AI分析");
            Directory.CreateDirectory(folder);

            var fileName = BuildFileName(room, "txt");
            var path = Path.Combine(folder, fileName);
            await System.IO.File.WriteAllTextAsync(path, content, System.Text.Encoding.UTF8);

            room.AiAnalysisFileName = fileName;
            await _context.SaveChangesAsync();

            return Ok(new { success = true, fileName });
        }

        // 🎯 檔案總管：列出「逐字稿」「錄影錄音」「AI分析」資料夾裡目前存了哪些檔案，並提供下載連結
        //    只有 HR/主管/最高主管能看（求職者不用看到這個）
        public IActionResult Files()
        {
            var role = HttpContext.Session.GetString("MemberRole")?.ToLower();
            if (role != "hr" && role != "manager" && role != "director")
                return RedirectToAction("Index", "Login");

            var transcriptFolder = Path.Combine(_env.WebRootPath, "逐字稿");
            var recordingFolder = Path.Combine(_env.WebRootPath, "錄影錄音");
            var aiFolder = Path.Combine(_env.WebRootPath, "AI分析");

            ViewBag.Transcripts = Directory.Exists(transcriptFolder)
                ? Directory.GetFiles(transcriptFolder).Select(Path.GetFileName).OrderByDescending(f => f).ToList()
                : new List<string?>();

            ViewBag.Recordings = Directory.Exists(recordingFolder)
                ? Directory.GetFiles(recordingFolder).Select(Path.GetFileName).OrderByDescending(f => f).ToList()
                : new List<string?>();

            ViewBag.AiAnalyses = Directory.Exists(aiFolder)
                ? Directory.GetFiles(aiFolder).Select(Path.GetFileName).OrderByDescending(f => f).ToList()
                : new List<string?>();

            return View("~/Views/Room/Files.cshtml");
        }

        // 下載指定資料夾裡的檔案（folder 只允許這三個固定值，避免被拿去讀取其他任意路徑）
        public IActionResult DownloadFile(string folder, string fileName)
        {
            var role = HttpContext.Session.GetString("MemberRole")?.ToLower();
            if (role != "hr" && role != "manager" && role != "director")
                return RedirectToAction("Index", "Login");

            if (folder != "逐字稿" && folder != "錄影錄音" && folder != "AI分析") return BadRequest();

            var safeFileName = Path.GetFileName(fileName); // 防止路徑跳脫
            var filePath = Path.Combine(_env.WebRootPath, folder, safeFileName);
            if (!System.IO.File.Exists(filePath)) return NotFound();

            var contentType = folder == "錄影錄音" ? "video/webm" : "text/plain";
            return PhysicalFile(filePath, contentType, safeFileName);
        }

        // 🎯 直接查看檔案內容（不強制下載）：逐字稿/AI分析瀏覽器會直接顯示純文字，錄影錄音則當作影片播放來源
        //    跟 DownloadFile 的差別只在於沒有帶 fileDownloadName，瀏覽器不會強制跳出「另存新檔」
        public IActionResult ViewFile(string folder, string fileName)
        {
            var role = HttpContext.Session.GetString("MemberRole")?.ToLower();
            if (role != "hr" && role != "manager" && role != "director")
                return RedirectToAction("Index", "Login");

            if (folder != "逐字稿" && folder != "錄影錄音" && folder != "AI分析") return BadRequest();

            var safeFileName = Path.GetFileName(fileName); // 防止路徑跳脫
            var filePath = Path.Combine(_env.WebRootPath, folder, safeFileName);
            if (!System.IO.File.Exists(filePath)) return NotFound();

            var contentType = folder == "錄影錄音" ? "video/webm" : "text/plain; charset=utf-8";
            return PhysicalFile(filePath, contentType, enableRangeProcessing: true); // 支援影片拖拉進度條的Range請求
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

            var filePath = Path.Combine(_env.WebRootPath, "逐字稿", room.TranscriptFileName);
            if (!System.IO.File.Exists(filePath))
                return Content("WEBVTT\n", "text/vtt", Encoding.UTF8);

            var lines = await System.IO.File.ReadAllLinesAsync(filePath, Encoding.UTF8);
            var vtt = BuildVttFromTranscript(lines);
            return Content(vtt, "text/vtt", Encoding.UTF8);
        }

        // 逐字稿固定格式是「[HH:MM:SS] 姓名：內容」，用第一句話的時間當基準點（t0 = 00:00.000）往後推算每句話的時間
        private string BuildVttFromTranscript(string[] lines)
        {
            var sb = new StringBuilder();
            sb.AppendLine("WEBVTT");
            sb.AppendLine();

            var regex = new Regex(@"^\[(\d{2}):(\d{2}):(\d{2})\]\s*(.+)$");
            var cues = new List<(TimeSpan time, string text)>();

            foreach (var raw in lines)
            {
                var line = raw?.Trim();
                if (string.IsNullOrEmpty(line)) continue;

                var m = regex.Match(line);
                if (!m.Success) continue;

                var t = new TimeSpan(0, int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
                cues.Add((t, m.Groups[4].Value));
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
        private static string BuildFileName(Room room, string ext)
        {
            var jobTitle = room.Job?.Title ?? "未知職缺";
            foreach (var c in Path.GetInvalidFileNameChars())
                jobTitle = jobTitle.Replace(c, '_');
            jobTitle = jobTitle.Replace('/', '_').Replace('－', '_').Replace('-', '_');

            var dateText = (room.StartAt ?? room.ScheduledAt ?? DateTime.Now).ToString("yyyy-MM-dd");
            return $"面試會議_{dateText}_{jobTitle}.{ext}";
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