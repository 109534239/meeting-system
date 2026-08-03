namespace InterviewProject.Models
{
    public class Announcement
    {
        public int Id { get; set; }
        public string Title { get; set; } = "";
        public string Content { get; set; } = "";
        public string Category { get; set; } = "公告"; // 最新 / 公告 / 活動
        public DateTime SDate { get; set; } = DateTime.UtcNow;
        public DateTime CDate { get; set; } = DateTime.UtcNow;
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}