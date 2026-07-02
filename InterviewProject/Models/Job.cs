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
        public string ExperienceRequired { get; set; } = "";
        public string EducationRequired { get; set; } = "bachelor";
        public string IndustryExperience { get; set; } = "";
        public string CertRequired { get; set; } = "";
        public string OtherRequirements { get; set; } = "";

        // 薪資範圍
        public int SalaryMin { get; set; } = 0;
        public int SalaryMax { get; set; } = 0;

        // 🎯 主管資訊：外鍵指向 Employees.Name（不是 Employees.Id）
        //    對應設定寫在 AppDbContext.OnModelCreating 裡（用 HasPrincipalKey 指定參考 Name 欄位）
        public string EmployeesName { get; set; } = "";
        public virtual Employee? Manager { get; set; }

        // 截止日期
        public DateTime Deadline { get; set; } = DateTime.UtcNow.AddDays(30);

        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        // 🎯 正規化後的一對多關聯
        public virtual ICollection<MajorRequired> MajorRequirements { get; set; } = new List<MajorRequired>();
        public virtual ICollection<LanguageRequired> LanguageRequirements { get; set; } = new List<LanguageRequired>();
        public virtual ICollection<SkillTag> SkillTags { get; set; } = new List<SkillTag>();

        // ── 不存 DB，查詢時計算 ──
        [NotMapped]
        public int NewApplicationsCount { get; set; } = 0;

        [NotMapped]
        public int TotalApplicationsCount { get; set; } = 0;

        // ── 方便前端顯示用的小工具（不存 DB）──
        [NotMapped]
        public string[] TagList => SkillTags?.Select(t => t.Tag).ToArray() ?? Array.Empty<string>();

        [NotMapped]
        public string[] MajorList => MajorRequirements?.Select(m => m.Major).ToArray() ?? Array.Empty<string>();
    }
}