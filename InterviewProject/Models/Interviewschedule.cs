using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace InterviewProject.Models
{
    public class InterviewSchedule
    {
        [Key]
        public int Id { get; set; }

        // 關聯求職者（Members 表）
        [Required]
        public int MemberId { get; set; }
        public virtual Member? Member { get; set; }

        // 關聯履歷（Resume 表）
        [Required]
        public int ResumeId { get; set; }
        public virtual Resume? Resume { get; set; }

        // 關聯職缺（Jobs 表）
        [Required]
        public int JobId { get; set; }
        public virtual Job? Job { get; set; }

        // 安排的 HR 或主管（Employees 表）
        [Required]
        public int ScheduledByEmployeeId { get; set; }
        public virtual Employee? ScheduledByEmployee { get; set; }

        // 面試時間
        [Required]
        public DateTime ScheduledAt { get; set; }

        // 備註
        public string? Notes { get; set; }

        // 面試會議室（關聯 Rooms 表）
        public int? RoomId { get; set; }
        public virtual Room? Room { get; set; }

        // 面試狀態：待確認 / 已確認 / 已完成 / 已取消
        public string Status { get; set; } = "待確認";

        // 建立時間
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        // 結果備註（面試後填寫）
        public string? ResultNote { get; set; }
    }
}