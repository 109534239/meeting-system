using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace InterviewProject.Models
{
    [Table("LanguageProficiency")]
    public class LanguageProficiency
    {
        [Key]
        public int Id { get; set; }

        public int ResumeId { get; set; }

        public string Language { get; set; } = string.Empty;

        public string Degree { get; set; } = string.Empty;

        // 導覽屬性 (選填)
        [ForeignKey("ResumeId")]
        public virtual Resume? Resume { get; set; }
    }
}