using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Worklane.Infrastructure.Multitenancy;

namespace Worklane.Infrastructure.Persistence;

/// <summary>
/// Lets <c>dotnet ef</c> build the DbContext without spinning up the full WebApi host
/// (which otherwise fails validating unrelated Identity/DataProtection services at design time).
/// <c>ConnectionStrings__Default</c> wins when it is set, so the beta migrator role is used
/// on the VM. The migrations bundle calls this factory and must not be passed
/// <c>--connection</c>, which would put the password in the process argv.
/// With the variable unset, local <c>dotnet ef</c> keeps the dev connection string.
/// </summary>
public class WorklaneDbContextFactory : IDesignTimeDbContextFactory<WorklaneDbContext>
{
    public const string LocalDevConnectionString =
        "Host=localhost;Port=5432;Database=worklane;Username=worklane;Password=worklane";

    public WorklaneDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = LocalDevConnectionString;

        var optionsBuilder = new DbContextOptionsBuilder<WorklaneDbContext>();
        optionsBuilder.UseNpgsql(connectionString);

        return new WorklaneDbContext(optionsBuilder.Options, new CurrentTenantService());
    }
}
