namespace InterviewProject.Models
{
    public class FAQReport
    {
        public int Id { get; set; }

        public string Role { get; set; } = "訪客";

        // 🎯 新增：求職者登入後送出 Q&A 時，寫入自己的 MemberId，
        //    之後才能用「登入者身份」查出「與自己相關」的 Q&A。
        //    訪客（未登入）送出的 Q&A 這欄會是 null。
        //    ⚠️ 請同步修改「求職者送出 Q&A」的 Create 動作：
        //       report.MemberId = HttpContext.Session.GetInt32("MemberId");
        public int? MemberId { get; set; }

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