using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace InterviewProject.Models
{
    [Table("Resume")]
    public class Resume
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public string UserId { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Gender { get; set; }
        public string? IdNumber { get; set; }
        public DateTime? Birthday { get; set; }
        public string? ZipCode { get; set; }
        public string? Address { get; set; }
        public string? MaritalStatus { get; set; }
        public string? MilitaryService { get; set; }
        public string? Phone1 { get; set; }
        public string? Phone2 { get; set; }
        public string? Mobile { get; set; }
        public string? EduLevel { get; set; }
        public string? SchoolName { get; set; }
        public string? Major { get; set; }
        public string? EduStatus { get; set; }
        public string? EduDate { get; set; }
        public string? Email { get; set; }
        public int WorkExperienceYears { get; set; }
        public string? CompanyName { get; set; }
        public string? JobTitle { get; set; }
        public string? JobDescription { get; set; }
        public string? Salary { get; set; }

        // 這些欄位將接收 JavaScript 串接後的長字串
        public string? LanguageSkills { get; set; }
        public string? DriverLicense { get; set; } // 確保名稱與 SQL 一致
        public string? Specialty { get; set; }
        public string? Certificates { get; set; }
        public string? ComputerSkills { get; set; }
        public string? Autobiography { get; set; }
        public DateTime ResumeTime { get; set; }
        public string? Position { get; set; }
        

    }
}