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
        public string WorkShift { get; set; } = "day";
        public string LeavePolicy { get; set; } = "twodays";
        public string HeadCount { get; set; } = "";
        public string Description { get; set; } = "";
        public string Requirements { get; set; } = "";  // 知識技能
        public string ExperienceRequired { get; set; } = "";
        public string EducationRequired { get; set; } = "bachelor";
        public string IndustryExperience { get; set; } = "";
        public string MajorRequired { get; set; } = "";
        public string LanguageRequired { get; set; } = "";
        public string CertRequired { get; set; } = "";
        public string OtherRequirements { get; set; } = "";
        public string SkillTags { get; set; } = "";     // 逗號分隔，前台標籤用

        // 薪資範圍（取代原本單一 Salary）
        public int SalaryMin { get; set; } = 0;
        public int SalaryMax { get; set; } = 0;

        // 主管資訊（存 DB）
        public string ManagerName { get; set; } = "";
        public string ReportToName { get; set; } = "";

        // 截止日期（存 DB）
        public DateTime Deadline { get; set; } = DateTime.UtcNow.AddDays(30);

        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public int CreatedBy { get; set; }
        public Member? Creator { get; set; }

        // ── 不存 DB，查詢時計算 ──
        [NotMapped]
        public int NewApplicationsCount { get; set; } = 0;

        [NotMapped]
        public int TotalApplicationsCount { get; set; } = 0;

        [NotMapped]
        public string[] TagList => !string.IsNullOrEmpty(SkillTags)
            ? SkillTags.Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries)
            : Array.Empty<string>();
    }
}