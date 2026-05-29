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

        public string Gender { get; set; } = "";

        // 🎯 成功新增：身分證字號（順序在性別後面，且為必填）
        public string IdNumber { get; set; } = "";

        // 🎯 新增：儲存照片於伺服器上的路徑或檔名
        public string ProfileImagePath { get; set; } = "";

        public DateOnly Birthday { get; set; }
        public string Address { get; set; } = "";

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}