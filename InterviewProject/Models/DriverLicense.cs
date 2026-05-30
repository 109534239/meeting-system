using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace InterviewProject.Models
{
    [Table("DriverLicense")]
    public class DriverLicense
    {
        [Key]
        public int Id { get; set; }

        public int ResumeId { get; set; }

        public string Driver { get; set; } = string.Empty;

        public string Type { get; set; } = string.Empty;

        // 導覽屬性 (選填)
        [ForeignKey("ResumeId")]
        public virtual Resume? Resume { get; set; }
    }
}