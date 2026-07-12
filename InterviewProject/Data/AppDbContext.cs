namespace InterviewProject.Data
{
    using Microsoft.EntityFrameworkCore;
    using InterviewProject.Models;

    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options)
            : base(options)
        {
        }

        public DbSet<Room> Rooms { get; set; }
        public DbSet<RoomParticipant> RoomParticipants { get; set; }
        public DbSet<Member> Members { get; set; }
        public DbSet<Job> Jobs { get; set; }
        public DbSet<LanguageProficiency> LanguageProficiency { get; set; }
        public DbSet<DriverLicense> DriverLicense { get; set; }
        public DbSet<ComputerSkills> ComputerSkills { get; set; }
        public DbSet<Resume> Resumes { get; set; }
        public DbSet<Education> Educations { get; set; } // 🎯 履歷學歷子表（一對多）
        public DbSet<WorkExperience> WorkExperiences { get; set; } // 🎯 履歷工作經歷子表（一對多）
        public DbSet<Portfolio> Portfolios { get; set; } // 🎯 履歷作品集子表（一對多）
        public DbSet<VerificationCode> VerificationCodes { get; set; }
        public DbSet<Specialties> Specialties { get; set; }
        public DbSet<Certificatecategories> Certificatecategories { get; set; }
        public DbSet<Certificates> Certificates { get; set; }
        public DbSet<Employee> Employees { get; set; }
        public DbSet<Announcement> Announcements { get; set; }
        public DbSet<InterviewSchedule> InterviewSchedules { get; set; }
        public DbSet<FAQ> Faqs { get; set; }
        public DbSet<FAQReport> FAQReports { get; set; }

        // 🎯 Job 表正規化後新增的三張子表
        public DbSet<MajorRequired> MajorRequired { get; set; }
        public DbSet<LanguageRequired> LanguageRequired { get; set; }
        public DbSet<SkillTag> SkillTags { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // 🎯 Employees.Name 必須是唯一值，才能被 Jobs.EmployeesName 當作外鍵目標
            modelBuilder.Entity<Employee>()
                .HasIndex(e => e.Name)
                .IsUnique();

            // 🎯 Job.EmployeesName 外鍵指向 Employees.Name（不是 Employees.Id），
            //    所以要用 HasPrincipalKey 明確指定參考的是 Name 欄位
            modelBuilder.Entity<Job>()
                .HasOne(j => j.Manager)
                .WithMany()
                .HasForeignKey(j => j.EmployeesName)
                .HasPrincipalKey(e => e.Name)
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}