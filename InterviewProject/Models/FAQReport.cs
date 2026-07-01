namespace InterviewProject.Models
{
    public class FAQReport
    {
        public int Id { get; set; }

        public string Name { get; set; } = "";

        public string Email { get; set; } = "";

        public string Category { get; set; } = "";

        public string Subject { get; set; } = "";

        public string Content { get; set; } = "";

        public string Status { get; set; } = "待處理";

        public int? AssignedEmployeeId { get; set; }

        public string? AssignedRole { get; set; }

        public string? ReplyContent { get; set; }

        public string? InternalNote { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public DateTime? RepliedAt { get; set; }
    }
}