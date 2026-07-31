using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("MARKETPLACEHUB_MIGRATION_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=marketplacehub;Username=marketplacehub;Password=development-only";
        return new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);
    }
}
