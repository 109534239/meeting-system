using System.ComponentModel.DataAnnotations.Schema;

namespace InterviewProject.Models
{
    public class SkillTag
    {
        public int Id { get; set; }

        [ForeignKey("Job")]
        public int JobsId { get; set; }
        public virtual Job? Job { get; set; }

        [Column("SkillTag")]
        public string Tag { get; set; } = "";
    }
}
