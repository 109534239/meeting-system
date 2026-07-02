using System.ComponentModel.DataAnnotations.Schema;

namespace InterviewProject.Models
{
    public class MajorRequired
    {
        public int Id { get; set; }

        [ForeignKey("Job")]
        public int JobsId { get; set; }
        public virtual Job? Job { get; set; }

        // 資料庫欄位名稱是 "MajorRequired"（跟表名一樣），
        // C# 屬性用 Major 避免跟類別名稱撞在一起造成混淆
        [Column("MajorRequired")]
        public string Major { get; set; } = "";
    }
}
