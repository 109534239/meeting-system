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

        public DbSet<User> Users { get; set; }
        public DbSet<Room> Rooms { get; set; }
        public DbSet<RoomMember> RoomMembers { get; set; }
        public DbSet<Attendance> Attendances { get; set; }
        public DbSet<Member> Members { get; set; }
        public DbSet<Job> Jobs { get; set; }
        public DbSet<Resume> Resume { get; set; }
        public DbSet<Resume> Resumes { get; set; }

        public DbSet<Employee> Employees { get; set; }
    }
}