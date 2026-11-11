using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Worklane.Application.Auth.Dtos;
using Worklane.Application.Common.Options;
using Worklane.Infrastructure.Identity;
using Worklane.Infrastructure.Persistence;
using Worklane.IntegrationTests.Infrastructure;

namespace Worklane.IntegrationTests.Auth;

public class LockoutSettingsTests
{
    [Fact]
    public void Defaults_AreFiveAttempts_AndFifteenMinutes()
    {
        var settings = new LockoutSettings();
        settings.MaxFailedAccessAttempts.Should().Be(5);
        settings.DefaultLockoutTimeSpan.Should().Be(TimeSpan.FromMinutes(15));
        settings.AllowedForNewUsers.Should().BeTrue();
        LockoutSettings.SectionName.Should().Be("Lockout");
    }

    [Fact]
    public void AppSettings_BindTheSameValues()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Worklane.slnx")))
            root = root.Parent;
        root.Should().NotBeNull();
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(root!.FullName, "src/Worklane.WebApi/appsettings.json")));
        var section = json.RootElement.GetProperty("Lockout");
        section.GetProperty("MaxFailedAccessAttempts").GetInt32().Should().Be(5);
        section.GetProperty("DefaultLockoutTimeSpan").GetString().Should().Be("00:15:00");
        section.GetProperty("AllowedForNewUsers").GetBoolean().Should().BeTrue();
    }
}

public class LoginLockoutTests : IClassFixture<WorklaneWebApplicationFactory>
{
    private const string Password = "Password123!";
    private const string WrongPassword = "WrongPassword1";

    private readonly WorklaneWebApplicationFactory _factory;

    public LoginLockoutTests(WorklaneWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Identity_UsesTheConfiguredLockout_AndTheStoreSupportsIt()
    {
        var identity = _factory.Services.GetRequiredService<IOptions<IdentityOptions>>().Value.Lockout;
        identity.MaxFailedAccessAttempts.Should().Be(5);
        identity.DefaultLockoutTimeSpan.Should().Be(TimeSpan.FromMinutes(15));
        identity.AllowedForNewUsers.Should().BeTrue();

        await using var scope = _factory.Services.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        manager.SupportsUserLockout.Should().BeTrue();
    }

    [Fact]
    public async Task FiveFailures_LockTheAccount_WithTheSameBodyAsAWrongPassword_AndSuccessResetsTheCount()
    {
        var user = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        await SetLockoutEnabledAsync(user.Tokens.User.Id, false);

        var first = await LoginAsync(user.Client, user.Tokens.User.Email, WrongPassword);
        first.Status.Should().Be(HttpStatusCode.Unauthorized);
        var wrongBody = first.Body;

        var afterFirst = await ReadUserAsync(user.Tokens.User.Id);
        afterFirst.LockoutEnabled.Should().BeTrue();
        afterFirst.AccessFailedCount.Should().Be(1);

        for (var attempt = 0; attempt < 4; attempt++)
        {
            var failed = await LoginAsync(user.Client, user.Tokens.User.Email, WrongPassword);
            failed.Status.Should().Be(HttpStatusCode.Unauthorized);
            failed.Body.Should().Be(wrongBody);
        }

        var locked = await ReadUserAsync(user.Tokens.User.Id);
        locked.LockoutEnd.Should().NotBeNull();
        locked.LockoutEnd!.Value.Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(10));
        locked.AccessFailedCount.Should().Be(0);

        var duringLockout = await LoginAsync(user.Client, user.Tokens.User.Email, Password);
        duringLockout.Status.Should().Be(HttpStatusCode.Unauthorized);
        duringLockout.Body.Should().Be(wrongBody);
        duringLockout.Body.Should().NotContain("lock");

        var unknown = await LoginAsync(user.Client, $"missing-{Guid.NewGuid():N}@example.com", Password);
        unknown.Status.Should().Be(HttpStatusCode.Unauthorized);
        unknown.Body.Should().Be(wrongBody);

        var stillLocked = await ReadUserAsync(user.Tokens.User.Id);
        stillLocked.LockoutEnd.Should().Be(locked.LockoutEnd);

        var fresh = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        for (var attempt = 0; attempt < 4; attempt++)
            (await LoginAsync(fresh.Client, fresh.Tokens.User.Email, WrongPassword)).Status.Should().Be(HttpStatusCode.Unauthorized);

        var recovered = await LoginAsync(fresh.Client, fresh.Tokens.User.Email, Password);
        recovered.Status.Should().Be(HttpStatusCode.OK, recovered.Body);
        (await ReadUserAsync(fresh.Tokens.User.Id)).AccessFailedCount.Should().Be(0);

        (await LoginAsync(fresh.Client, fresh.Tokens.User.Email, WrongPassword)).Status.Should().Be(HttpStatusCode.Unauthorized);
        var afterReset = await LoginAsync(fresh.Client, fresh.Tokens.User.Email, Password);
        afterReset.Status.Should().Be(HttpStatusCode.OK, afterReset.Body);
    }

    private async Task SetLockoutEnabledAsync(Guid userId, bool enabled)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
        var user = await db.Users.SingleAsync(row => row.Id == userId);
        user.LockoutEnabled = enabled;
        await db.SaveChangesAsync();
    }

    private async Task<AppUser> ReadUserAsync(Guid userId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
        return await db.Users.AsNoTracking().SingleAsync(row => row.Id == userId);
    }

    private static async Task<LoginResult> LoginAsync(HttpClient client, string email, string password)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password));
        var body = await response.Content.ReadAsStringAsync();
        return new LoginResult(response.StatusCode, body);
    }

    private sealed record LoginResult(HttpStatusCode Status, string Body);
}

public sealed class ShortLockoutFactory : WorklaneWebApplicationFactory
{
    public static readonly TimeSpan Lockout = TimeSpan.FromSeconds(4);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Lockout:DefaultLockoutTimeSpan"] = Lockout.ToString("c"),
            });
        });
    }
}

public class LoginLockoutExpiryTests : IClassFixture<ShortLockoutFactory>
{
    private const string Password = "Password123!";
    private const string WrongPassword = "WrongPassword1";

    private readonly ShortLockoutFactory _factory;

    public LoginLockoutExpiryTests(ShortLockoutFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task AfterTheConfiguredLockout_TheCorrectPasswordWorksAgain()
    {
        var identity = _factory.Services.GetRequiredService<IOptions<IdentityOptions>>().Value.Lockout;
        identity.DefaultLockoutTimeSpan.Should().Be(ShortLockoutFactory.Lockout);
        identity.MaxFailedAccessAttempts.Should().Be(5);

        var user = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        string? wrongBody = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var failed = await LoginAsync(user.Client, user.Tokens.User.Email, WrongPassword);
            failed.Status.Should().Be(HttpStatusCode.Unauthorized);
            wrongBody ??= failed.Body;
            failed.Body.Should().Be(wrongBody);
        }

        var locked = await LoginAsync(user.Client, user.Tokens.User.Email, Password);
        locked.Status.Should().Be(HttpStatusCode.Unauthorized);
        locked.Body.Should().Be(wrongBody);

        await Task.Delay(ShortLockoutFactory.Lockout + TimeSpan.FromMilliseconds(750));

        var unlocked = await LoginAsync(user.Client, user.Tokens.User.Email, Password);
        unlocked.Status.Should().Be(HttpStatusCode.OK, unlocked.Body);
    }

    private static async Task<LoginResult> LoginAsync(HttpClient client, string email, string password)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password));
        var body = await response.Content.ReadAsStringAsync();
        return new LoginResult(response.StatusCode, body);
    }

    private sealed record LoginResult(HttpStatusCode Status, string Body);
}

public sealed class CountingPasswordHasher : IPasswordHasher<AppUser>
{
    private readonly PasswordHasher<AppUser> _inner = new();
    private readonly object _gate = new();
    private readonly List<string> _verifiedHashes = [];

    public void Reset()
    {
        lock (_gate)
            _verifiedHashes.Clear();
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate)
            return _verifiedHashes.ToArray();
    }

    public string HashPassword(AppUser user, string password) => _inner.HashPassword(user, password);

    public PasswordVerificationResult VerifyHashedPassword(AppUser user, string hashedPassword, string providedPassword)
    {
        lock (_gate)
            _verifiedHashes.Add(hashedPassword);
        return _inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
    }
}

public sealed class CountingHasherFactory : WorklaneWebApplicationFactory
{
    public CountingPasswordHasher Hasher { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IPasswordHasher<AppUser>>();
            services.AddSingleton(Hasher);
            services.AddSingleton<IPasswordHasher<AppUser>>(sp => sp.GetRequiredService<CountingPasswordHasher>());
        });
    }
}

public class LoginLockoutHardeningTests : IClassFixture<CountingHasherFactory>
{
    private const string Password = "Password123!";
    private const string WrongPassword = "WrongPassword1";
    private const int ParallelBurst = 40;

    private readonly CountingHasherFactory _factory;

    public LoginLockoutHardeningTests(CountingHasherFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task UnknownAndLockedLogins_VerifyAPasswordHash_AndKeepTheWrongPasswordBody()
    {
        var user = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        var wrong = await LoginAsync(user.Client, user.Tokens.User.Email, WrongPassword);
        wrong.Status.Should().Be(HttpStatusCode.Unauthorized);
        var storedHash = await PasswordHashAsync(user.Tokens.User.Id);

        _factory.Hasher.Reset();
        var unknown = await LoginAsync(user.Client, $"missing-{Guid.NewGuid():N}@example.com", WrongPassword);
        unknown.Status.Should().Be(HttpStatusCode.Unauthorized);
        unknown.Body.Should().Be(wrong.Body);
        var unknownHashes = _factory.Hasher.Snapshot();
        unknownHashes.Should().ContainSingle();
        unknownHashes[0].Should().NotBe(storedHash);

        for (var attempt = 0; attempt < 4; attempt++)
            (await LoginAsync(user.Client, user.Tokens.User.Email, WrongPassword)).Status.Should().Be(HttpStatusCode.Unauthorized);

        _factory.Hasher.Reset();
        var locked = await LoginAsync(user.Client, user.Tokens.User.Email, Password);
        locked.Status.Should().Be(HttpStatusCode.Unauthorized);
        locked.Body.Should().Be(wrong.Body);
        locked.Body.Should().NotContain("lock");
        var lockedHashes = _factory.Hasher.Snapshot();
        lockedHashes.Should().ContainSingle();
        lockedHashes[0].Should().NotBe(storedHash);
    }

    [Fact]
    public async Task ParallelWrongPasswords_CountEveryAttempt_AndStopVerifyingTheRealHashAtTheLimit()
    {
        var identity = _factory.Services.GetRequiredService<IOptions<IdentityOptions>>().Value.Lockout;
        var max = identity.MaxFailedAccessAttempts;
        var below = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        var wrong = await LoginAsync(below.Client, $"missing-{Guid.NewGuid():N}@example.com", WrongPassword);

        _factory.Hasher.Reset();
        var counted = await Task.WhenAll(Enumerable.Range(0, max - 1).Select(_ =>
            LoginAsync(below.Client, below.Tokens.User.Email, WrongPassword)));
        counted.Should().OnlyContain(result => result.Status == HttpStatusCode.Unauthorized && result.Body == wrong.Body);
        var belowHash = await PasswordHashAsync(below.Tokens.User.Id);
        GuessesAgainst(belowHash).Should().Be(max - 1);
        var belowRow = await ReadUserAsync(below.Tokens.User.Id);
        belowRow.AccessFailedCount.Should().Be(max - 1);
        belowRow.LockoutEnd.Should().BeNull();

        var burst = await TestAuthHelper.RegisterAsync(_factory.CreateClient());
        _factory.Hasher.Reset();
        var results = await Task.WhenAll(Enumerable.Range(0, ParallelBurst).Select(_ =>
            LoginAsync(burst.Client, burst.Tokens.User.Email, WrongPassword)));
        results.Should().OnlyContain(result => result.Status == HttpStatusCode.Unauthorized && result.Body == wrong.Body);
        var burstHash = await PasswordHashAsync(burst.Tokens.User.Id);
        GuessesAgainst(burstHash).Should().BeLessThanOrEqualTo(max).And.Be(max);
        var burstRow = await ReadUserAsync(burst.Tokens.User.Id);
        burstRow.LockoutEnd.Should().NotBeNull();
        burstRow.LockoutEnd!.Value.Should().BeAfter(DateTimeOffset.UtcNow);
        burstRow.AccessFailedCount.Should().Be(0);
    }

    private int GuessesAgainst(string passwordHash) =>
        _factory.Hasher.Snapshot().Count(hash => hash == passwordHash);

    private async Task<string> PasswordHashAsync(Guid userId) =>
        (await ReadUserAsync(userId)).PasswordHash!;

    private async Task<AppUser> ReadUserAsync(Guid userId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
        return await db.Users.AsNoTracking().SingleAsync(row => row.Id == userId);
    }

    private static async Task<LoginResult> LoginAsync(HttpClient client, string email, string password)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password));
        var body = await response.Content.ReadAsStringAsync();
        return new LoginResult(response.StatusCode, body);
    }

    private sealed record LoginResult(HttpStatusCode Status, string Body);
}
