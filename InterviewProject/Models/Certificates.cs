using DocumentFormat.OpenXml.Spreadsheet;
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace InterviewProject.Models
{
    [Table("Certificates")]
    public class Certificates
    {
        [Key]
        public int Id { get; set; } // 檢查這個名稱是否與資料庫一致
        public int ResumeId { get; set; }
        public string CName { get; set; } = string.Empty;
        public string Levels { get; set; } = string.Empty;
        // 導覽屬性 (選填)
        [ForeignKey("ResumeId")]
        public virtual Resume? Resume { get; set; }
    }
}