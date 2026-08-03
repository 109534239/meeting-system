namespace InterviewProject.Models
{
    public class VerificationCode
    {
        public int Id { get; set; }
        public int MemberId { get; set; }
        public string? Code { get; set; }
        public bool IsUsed { get; set; }
    }
}
