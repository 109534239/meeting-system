using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace InterviewProject.Models
{
    // 🎯 工作經歷子表：一筆履歷可對應多筆工作經歷（比照 Education 的正規化寫法）
    //    「工作總年資」仍然是 Resume.WorkExperienceYears 這個單一欄位，不拆進這張子表，
    //    這張表只放「每一段」公司/職稱/工作說明。
    [Table("WorkExperience")]
    public class WorkExperience
    {
        [Key]
        public int Id { get; set; }
        public int ResumeId { get; set; }

        public string CompanyName { get; set; } = string.Empty;
        public string JobTitle { get; set; } = string.Empty;
        public string JobDescription { get; set; } = string.Empty;

        // 🎯 新增：起訖日期（比照 Education 的 StartDate/EndDate 寫法）
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }

        // 顯示順序：第一筆（0）視為最相關/最近的工作經歷
        public int SortOrder { get; set; }

        // 導覽屬性 (選填)
        [ForeignKey("ResumeId")]
        public virtual Resume? Resume { get; set; }
    }
}
