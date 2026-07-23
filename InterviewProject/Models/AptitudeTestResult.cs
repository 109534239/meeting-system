using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace InterviewProject.Models
{
    // 一份履歷只會有一筆測驗紀錄（作答一次即完成，不可重測）
    [Table("AptitudeTestResults")]
    public class AptitudeTestResult
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public int ResumeId { get; set; }
        [ForeignKey("ResumeId")]
        public virtual Resume? Resume { get; set; }

        // JSON 陣列：[{"QuestionId":1,"Score":4}, ...]
        [Required]
        public string AnswersJson { get; set; } = "[]";

        // 各構面平均分數，方便 HR 之後查看，不做及格/不及格判斷，完成即通過
        public double StressToleranceScore { get; set; }
        public double TeamworkScore { get; set; }
        public double ProactivenessScore { get; set; }
        public double ReliabilityScore { get; set; }
        public double CommunicationScore { get; set; }

        [Required]
        public DateTime SubmittedAt { get; set; } = DateTime.Now;
    }
}
