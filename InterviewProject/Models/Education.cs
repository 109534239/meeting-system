using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace InterviewProject.Models
{
    // 🎯 學歷子表：一筆履歷可對應多筆學歷（比照 Job 的 MajorRequired / LanguageRequired 正規化寫法）
    [Table("Education")]
    public class Education
    {
        [Key]
        public int Id { get; set; }
        public int ResumeId { get; set; }

        public string EduLevel { get; set; } = string.Empty;    // 學歷別：博士/碩士/大學...
        public string SchoolName { get; set; } = string.Empty;
        public string Major { get; set; } = string.Empty;
        public string EduStatus { get; set; } = string.Empty;   // 畢業/肄業/在學

        // 🎯 原本只有「年月」（單一畢業日期），改成起訖日期
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }

        // 顯示順序：第一筆（0）視為最高學歷，比照 Specialties 的 SortOrder 用法
        public int SortOrder { get; set; }

        // 導覽屬性 (選填)
        [ForeignKey("ResumeId")]
        public virtual Resume? Resume { get; set; }
    }
}
