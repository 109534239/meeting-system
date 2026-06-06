using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace InterviewProject.Models
{
    [Table("Specialties")]
    public class Specialties
    {
        [Key]
        public int Id { get; set; } // 檢查這個名稱是否與資料庫一致
        public int ResumeId { get; set; }
        public string Specialty { get; set; } = string.Empty;
        public int SortOrder { get; set; }

        // 導覽屬性 (選填)
        [ForeignKey("ResumeId")]
        public virtual Resume? Resume { get; set; }
    }
}