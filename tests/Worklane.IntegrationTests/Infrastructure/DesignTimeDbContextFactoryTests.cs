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
    public void KeepsLocalDevConnectionStringWhenEnvironmentIsUnset()
    {
        var previous = Environment.GetEnvironmentVariable("ConnectionStrings__Default");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", null);
        try
        {
            using var db = new WorklaneDbContextFactory().CreateDbContext([]);
            db.Database.GetConnectionString().Should().Be(WorklaneDbContextFactory.LocalDevConnectionString);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__Default", previous);
        }
    }
}
