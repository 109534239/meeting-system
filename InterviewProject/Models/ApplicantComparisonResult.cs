
namespace InterviewProject.Models
{
    public class ApplicantComparisonResult
    {
        public int Version { get; set; } = 2;

        public List<ApplicantRecommendation> Recommendations
        { get; set; } = new();

        public string Report { get; set; } = "";
    }

    public class ApplicantRecommendation
    {
        public int ResumeId { get; set; }

        public string Recommendation { get; set; } = "";

        public string Reason { get; set; } = "";

        public string InterviewFocus { get; set; } = "";
    }
}
