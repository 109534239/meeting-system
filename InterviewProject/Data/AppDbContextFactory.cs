using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace InterviewProject.Data
{
    public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext(string[] args)
        {
            var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();

            optionsBuilder.UseNpgsql(
                "Host=dpg-d81qu5f7f7vs73edko5g-a.singapore-postgres.render.com;Port=5432;Database=interviewdb_wrny;Username=interviewuser;Password=XO2vvyQaHNWrV5lYmHbN0SQOitHeizff;SSL Mode=Require;Trust Server Certificate=true");

            return new AppDbContext(optionsBuilder.Options);
        }
    }
}