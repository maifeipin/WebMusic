using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace WebMusic.Backend.Data;

/// <summary>
/// Keeps EF migration generation offline. The connection is never opened by
/// tooling; production configuration remains owned by Program.cs.
/// </summary>
public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=5432;Database=webmusic_design;Username=postgres;Password=design-time-only")
            .Options;
        return new AppDbContext(options);
    }
}
