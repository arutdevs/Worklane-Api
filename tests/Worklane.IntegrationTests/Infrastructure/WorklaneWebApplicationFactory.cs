using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Testcontainers.PostgreSql;

namespace Worklane.IntegrationTests.Infrastructure;

public class WorklaneWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("worklane_test")
        .WithUsername("worklane")
        .WithPassword("worklane")
        .Build();

    /// <summary>
    /// Per-fixture attachment directory, and a small cap so tests can prove the 413
    /// path without writing a 10 MB body. Production default stays 10 MB.
    /// </summary>
    public string AttachmentPath { get; } = Path.Combine(Path.GetTempPath(), "worklane-tests", Guid.NewGuid().ToString("N"));

    public const long TestMaxUploadBytes = 2048;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
    }

    public new async Task DisposeAsync()
    {
        await _postgres.DisposeAsync();
        if (Directory.Exists(AttachmentPath))
            Directory.Delete(AttachmentPath, recursive: true);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = _postgres.GetConnectionString(),
                ["FileStorage:LocalPath"] = AttachmentPath,
                ["FileStorage:MaxBytes"] = TestMaxUploadBytes.ToString()
            });
        });
    }
}
