using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Worklane.Infrastructure.Persistence;

namespace Worklane.IntegrationTests.Infrastructure;

public class DesignTimeDbContextFactoryTests
{
    [Fact]
    public void UsesConnectionStringFromEnvironmentWhenSet()
    {
        const string connection =
            "Host=postgres;Port=5432;Database=worklane;Username=worklane_migrator;Password=not-the-dev-password";
        var previous = Environment.GetEnvironmentVariable("ConnectionStrings__Default");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", connection);
        try
        {
            using var db = new WorklaneDbContextFactory().CreateDbContext([]);
            db.Database.GetConnectionString().Should().Be(connection);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__Default", previous);
        }
    }

    [Fact]
    public void UsesLocalDevConnectionStringOnlyInDevelopment()
    {
        var previousConn = Environment.GetEnvironmentVariable("ConnectionStrings__Default");
        var previousEnv = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", null);
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
        try
        {
            using var db = new WorklaneDbContextFactory().CreateDbContext([]);
            db.Database.GetConnectionString().Should().Be(WorklaneDbContextFactory.LocalDevConnectionString);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__Default", previousConn);
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previousEnv);
        }
    }

    [Fact]
    public void ThrowsWhenConnectionStringMissingOutsideDevelopment()
    {
        var previousConn = Environment.GetEnvironmentVariable("ConnectionStrings__Default");
        var previousEnv = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", null);
        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", null);
            var unset = () => new WorklaneDbContextFactory().CreateDbContext([]);
            unset.Should().Throw<InvalidOperationException>();

            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production");
            var production = () => new WorklaneDbContextFactory().CreateDbContext([]);
            production.Should().Throw<InvalidOperationException>();
        }
        finally
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__Default", previousConn);
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previousEnv);
        }
    }
}
