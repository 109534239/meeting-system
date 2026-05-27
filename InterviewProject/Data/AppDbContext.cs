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
        public DbSet<Member> Members { get; set; }
        public DbSet<Job> Jobs { get; set; }
        public DbSet<Resume> Resume { get; set; }
        public DbSet<LanguageProficiency> LanguageProficiency { get; set; }
        public DbSet<Resume> Resumes { get; set; }
        public DbSet<VerificationCode> VerificationCodes { get; set; }
        public DbSet<Employee> Employees { get; set; }
        public DbSet<Announcement> Announcements { get; set; }
        public DbSet<InterviewSchedule> InterviewSchedules { get; set; }
    }
}