using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace InterviewProject.Models
{
    // 🎯 作品集子表：比照 Education / WorkExperience 的模式（一筆履歷可有多筆作品集），
    //    每筆有「說明、連結、上傳檔案」三個欄位，跟 Specialty 那種單一字串清單不同，
    //    所以獨立成一張表，而不是塞進 Resume 的某個欄位。
    [Table("Portfolio")]
    public class Portfolio
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public int ResumeId { get; set; }

        [ForeignKey("ResumeId")]
        public virtual Resume? Resume { get; set; }

        public string? Title { get; set; }

        public string? Description { get; set; }

        public string? Link { get; set; }

        // 🎯 存的是實體檔案在 wwwroot 底下的相對路徑（例如 /uploads/portfolio/xxx.pdf），
        //    不是檔案本身內容，檔案實體另外存在伺服器磁碟上
        public string? FilePath { get; set; }

        public int SortOrder { get; set; } = 0;
    }
}
