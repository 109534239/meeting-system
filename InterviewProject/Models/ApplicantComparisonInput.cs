
namespace InterviewProject.Models
{
    public class ApplicantComparisonInput
    {
        public string JobTitle { get; set; } = "";

        public string JobDescription { get; set; } = "";

        public string JobRequirements { get; set; } = "";

        public List<ApplicantComparisonCandidate> Candidates
        { get; set; } = new();
    }

    public class ApplicantComparisonCandidate
    {
        public int ResumeId { get; set; }

        public string MemberName { get; set; } = "";

        public int AiScore { get; set; }

        public string AiComment { get; set; } = "";
    }
}
