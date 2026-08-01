using Microsoft.AspNetCore.SignalR;
using InterviewProject.Data;
using InterviewProject.Models;
using InterviewProject.Services;
using InterviewProject.Controllers;
using Microsoft.EntityFrameworkCore;

namespace InterviewProject.Hubs
{
    public class MeetingHub : Hub
    {
        private readonly AppDbContext _db;
        private readonly JitsiBotService _botService;
        private readonly R2StorageService _storage;
        private readonly IWebHostEnvironment _env;

        public MeetingHub(AppDbContext db, JitsiBotService botService, R2StorageService storage, IWebHostEnvironment env)
        {
            _db = db;
            _botService = botService;
            _storage = storage;
            _env = env;
        }

        // 加入房間群組（前端連上 hub 後第一件事）
        public async Task JoinRoom(string roomCode)
            => await Groups.AddToGroupAsync(Context.ConnectionId, roomCode);

        // 🎯 只有「這個房間受邀的最高主管」才能真的把會議狀態改成進行中
        public async Task StartMeeting(string roomCode)
        {
            var room = await GetAuthorizedHostRoomAsync(roomCode);
            if (room == null) return;

            if (room.MeetingStatus != "InProgress")
            {
                room.MeetingStatus = "InProgress";
                room.StartAt = DateTime.Now; // 🎯 這裡才是真正的開始時間，不是排程時預先填的

                // 🎯 求職者的面試狀態同步推進到「面試中」
                await UpdateJobseekerInterviewStatusAsync(room.Id, InterviewStatusValues.InProgress, null);

                await _db.SaveChangesAsync();
            }

            // 🎯 先讓大家（含主持人自己）收到「會議開始」廣播，不要卡在等 AI 面試官加入會議
            //    （Playwright 開一個無頭瀏覽器大概要幾秒鐘，不該讓真人一起等）
            await Clients.Group(roomCode).SendAsync("MeetingStarted");

            // 🎯 讓 AI 面試官真的加入這場 Jitsi 會議（不是只有前端畫一個假面板），
            //    用 男性面試官.y4m 當假攝影機畫面，並開始錄下它自己在會議室裡看到的畫面——
            //    Jitsi 預設排版就是「目前誰在說話就放大顯示、其他人縮圖排在旁邊」，
            //    這樣錄出來的檔案自然就是「以說話者為主」的視角，不受任何一位真人視窗縮放/被蓋住影響
            var videoPath = Path.Combine(_env.WebRootPath, "video", "男性面試官.y4m");
            try
            {
                await _botService.JoinRoomAsync(roomCode, videoPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MeetingHub] AI 面試官加入房間 {roomCode} 失敗：{ex.Message}");
            }
        }

        // 🎯 只有主持人能結束會議，結束的當下把 EndAt 寫回資料庫，並把求職者狀態推進到「面試結束/等待結果中」
        public async Task EndMeeting(string roomCode)
        {
            var room = await GetAuthorizedHostRoomAsync(roomCode);
            if (room == null) return;

            room.MeetingStatus = "Ended";
            room.EndAt = DateTime.Now;

            await UpdateJobseekerInterviewStatusAsync(room.Id, InterviewStatusValues.Ended, AdmissionResultValues.PendingResult);

            // 🎯 AI 面試官離開會議室，把它錄到的畫面（=以說話者為主的視角）上傳到 R2，
            //    這份取代掉「主管自己螢幕分享」那種畫面，成為這場面試唯一、正式保存的錄影
            string? localVideoPath = null;
            try
            {
                localVideoPath = await _botService.LeaveRoomAsync(roomCode);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MeetingHub] AI 面試官離開房間 {roomCode} 失敗：{ex.Message}");
            }

            if (!string.IsNullOrEmpty(localVideoPath) && File.Exists(localVideoPath))
            {
                try
                {
                    var roomWithJob = await _db.Rooms.Include(r => r.Job).FirstAsync(r => r.Id == room.Id);
                    var fileName = RoomController.BuildFileName(roomWithJob, "webm");

                    await using (var stream = File.OpenRead(localVideoPath))
                    {
                        await _storage.UploadStreamAsync($"錄影錄音/{fileName}", stream, "video/webm");
                    }

                    room.RecordingFileName = fileName;

                    // 上傳完就可以刪暫存檔了，不要一直堆在伺服器磁碟上
                    try
                    {
                        File.Delete(localVideoPath);
                        var dir = Path.GetDirectoryName(localVideoPath);
                        if (dir != null && Directory.Exists(dir) && Directory.GetFiles(dir).Length == 0)
                            Directory.Delete(dir);
                    }
                    catch { /* 刪暫存檔失敗不影響主流程，忽略即可 */ }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[MeetingHub] 錄影上傳到 R2 失敗：{ex.Message}");
                }
            }
            else
            {
                Console.WriteLine($"[MeetingHub] 房間 {roomCode} 沒有取得 AI 面試官的錄影檔，這場面試不會有錄影。");
            }

            await _db.SaveChangesAsync();

            await Clients.Group(roomCode).SendAsync("MeetingEnded");
        }

        // ✅ 任何人偵測到聲音 → 廣播給所有人重置冷場計時（不含逐字稿內容，純粹只是「有人在講話」的訊號）
        public async Task BroadcastSpeech(string roomCode)
            => await Clients.Group(roomCode).SendAsync("SpeechDetected");

        // 🎯 修正：把某人辨識到的逐字稿「內容」廣播給房間裡其他人 —— 每個人的 SpeechRecognition 只能聽到自己的麥克風，
        //    之前只廣播了一個空訊號（BroadcastSpeech），沒有真的傳文字內容，導致主持人只看得到自己說的話。
        //    這裡送給「其他人」就好（自己已經在本機 push 過一次了，不用再收自己的）
        public async Task BroadcastTranscript(string roomCode, string speaker, string text, string time)
            => await Clients.OthersInGroup(roomCode).SendAsync("TranscriptReceived", speaker, text, time);

        // ── 內部小工具：確認呼叫者是不是「這個房間」受邀的最高主管，是的話回傳 Room，不是就回傳 null ──
        private async Task<Room?> GetAuthorizedHostRoomAsync(string roomCode)
        {
            var httpContext = Context.GetHttpContext();
            if (httpContext == null) return null;

            var sessionMemberId = httpContext.Session.GetInt32("MemberId");
            var sessionRole = httpContext.Session.GetString("MemberRole")?.ToLower();
            if (sessionMemberId == null || sessionRole != "director") return null;

            var room = await _db.Rooms.FirstOrDefaultAsync(r => r.JitsiRoomName == roomCode);
            if (room == null) return null;

            var isDirectorOfThisRoom = await _db.RoomParticipants.AnyAsync(p =>
                p.RoomId == room.Id
                && p.Role == ParticipantRole.Director
                && p.EmployeeId == sessionMemberId.Value);

            return isDirectorOfThisRoom ? room : null;
        }

        // ── 內部小工具：把這個房間裡所有求職者的履歷，面試狀態一起推進 ──
        private async Task UpdateJobseekerInterviewStatusAsync(int roomId, string interviewStatus, string? admissionResult)
        {
            var resumeIds = await _db.RoomParticipants
                .Where(p => p.RoomId == roomId && p.Role == ParticipantRole.Jobseeker && p.ResumeId != null)
                .Select(p => p.ResumeId!.Value)
                .ToListAsync();

            if (resumeIds.Count == 0) return;

            var resumes = await _db.Resumes.Where(r => resumeIds.Contains(r.Id)).ToListAsync();
            foreach (var resume in resumes)
            {
                resume.InterviewStatus = interviewStatus;
                if (admissionResult != null) resume.AdmissionResult = admissionResult;
            }
        }
    }
}
