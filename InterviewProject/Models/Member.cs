using System;

namespace InterviewProject.Models
{
    public class Member
    {
        public int Id { get; set; }

        // 所有欄位改為必填，不可為 null
        public string Name { get; set; } = "";
        public string Email { get; set; } = "";
        public string Phone { get; set; } = "";
        public string PasswordHash { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public string Gender { get; set; } = "";
        public DateOnly Birthday { get; set; } 
        public string Address { get; set; } = "";

    }
}