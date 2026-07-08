using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace InterviewProject.Models
{
    // 進場狀態：受邀但還沒進來 / 已核准可進 / 會議開始後申請中待核准 / 被拒絕
    public static class ParticipantStatus
    {
        public const string Invited = "Invited";
        public const string Admitted = "Admitted";
        public const string Pending = "Pending";
        public const string Denied = "Denied";
    }

    // 在這場會議裡的角色
    public static class ParticipantRole
    {
        public const string Jobseeker = "Jobseeker";
        public const string Manager = "Manager";     // 部門主管
        public const string Director = "Director";   // 部門最高主管（主持人）
        public const string AI = "AI";                // AI 面試官
    }

    [Table("RoomParticipants")]
    public class RoomParticipant
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public int RoomId { get; set; }
        [ForeignKey("RoomId")]
        public virtual Room? Room { get; set; }

        [Required]
        public string Role { get; set; } = ParticipantRole.Jobseeker;

        // 求職者用 ResumeId（同時能反查是哪個 Member、哪個 Job）
        public int? ResumeId { get; set; }
        [ForeignKey("ResumeId")]
        public virtual Resume? Resume { get; set; }

        // 主管/最高主管/HR 用 EmployeeId
        public int? EmployeeId { get; set; }
        [ForeignKey("EmployeeId")]
        public virtual Employee? Employee { get; set; }

        [Required]
        public string Status { get; set; } = ParticipantStatus.Invited;

        public DateTime? JoinedAt { get; set; }
        public DateTime? LeftAt { get; set; }
    }
}