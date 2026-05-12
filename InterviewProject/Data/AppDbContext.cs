namespace 面試.Data
{
    using Microsoft.EntityFrameworkCore;
    using 面試.Models;

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
    }
}
