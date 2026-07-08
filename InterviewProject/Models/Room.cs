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
        public DateTime? StartAt { get; set; }          // 開放進入時間（null = 立即可用）
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