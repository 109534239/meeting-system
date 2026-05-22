using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace InterviewProject.Models
{
    public class Job
    {
        public int Id { get; set; }

        public string Title { get; set; }

        public string Department { get; set; }

        public string Location { get; set; }

        public string JobType { get; set; }

        public string Description { get; set; }

        public string Requirements { get; set; }

        public int Salary { get; set; }

        public bool IsActive { get; set; }

        public DateTime CreatedAt { get; set; }

        public DateTime UpdatedAt { get; set; }

        // 修改這裡：將 string 改成 int
        public int CreatedBy { get; set; }

        [NotMapped]
        public string[] TagList => !string.IsNullOrEmpty(Requirements)
            ? Requirements.Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries)
            : Array.Empty<string>();
    }
}