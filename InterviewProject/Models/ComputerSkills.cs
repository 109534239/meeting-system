using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace InterviewProject.Models
{
    [Table("ComputerSkills")]
    public class ComputerSkills
    {
        [Key]
        public int Id { get; set; } // 檢查這個名稱是否與資料庫一致
        public int ResumeId { get; set; }
        public string ComputerSkill { get; set; }

        // 導覽屬性 (選填)
        [ForeignKey("ResumeId")]
        public virtual Resume? Resume { get; set; }
    }
}