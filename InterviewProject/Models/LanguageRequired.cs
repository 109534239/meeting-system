using System.ComponentModel.DataAnnotations.Schema;

namespace InterviewProject.Models
{
    public class LanguageRequired
    {
        public int Id { get; set; }

        [ForeignKey("Job")]
        public int JobsId { get; set; }
        public virtual Job? Job { get; set; }

        public string Language { get; set; } = ""; 
        public string Degree { get; set; } = ""; 
    }
}
