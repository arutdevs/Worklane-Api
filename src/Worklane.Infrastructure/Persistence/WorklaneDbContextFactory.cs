using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Worklane.Infrastructure.Multitenancy;

namespace Worklane.Infrastructure.Persistence;

/// <summary>
/// Lets `dotnet ef migrations` build the DbContext without spinning up the full WebApi host/DI
/// (which otherwise fails validating unrelated Identity/DataProtection services at design time).
/// </summary>
public class WorklaneDbContextFactory : IDesignTimeDbContextFactory<WorklaneDbContext>
{
    public WorklaneDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<WorklaneDbContext>();
        optionsBuilder.UseNpgsql("Host=localhost;Port=5432;Database=worklane;Username=worklane;Password=worklane");

        return new WorklaneDbContext(optionsBuilder.Options, new CurrentTenantService());
    }
}
