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
                "Host=dpg-d81pedg3kofs73cuuqjg-a.singapore-postgres.render.com;Port=5432;Database=interviewdb_3gme;Username=interviewuser;Password=ZSxnwDVUOfM6w7h0d0Tvm8FvXdk0Z6Rs;SSL Mode=Require;Trust Server Certificate=true");

            return new AppDbContext(optionsBuilder.Options);
        }
    }
}