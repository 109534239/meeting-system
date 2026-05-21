namespace InterviewProject.Models
{
    public class Member
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Email { get; set; } = "";
        public string Phone { get; set; } = "";
        public string PasswordHash { get; set; } = "";
        public string Role { get; set; } = "jobseeker"; // jobseeker / employee
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // Profile 額外欄位
        public string? Gender { get; set; }
        public DateOnly? Birthday { get; set; }
        public string? Address { get; set; }
    }
}