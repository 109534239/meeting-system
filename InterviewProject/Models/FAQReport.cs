namespace InterviewProject.Models
{
    public class FAQReport
    {
        public int Id { get; set; }

        public string Role { get; set; } = "訪客";

        public string Name { get; set; } = "";

        public string Email { get; set; } = "";

        public string Category { get; set; } = "";

        public string Subject { get; set; } = "";

        public string Content { get; set; } = "";

        public string Status { get; set; } = "待處理";

        public string? ReplyContent { get; set; }

        public string? Department { get; set; }
        
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public DateTime? RepliedAt { get; set; }
    }
}