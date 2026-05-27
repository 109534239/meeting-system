using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace InterviewProject.Models
{
    [Table("Resume")]
    public class Resume
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public int UserId { get; set; }
        public string? MaritalStatus { get; set; }
        public string? MilitaryService { get; set; }
        public string? Phone1 { get; set; }
        public string? Phone2 { get; set; }
        public string? Mobile { get; set; }
        public string? EduLevel { get; set; }
        public string? SchoolName { get; set; }
        public string? Major { get; set; }
        public string? EduStatus { get; set; }

        // 🎯 貼心修正：將型態由 string? 改為 DateTime?，才能完美接收前端 input type="month" 的日期
        public DateTime? EduDate { get; set; }

        public int? WorkExperienceYears { get; set; }
        public string? CompanyName { get; set; }
        public string? JobTitle { get; set; }
        public string? JobDescription { get; set; }

        // 這些欄位將接收 JavaScript 串接後的長字串
        public string? LanguageSkills { get; set; }
        public string? DriverLicense { get; set; }
        public string? Specialty { get; set; }
        public string? Certificates { get; set; }
        public string? ComputerSkills { get; set; }
        public string? Autobiography { get; set; }
        public DateTime ResumeTime { get; set; }

        // 🎯 核心修正一：將 Position 的型態由 string? 改為 int，並聲明它是 Job 的外鍵
        [ForeignKey("Job")]
        public int Position { get; set; }

        // 🎯 核心修正二：建立與 Job 模型實體的虛擬關聯，供前端 @Model.Job?.Title 撈取名稱
        public virtual Job? Job { get; set; }

        public string Status { get; set; } = "待審核";
    }
}