using DocumentFormat.OpenXml.Spreadsheet;
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
        public int MembersId { get; set; } // 這是實際在資料庫的欄位

        [ForeignKey("MembersId")]
        public virtual Member? Member { get; set; } // 這是導覽屬性
        public string? MaritalStatus { get; set; }
        public string? MilitaryService { get; set; }
        public string? Phone1 { get; set; }

        // 🎯 學歷改為多筆子表（比照 Job 的 MajorRequired / LanguageRequired），
        //    不再是單一 EduLevel/SchoolName/Major/EduStatus/EduDate 欄位
        public virtual ICollection<Education> Educations { get; set; } = new List<Education>();
        [Required]
        public string ContactAddress { get; set; } = "";

        public int? WorkExperienceYears { get; set; }
        // 🎯 工作經歷改為多筆子表（比照 Educations），不再是單一 CompanyName/JobTitle/JobDescription 欄位
        public virtual ICollection<WorkExperience> WorkExperiences { get; set; } = new List<WorkExperience>();

        // 🎯 作品集：多筆子表（比照 Educations / WorkExperiences），每筆有說明、連結、上傳檔案，
        //    未新增任何一筆即視為「無作品集」
        public virtual ICollection<Portfolio> Portfolios { get; set; } = new List<Portfolio>();

        // 這些欄位將接收 JavaScript 串接後的長字串

        [NotMapped] // 🎯 關鍵！告訴 EF：這個屬性在資料庫裡「沒有」對應欄位，不要去 SQL 撈它
        public string? LanguageSkills { get; set; }
        [NotMapped]
        public string? DriverLicense { get; set; }
        [NotMapped]
        public string? Specialty { get; set; }
        [NotMapped]
        public string? Certificates { get; set; }
        [NotMapped]
        public string? ComputerSkills { get; set; }
        public string? Autobiography { get; set; }
        public DateTime ResumeTime { get; set; }

        // 🎯 核心修正一：將 JobsId 的型態由 string? 改為 int，並聲明它是 Job 的外鍵
        [ForeignKey("Job")]
        public int JobsId { get; set; }

        // 🎯 核心修正二：建立與 Job 模型實體的虛擬關聯，供前端 @Model.Job?.Title 撈取名稱
        public virtual Job? Job { get; set; }

        public string? Status { get; set; }
        // 🎯 核心關鍵：加入對應面試資料表的導覽屬性
        // 假設你的面試模型叫 Interview，一筆履歷對應一場面試（或是多場，這裡用單數為例）
        //public virtual Interview? Interview { get; set; }

        public int? AiScore { get; set; }
        public string? AiComment { get; set; }
    }
}