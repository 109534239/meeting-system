using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace InterviewProject.Models
{
    [Table("Resume")] // 確保對應 PostgreSQL 的 Resume 資料表
    public class Resume
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public int MembersId { get; set; }

        // ── 基本聯絡資訊 ──
        public string? MaritalStatus { get; set; }
        public string? MilitaryService { get; set; }
        public string? Phone1 { get; set; }
        public string? Phone2 { get; set; }
        public string? Mobile { get; set; }

        // ── 學歷學群 ──
        public string? EduLevel { get; set; }
        public string? SchoolName { get; set; }
        public string? Major { get; set; }
        public string? EduStatus { get; set; }
        public DateTime? EduDate { get; set; }

        // ── 工作經歷 ──
        public int? WorkExperienceYears { get; set; }
        public string? CompanyName { get; set; }
        public string? JobTitle { get; set; }
        public string? JobDescription { get; set; }

        // ── 專長與自傳 ──

        // 🎯 核心修正：加上 [NotMapped]，這樣 EF Core 就不會去 PostgreSQL 撈這個欄位，徹底解決 42703 錯誤！
        [NotMapped]
        public string? Specialty { get; set; }

        public string? Certificates { get; set; }
        public string? Autobiography { get; set; }
        public DateTime ResumeTime { get; set; }

        // ── 串接前台 JavaScript 長字串的非資料庫暫存欄位 ──
        [NotMapped] public string? LanguageSkills { get; set; }
        [NotMapped] public string? DriverLicense { get; set; }
        [NotMapped] public string? ComputerSkills { get; set; }

        // ── 核心外鍵關聯：對齊你提供的 Job 實體 ──
        [ForeignKey("Job")]
        public int JobsId { get; set; }

        // 🔗 導覽屬性：讓控制器中的 .Include(r => r.Job) 能夠完美發揮 JOIN 作用
        public virtual Job? Job { get; set; }

        // ── AI 評審與狀態欄位 ──
        public string Status { get; set; } = "待審核";
        public int? AiScore { get; set; }
        public string? AiComment { get; set; }
    }
}