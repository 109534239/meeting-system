namespace InterviewProject.Models
{
    public class FAQ
    {
        public int Id { get; set; }

        public string Question { get; set; } = "";

        public string Answer { get; set; } = "";

        public int SortOrder { get; set; } = 0;

        public bool IsActive { get; set; } = true;

        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }
}