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

        // 🎯 JaaS 伺服器端錄影完成後，透過 Webhook 收到的下載連結（24小時內有效）
        public string? RecordingUrl { get; set; }

        public virtual ICollection<RoomParticipant> Participants { get; set; } = new List<RoomParticipant>();

        // 判斷房間目前是否可進入
        public bool CanEnter()
        {
            if (!IsActive) return false;
            var now = DateTime.Now;
            if (StartAt.HasValue && now < StartAt.Value) return false;
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
            if (EndAt.HasValue && now > EndAt.Value)
                return "已結束";
            return "進行中";
        }
    }
}