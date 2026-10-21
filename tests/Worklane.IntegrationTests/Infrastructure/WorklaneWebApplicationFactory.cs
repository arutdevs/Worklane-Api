using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

    public string DataProtectionKeysPath { get; } = Path.Combine(Path.GetTempPath(), "worklane-tests", "dp-" + Guid.NewGuid().ToString("N"));

    public string JwtSecret { get; } = NewKey();

    public string PreviousJwtSecret { get; } = NewKey();

    public const long TestMaxUploadBytes = 2048;

    public WorklaneWebApplicationFactory()
    {
        Directory.CreateDirectory(AttachmentPath);
        Directory.CreateDirectory(DataProtectionKeysPath);
    }

    private static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
    }

    public new async Task DisposeAsync()
    {
        await _postgres.DisposeAsync();
        if (Directory.Exists(AttachmentPath))
            Directory.Delete(AttachmentPath, recursive: true);
        if (Directory.Exists(DataProtectionKeysPath))
            Directory.Delete(DataProtectionKeysPath, recursive: true);
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
                ["FileStorage:MaxBytes"] = TestMaxUploadBytes.ToString(),
                ["Jwt:Secret"] = JwtSecret,
                ["Jwt:PreviousSecrets:0"] = PreviousJwtSecret,
                ["DataProtection:KeysPath"] = DataProtectionKeysPath,
                // The suite shares one client IP per factory. Production stays at RateLimit:Auth.
                ["RateLimit:Auth:PermitLimit"] = "10000",
                ["RateLimit:Auth:WindowSeconds"] = "60"
            });
        });

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<WorkspaceInsertGate>();
            services.AddSingleton<ISaveChangesInterceptor>(sp => sp.GetRequiredService<WorkspaceInsertGate>());
        });
    }
}
