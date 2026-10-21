using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging;
using Worklane.Application.Common.Options;
using Worklane.Infrastructure.Identity;
using Worklane.WebApi.Startup;

namespace Worklane.IntegrationTests.Infrastructure;

public class JwtSigningKeyTests
{
    [Fact]
    public async Task GeneratedKey_IsAccepted_AndAMarkedFingerprintIsRejectedWithoutEchoingTheKey()
    {
        var secret = Key(48);
        var fresh = Key(64);
        var fingerprint = JwtSigningKeys.Fingerprint(Convert.FromBase64String(secret));
        JwtSigningKeys.LeakedFingerprints.Add(fingerprint);
        try
        {
            var leaked = Assert.Throws<InvalidOperationException>(() => JwtSigningKeys.Create(Settings(secret)));
            var whitespace = Assert.Throws<InvalidOperationException>(() => JwtSigningKeys.Create(Settings(secret.Insert(8, "\n"))));
            var asPrevious = Assert.Throws<InvalidOperationException>(() => JwtSigningKeys.Create(Settings(fresh, secret)));

            AssertNoKey(string.Concat(leaked, whitespace, asPrevious), secret);
            leaked.Message.Should().Contain(fingerprint);
            whitespace.Message.Should().Contain(fingerprint);
            asPrevious.Message.Should().Contain("Jwt:PreviousSecrets[0]");
        }
        finally
        {
            JwtSigningKeys.LeakedFingerprints.Remove(fingerprint);
        }

        JwtSigningKeys.Create(Settings(fresh)).Current.KeyId
            .Should().Be(JwtSigningKeys.Fingerprint(Convert.FromBase64String(fresh)));
        JwtSigningKeys.LeakedFingerprints.Should().Contain(
            "6478a52cc5de53f75fc5b15ccda93afa869c601533c7b5c6894d963e0a839ff4");

        var host = new WorklaneWebApplicationFactory();
        await host.DisposeAsync();
        Convert.FromBase64String(host.JwtSecret).Length.Should().BeGreaterThanOrEqualTo(JwtSigningKeys.MinimumBytes);
        host.JwtSecret.Should().NotBe(host.PreviousJwtSecret);
    }

    [Fact]
    public void Repo_DoesNotCommitASigningKey()
    {
        var root = RepoRoot.Find();
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "src/Worklane.WebApi/appsettings.Development.json")));
        var fromJson = doc.RootElement.GetProperty("Jwt").GetProperty("Secret").GetString();
        string.IsNullOrEmpty(fromJson).Should().BeTrue();

        var compose = File.ReadAllText(Path.Combine(root, "docker-compose.yml"));
        compose.Should().Contain("Jwt__Secret: \"${JWT_SIGNING_KEY:?}\"");
        var example = File.ReadAllText(Path.Combine(root, ".env.example"));
        example.Should().Contain("JWT_SIGNING_KEY=");
        foreach (var line in example.Split('\n'))
        {
            if (!line.StartsWith("JWT_SIGNING_KEY=", StringComparison.Ordinal))
                continue;
            line["JWT_SIGNING_KEY=".Length..].Trim().Should().BeEmpty();
        }
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
    public void Startup_RejectsGeneratedLeakedKey_AndDoesNotLogIt(string environment)
    {
        var secret = Key(48);
        var fingerprint = JwtSigningKeys.Fingerprint(Convert.FromBase64String(secret));
        JwtSigningKeys.LeakedFingerprints.Add(fingerprint);
        try
        {
            using var keys = new TempDirectory();
            using var factory = ProbeFactory.Create(environment, new Dictionary<string, string?>
            {
                ["Jwt:Secret"] = secret,
                ["DataProtection:KeysPath"] = keys.Path
            });
            var failure = StartFailure(factory);
            AssertNoKey(failure, secret);
            failure.Should().Contain("leaked signing key").And.Contain(fingerprint);
        }
        finally
        {
            JwtSigningKeys.LeakedFingerprints.Remove(fingerprint);
        }
    }

    private static string StartFailure(WebApplicationFactory<Program> factory)
    {
        try
        {
            using var client = factory.CreateClient();
        }
        catch (Exception ex)
        {
            return ex.ToString();
        }

        throw new Xunit.Sdk.XunitException("Host started.");
    }

    private static JwtSettings Settings(string secret, params string[] previous) => new()
    {
        Secret = secret,
        PreviousSecrets = previous,
        Issuer = "Worklane",
        Audience = "WorklaneClient"
    };

    private static string Key(int bytes) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes));

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
        Assert.Throws<InvalidOperationException>(() => Add(Environments.Production, "  "))
            .Message.Should().Contain("DataProtection:KeysPath is not set")
            .And.Contain("/var/lib/worklane/dataprotection-keys");
    }

    [Fact]
    public void Production_Startup_WithoutPath_Fails()
    {
        using var factory = ProbeFactory.Create(Environments.Production, new Dictionary<string, string?>
        {
            ["Jwt:Secret"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)),
            ["DataProtection:KeysPath"] = ""
        });
        try
        {
            using var client = factory.CreateClient();
            throw new Xunit.Sdk.XunitException("Host started.");
        }
        catch (Xunit.Sdk.XunitException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ex.ToString().Should().Contain("DataProtection:KeysPath is not set");
        }
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
        SetMode(dir.Path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Assert.Throws<InvalidOperationException>(() => Add(Environments.Production, dir.Path))
                .Message.Should().Contain("not writable").And.Contain(dir.Path);
        }
        finally
        {
            SetMode(dir.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void WritablePath_PersistsKeys_ForApplicationWorklane()
    {
        using var dir = new TempDirectory();
        using var provider = Add(Environments.Production, dir.Path).BuildServiceProvider();
        var payload = provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("wl").Protect("payload");
        Directory.GetFiles(dir.Path, "*.xml").Should().NotBeEmpty();

        using var same = Protector(dir.Path, DataProtectionRegistration.ApplicationName);
        same.GetRequiredService<IDataProtectionProvider>().CreateProtector("wl").Unprotect(payload).Should().Be("payload");
        using var other = Protector(dir.Path, "Other");
        var act = () => other.GetRequiredService<IDataProtectionProvider>().CreateProtector("wl").Unprotect(payload);
        act.Should().Throw<CryptographicException>();
    }

    private static ServiceProvider Protector(string path, string applicationName)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(path)).SetApplicationName(applicationName);
        return services.BuildServiceProvider();
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

    private static void SetMode(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, mode);
    }
}

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
        // Later than appsettings, so an empty Development secret does not hide the value under test.
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(_settings));
        return base.CreateHost(builder);
    }
}

file static class RepoRoot
{
    public static string Find()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Worklane.slnx")))
            root = root.Parent;
        if (root is null)
            throw new InvalidOperationException("Could not locate the repository root.");
        return root.FullName;
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
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (IOException)
        {
        }

        Directory.Delete(Path, recursive: true);
    }
}
