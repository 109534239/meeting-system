using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace InterviewProject.Models
{
    public class Job
    {
        public int Id { get; set; }
        public string Title { get; set; } = "";
        public string Department { get; set; } = "";
        public string Location { get; set; } = "";
        public string JobType { get; set; } = "fulltime";
        public string Description { get; set; } = "";
        public string Requirements { get; set; } = "";
        public int Salary { get; set; } = 0;
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public int CreatedBy { get; set; }
        public Member? Creator { get; set; }

        [NotMapped]
        public string[] TagList => !string.IsNullOrEmpty(Requirements)
            ? Requirements.Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries)
            : Array.Empty<string>();
    }
}