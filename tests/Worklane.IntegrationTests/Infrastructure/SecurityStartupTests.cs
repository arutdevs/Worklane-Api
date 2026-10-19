using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Worklane.Application.Auth.Dtos;
using Worklane.Application.Common.Options;
using Worklane.Infrastructure.Identity;
using Worklane.WebApi.Startup;

namespace Worklane.IntegrationTests.Infrastructure;

public class JwtSigningKeyTests
{
    [Fact]
    public void CommittedKey_IsRejected_AndTheMessageOmitsTheKey()
    {
        var secret = CommittedSecrets.Value;
        var fresh = Key(64);
        var leaked = Assert.Throws<InvalidOperationException>(() => JwtSigningKeys.Create(Settings(secret)));
        var whitespace = Assert.Throws<InvalidOperationException>(() => JwtSigningKeys.Create(Settings(secret.Insert(8, "\n"))));
        var asPrevious = Assert.Throws<InvalidOperationException>(() => JwtSigningKeys.Create(Settings(fresh, secret)));

        AssertNoKey(leaked.ToString(), secret);
        AssertNoKey(whitespace.ToString(), secret);
        AssertNoKey(asPrevious.ToString(), secret);
        leaked.Message.Should().Contain(CommittedSecrets.Fingerprint);
        whitespace.Message.Should().Contain(CommittedSecrets.Fingerprint);
        asPrevious.Message.Should().Contain("Jwt:PreviousSecrets[0]");
        JwtSigningKeys.Create(Settings(fresh)).Current.KeyId
            .Should().Be(JwtSigningKeys.Fingerprint(Convert.FromBase64String(fresh)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingKey_IsRejected(string secret)
    {
        Assert.Throws<InvalidOperationException>(() => JwtSigningKeys.Create(Settings(secret)))
            .Message.Should().Contain("Jwt:Secret is missing");
    }

    [Fact]
    public void ShortOrInvalidKey_IsRejected_WithoutEchoingTheValue()
    {
        var shortKey = Key(31);
        const string invalid = "not-a-jwt-signing-key!!";
        var tooShort = Assert.Throws<InvalidOperationException>(() => JwtSigningKeys.Create(Settings(shortKey)));
        var notBase64 = Assert.Throws<InvalidOperationException>(() => JwtSigningKeys.Create(Settings(invalid)));
        tooShort.Message.Should().Contain("at least 32 bytes");
        notBase64.Message.Should().Contain("not valid base64");
        AssertNoKey(tooShort.ToString(), shortKey);
        if (notBase64.ToString().Contains(invalid, StringComparison.Ordinal))
            throw new Xunit.Sdk.XunitException("Failure text contained the rejected signing key.");

        JwtSigningKeys.Create(Settings(Key(32))).Current.Key.Should().HaveCount(32);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public void Startup_RejectsCommittedKey_AndDoesNotLogIt(string environment)
    {
        using var keys = new TempDirectory();
        var secret = CommittedSecrets.Value;
        using var factory = ProbeFactory.Create(environment, new Dictionary<string, string?>
        {
            ["Jwt:Secret"] = secret,
            ["DataProtection:KeysPath"] = keys.Path
        });
        var failure = StartFailure(factory);
        AssertNoKey(failure, secret);
        failure.Should().Contain("leaked signing key").And.Contain(CommittedSecrets.Fingerprint);
    }

    [Fact]
    public async Task Startup_AcceptsFreshKey_AndPersistsDataProtectionKeys()
    {
        using var keys = new TempDirectory();
        using var factory = ProbeFactory.Create("Production", new Dictionary<string, string?>
        {
            ["Jwt:Secret"] = Key(64),
            ["Jwt:Issuer"] = "Worklane",
            ["Jwt:Audience"] = "WorklaneClient",
            ["DataProtection:KeysPath"] = keys.Path
        });
        using var client = factory.CreateClient();
        (await client.GetAsync("/api/workspaces")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var payload = factory.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("wl").Protect("payload");
        Directory.GetFiles(keys.Path, "*.xml").Should().NotBeEmpty();
        Protector(keys.Path, DataProtectionRegistration.ApplicationName).Unprotect(payload).Should().Be("payload");
        var act = () => Protector(keys.Path, "Other").Unprotect(payload);
        act.Should().Throw<CryptographicException>();
    }

    internal static string StartFailure(WebApplicationFactory<Program> factory)
    {
        try
        {
            using var client = factory.CreateClient();
        }
        catch (Exception ex)
        {
            return Flatten(ex);
        }

        throw new Xunit.Sdk.XunitException("Host started.");
    }

    private static string Flatten(Exception ex)
    {
        var text = new StringBuilder();
        var seen = new HashSet<Exception>();
        void Walk(Exception? current)
        {
            if (current is null || !seen.Add(current))
                return;
            text.AppendLine(current.Message);
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                    Walk(inner);
            }

            Walk(current.InnerException);
        }

        Walk(ex);
        return text.ToString();
    }

    private static JwtSettings Settings(string secret, params string[] previous) => new()
    {
        Secret = secret,
        PreviousSecrets = previous,
        Issuer = "Worklane",
        Audience = "WorklaneClient"
    };

    internal static string Key(int bytes) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes));

    private static IDataProtector Protector(string path, string applicationName)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(path)).SetApplicationName(applicationName);
        return services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>().CreateProtector("wl");
    }

    private static void AssertNoKey(string text, string secret)
    {
        if (text.Contains(secret, StringComparison.Ordinal))
            throw new Xunit.Sdk.XunitException("Failure text contained the signing key.");
        if (text.Contains(Convert.ToHexString(Convert.FromBase64String(secret)), StringComparison.OrdinalIgnoreCase))
            throw new Xunit.Sdk.XunitException("Failure text contained the key bytes.");
    }
}

public class DataProtectionStartupTests
{
    [Fact]
    public void Production_WithoutPath_Fails()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Add(Environments.Production, "  "));
        ex.Message.Should().Contain("DataProtection:KeysPath is not set")
            .And.Contain("/var/lib/worklane/dataprotection-keys");
    }

    [Fact]
    public void Production_Startup_WithoutPath_Fails()
    {
        using var factory = ProbeFactory.Create(Environments.Production, new Dictionary<string, string?>
        {
            ["Jwt:Secret"] = JwtSigningKeyTests.Key(64),
            ["DataProtection:KeysPath"] = ""
        });
        JwtSigningKeyTests.StartFailure(factory).Should().Contain("DataProtection:KeysPath is not set");
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void LocalEnvironments_MayOmitPath(string environment)
    {
        var services = Add(environment, null);
        if (environment != "Testing")
            return;

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("wl").Protect("ok").Should().NotBe("ok");
    }

    [Fact]
    public void MissingOrReadOnlyDirectory_Fails()
    {
        var missing = Path.Combine(Path.GetTempPath(), "worklane-missing-" + Guid.NewGuid().ToString("N"));
        Assert.Throws<InvalidOperationException>(() => Add(Environments.Production, missing))
            .Message.Should().Contain("does not exist").And.Contain(missing);

        using var dir = new TempDirectory();
        UnixModes.Set(dir.Path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Assert.Throws<InvalidOperationException>(() => Add(Environments.Production, dir.Path))
                .Message.Should().Contain("not writable").And.Contain(dir.Path);
        }
        finally
        {
            UnixModes.Set(dir.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static IServiceCollection Add(string environment, string? path)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [DataProtectionRegistration.KeysPathKey] = path
        }).Build();
        services.AddWorklaneDataProtection(new HostingEnvironment { EnvironmentName = environment }, configuration);
        return services;
    }
}

public class JwtRotationTests : IClassFixture<WorklaneWebApplicationFactory>
{
    private readonly WorklaneWebApplicationFactory _factory;

    public JwtRotationTests(WorklaneWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public void Factory_DoesNotUseTheCommittedKey()
    {
        var committed = CommittedSecrets.Value;
        if (_factory.JwtSecret == committed || _factory.PreviousJwtSecret == committed)
            throw new Xunit.Sdk.XunitException("Test host is using the committed signing key.");
    }

    [Fact]
    public async Task PreviousKey_IsAccepted_UnknownKey_IsRejected_AndKidMatches()
    {
        var client = _factory.CreateClient();
        var email = $"rotate-{Guid.NewGuid():N}@example.com";
        var registered = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(email, "Password123!", "Ada"));
        registered.EnsureSuccessStatusCode();
        var tokens = await registered.Content.ReadFromJsonAsync<AuthTokensResponse>();
        new JsonWebTokenHandler().ReadJsonWebToken(tokens!.AccessToken).Kid
            .Should().Be(JwtSigningKeys.Fingerprint(Convert.FromBase64String(_factory.JwtSecret)));

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Mint(_factory.PreviousJwtSecret));
        (await client.GetAsync("/api/workspaces")).StatusCode.Should().Be(HttpStatusCode.OK);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Mint(JwtSigningKeyTests.Key(64)));
        (await client.GetAsync("/api/workspaces")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static string Mint(string secret)
    {
        var bytes = Convert.FromBase64String(secret);
        var key = new SymmetricSecurityKey(bytes) { KeyId = JwtSigningKeys.Fingerprint(bytes) };
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString()),
                new Claim(JwtRegisteredClaimNames.Email, "person@example.com")
            ]),
            Expires = DateTime.UtcNow.AddMinutes(5),
            Issuer = "Worklane",
            Audience = "WorklaneClient",
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256)
        });
    }
}

/// <summary>
/// Host configuration is copied onto the entry-point command line, so it wins over appsettings.
/// </summary>
file sealed class ProbeFactory : WebApplicationFactory<Program>
{
    private readonly string _environment;
    private readonly Dictionary<string, string?> _settings;

    private ProbeFactory(string environment, Dictionary<string, string?> settings)
    {
        _environment = environment;
        _settings = settings;
    }

    public static ProbeFactory Create(string environment, Dictionary<string, string?> settings) => new(environment, settings);

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var values = new Dictionary<string, string?>(_settings) { [HostDefaults.EnvironmentKey] = _environment };
        builder.ConfigureHostConfiguration(config => config.AddInMemoryCollection(values));
        return base.CreateHost(builder);
    }
}

file static class CommittedSecrets
{
    public static string Value { get; } = Read();
    public static string Fingerprint { get; } = JwtSigningKeys.Fingerprint(Convert.FromBase64String(Value));

    private static string Read()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Worklane.slnx")))
            root = root.Parent;
        if (root is null)
            throw new InvalidOperationException("Could not locate the repository root.");

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root.FullName, "src/Worklane.WebApi/appsettings.Development.json")));
        var fromJson = doc.RootElement.GetProperty("Jwt").GetProperty("Secret").GetString();
        if (string.IsNullOrEmpty(fromJson))
            throw new InvalidOperationException("Development Jwt:Secret is empty.");

        var compose = File.ReadAllText(Path.Combine(root.FullName, "docker-compose.yml"));
        var index = compose.IndexOf("Jwt__Secret:", StringComparison.Ordinal);
        if (index < 0)
            throw new InvalidOperationException("docker-compose.yml has no Jwt__Secret entry.");
        var line = compose[index..].Split('\n', 2)[0];
        var start = line.IndexOf('"');
        var end = start < 0 ? -1 : line.IndexOf('"', start + 1);
        if (start < 0 || end < 0)
            throw new InvalidOperationException("Jwt__Secret is not a quoted literal.");

        var fromCompose = line[(start + 1)..end];
        if (!string.Equals(
                JwtSigningKeys.Fingerprint(Convert.FromBase64String(fromJson)),
                JwtSigningKeys.Fingerprint(Convert.FromBase64String(fromCompose)),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Committed JWT secrets do not share one key fingerprint.");

        return fromJson;
    }
}

file static class UnixModes
{
    public static void Set(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, mode);
    }
}

file sealed class TempDirectory : IDisposable
{
    public TempDirectory() => Path = Directory.CreateTempSubdirectory("wl-dp-").FullName;
    public string Path { get; }

    public void Dispose()
    {
        if (!Directory.Exists(Path))
            return;
        try
        {
            UnixModes.Set(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (IOException)
        {
        }

        Directory.Delete(Path, recursive: true);
    }
}
