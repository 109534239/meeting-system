using System.ComponentModel.DataAnnotations.Schema;

namespace InterviewProject.Models
{
    public class Room
    {
        public int Id { get; set; }
        public string RoomName { get; set; } = string.Empty;
        public string JitsiRoomName { get; set; } = string.Empty;
        public DateTime CreatedTime { get; set; } = DateTime.Now;

        // ✅ 新增：會議時間控制
        // ScheduledAt：職缺自動排程時算出的「預計面試時間」（隔天），用來決定候選人/主管最早什麼時候能打開這個房間頁面
        // StartAt：主持人「真的按下開始會議」的當下時間，在那之前是 null
        public DateTime? ScheduledAt { get; set; }
        public DateTime? StartAt { get; set; }          // 主持人實際按下開始會議的時間（null = 還沒開始）
        public DateTime? EndAt { get; set; }            // 關閉時間（null = 不限）
        public bool IsActive { get; set; } = true;      // 手動開關

        // 🎯 Step A 新增：這場面試對應哪個職缺
        public int? JobsId { get; set; }
        [ForeignKey("JobsId")]
        public virtual Job? Job { get; set; }

        // 🎯 Step A 新增：會議目前的階段
        // NotStarted：還沒開始（知道代碼就能進）
        // InProgress：主持人已開始（之後進場要等候核准）
        // Ended：已結束
        public string MeetingStatus { get; set; } = "NotStarted";

        // 🎯 這場面試的主持人（HR 建立會議室時從該部門職員中指定一位，面試開始前可改）。
        //    關聯到 Employees 表；RoomParticipants 裡 Role=Director 那一列會同步維持一致（會議程式讀的是那一列）。
        public int? HostEmployeeId { get; set; }

        // 主持人在面試後「送出錄取結果」並通知HR的時間（null = 還沒送出）
        public DateTime? ResultSubmittedAt { get; set; }
        [ForeignKey("HostEmployeeId")]
        public virtual Employee? HostEmployee { get; set; }

        // 🎯 JaaS 伺服器端錄影完成後，透過 Webhook 收到的下載連結（24小時內有效）
        public string? RecordingUrl { get; set; }

        // 🎯 存進 wwwroot 資料夾後的檔名，讓「面試評分」頁面可以直接連到對應檔案，不用用猜的
        public string? TranscriptFileName { get; set; }
        public string? RecordingFileName { get; set; }
        public string? AiAnalysisFileName { get; set; }

        // 🐛 這輪新增：AI 面試官加入會議室（Playwright）如果失敗，之前是整個吃案——
        //    只印在伺服器 console，資料庫、前端使用者都完全看不到，導致「AI 沒進會議室」
        //    跟「錄影其實沒有錄到」這兩件事，使用者只能憑空猜測，畫面上還會誤導人地顯示「已完成上傳」。
        //    這裡把失敗原因存下來，「結束會議」畫面才有辦法誠實反映實際狀況，而不是無條件宣告成功。
        //    AI 面試官成功加入時會清成 null；每次開始會議都會被覆寫成最新一次的結果。
        public string? AiBotErrorMessage { get; set; }

        public virtual ICollection<RoomParticipant> Participants { get; set; } = new List<RoomParticipant>();

        // 判斷房間目前是否可進入
        //   🐛 修正兩個問題：
        //   1. 原本只看 StartAt/EndAt 的時間比對，完全沒看 MeetingStatus，
        //      會出現「MeetingStatus 已經是 Ended，但因為時間比對邏輯又判斷成尚未開放」的矛盾畫面
        //   2. 原本用 StartAt 判斷「是否到了可以打開頁面的時間」，但 StartAt 在主持人真正按下開始之前一直是 null，
        //      這個判斷式在會議開始前形同虛設（永遠不會擋人），實際上應該要用 ScheduledAt（預計面試時間）來卡
        public bool CanEnter()
        {
            if (!IsActive) return false;
            if (MeetingStatus == "Ended") return false;

            var now = DateTime.Now;
            // 會議還沒開始（NotStarted）時，用「預計面試時間」卡進場時機；已經 InProgress 就不用再卡了
            if (MeetingStatus == "NotStarted" && ScheduledAt.HasValue && now < ScheduledAt.Value) return false;
            if (EndAt.HasValue && now > EndAt.Value) return false;
            return true;
        }

        // 人性化狀態文字
        public string StatusText()
        {
            if (!IsActive) return "已關閉";
            var now = DateTime.Now;
            if (StartAt.HasValue && now < StartAt.Value)
                return $"尚未開放（{StartAt.Value:MM/dd HH:mm} 後可進入）";
            // 還沒開始、且還沒到預計面試時間：要跟 CanEnter() 的判斷一致，不能顯示「進行中」
            if (MeetingStatus == "NotStarted" && ScheduledAt.HasValue && now < ScheduledAt.Value)
                return $"尚未開放（{ScheduledAt.Value:MM/dd HH:mm} 後可進入）";
            if (EndAt.HasValue && now > EndAt.Value)
                return "已結束";
            return "進行中";
        }
    }
}