using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace InterviewProject.Models
{
    // 主管/最高主管針對某位求職者這場面試的評分評語，一人一份（同一個人不能重複評同一位求職者）
    [Table("InterviewEvaluations")]
    public class InterviewEvaluation
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public int RoomId { get; set; }
        [ForeignKey("RoomId")]
        public virtual Room? Room { get; set; }

        [Required]
        public int ResumeId { get; set; }
        [ForeignKey("ResumeId")]
        public virtual Resume? Resume { get; set; }

        [Required]
        public int EvaluatorEmployeeId { get; set; }
        [ForeignKey("EvaluatorEmployeeId")]
        public virtual Employee? EvaluatorEmployee { get; set; }

        [Required]
        [Range(1, 5)]
        public int Score { get; set; }

        public string? Comment { get; set; }

        [Required]
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }
}
