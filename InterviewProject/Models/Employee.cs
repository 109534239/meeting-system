namespace InterviewProject.Models
{
    public class Employee
    {
        public int Id { get; set; }
        public string Account { get; set; } = "";      // 員工編號或Email
        public string PasswordHash { get; set; } = ""; // SHA256 加密
        public string Name { get; set; } = "";         // 顯示名稱
        public string Role { get; set; } = "hr";       // hr / manager
        public string? JobTitle { get; set; }
        public string? Department { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}