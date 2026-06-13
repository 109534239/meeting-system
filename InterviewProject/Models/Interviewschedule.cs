using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace InterviewProject.Models
{
    public class InterviewSchedule
    {
        [Key]
        public int Id { get; set; }

        // 需求 1：已刪除 MemberId 與 JobId，完全由 ResumeId 關聯取得
        public int ResumeId { get; set; }
        public int ScheduledByEmployeeId { get; set; }

        // 需求 2 & 4：已刪除 Notes 與 ScheduledAt
        public int? RoomId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public string? ResultNote { get; set; }

        // 需求 6：新增分數欄位
        public int? ResultScore { get; set; }

        // 導覽屬性 (Navigation Properties)
        [ForeignKey("ResumeId")]
        public virtual Resume? Resume { get; set; }

        [ForeignKey("RoomId")]
        public virtual Room? Room { get; set; }

        [ForeignKey("ScheduledByEmployeeId")]
        public virtual Member? ScheduledByEmployee { get; set; }
    }
}