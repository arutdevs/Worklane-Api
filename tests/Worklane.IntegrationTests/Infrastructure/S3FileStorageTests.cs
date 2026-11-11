using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Worklane.Application.Common.Exceptions;
using Worklane.Application.Common.Interfaces;
using Worklane.Application.Common.Options;
using Worklane.Infrastructure;
using Worklane.Infrastructure.Services;

namespace Worklane.IntegrationTests.Infrastructure;

public class S3FileStorageTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Local")]
    [InlineData("local")]
    public void Provider_DefaultsToLocalDisk(string? provider)
    {
        using var host = BuildHost(provider, s3: null);
        Validate(host);
        using var scope = host.CreateScope();
        scope.ServiceProvider.GetRequiredService<IFileStorage>().Should().BeOfType<LocalFileStorage>();
    }

    [Fact]
    public void Provider_S3_SelectsS3FileStorage_AndBindsComposeKeys()
    {
        using var host = BuildHost("S3", ValidS3Config());
        Validate(host);
        using var scope = host.CreateScope();
        scope.ServiceProvider.GetRequiredService<IFileStorage>().Should().BeOfType<S3FileStorage>();

        var options = scope.ServiceProvider.GetRequiredService<IOptions<S3StorageOptions>>().Value;
        options.ServiceUrl.Should().Be("https://placeholder.r2.cloudflarestorage.com");
        options.Region.Should().Be("auto");
        options.Bucket.Should().Be("worklane-prod-files");
        options.AccessKey.Should().Be("placeholder-access-key");
        options.SecretKey.Should().Be("placeholder-secret-key");
        options.ForcePathStyle.Should().BeTrue();
        options.RequireHttps.Should().BeTrue();
        options.PresignExpirySeconds.Should().Be(60);
        options.PublicServiceUrl.Should().BeNullOrEmpty();
    }

    [Fact]
    public void Provider_Local_DoesNotValidateS3Settings()
    {
        var s3 = new Dictionary<string, string?>
        {
            ["FileStorage:S3:ServiceUrl"] = "not a url",
            ["FileStorage:S3:Bucket"] = "",
        };
        using var host = BuildHost("Local", s3);
        var act = () => Validate(host);
        act.Should().NotThrow();
    }

    [Fact]
    public void UnknownProvider_FailsValidateOnStart()
    {
        using var host = BuildHost("Blob", s3: null);
        var act = () => Validate(host);
        act.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().Contain(message => message.Contains("FileStorage:Provider", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("FileStorage:S3:ServiceUrl", "")]
    [InlineData("FileStorage:S3:Region", "")]
    [InlineData("FileStorage:S3:Bucket", "")]
    [InlineData("FileStorage:S3:AccessKey", "")]
    [InlineData("FileStorage:S3:SecretKey", "")]
    public void S3_MissingRequiredSetting_FailsValidateOnStart(string key, string value)
    {
        var s3 = ValidS3Config();
        s3[key] = value;
        using var host = BuildHost("S3", s3, Environments.Production);
        var act = () => Validate(host);
        act.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().Contain(message => message.Contains(key, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("http://s3:9000")]
    [InlineData("http://localhost:9000")]
    [InlineData("http://127.0.0.1:9000")]
    [InlineData("http://[::1]:9000")]
    [InlineData("https://host.docker.internal:9000")]
    [InlineData("https://minio")]
    public void S3_DevEndpoint_IsRejectedOutsideDevelopment(string serviceUrl)
    {
        var s3 = ValidS3Config();
        s3["FileStorage:S3:ServiceUrl"] = serviceUrl;
        s3["FileStorage:S3:RequireHttps"] = serviceUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "true" : "false";
        using var host = BuildHost("S3", s3, Environments.Production);
        var act = () => Validate(host);
        act.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().Contain(message => message.Contains("FileStorage:S3:ServiceUrl", StringComparison.Ordinal));
    }

    [Fact]
    public void S3_DevBucketAndPublicUrl_AreRejectedInProduction()
    {
        var s3 = ValidS3Config();
        s3["FileStorage:S3:Bucket"] = "worklane-dev-files";
        s3["FileStorage:S3:PublicServiceUrl"] = "http://localhost:9000";
        s3["FileStorage:S3:RequireHttps"] = "false";
        using var host = BuildHost("S3", s3, Environments.Production);
        var act = () => Validate(host);
        var failures = act.Should().Throw<OptionsValidationException>().Which.Failures;
        failures.Should().Contain(message => message.Contains("worklane-dev-files", StringComparison.Ordinal));
        failures.Should().Contain(message => message.Contains("RequireHttps", StringComparison.Ordinal));
        failures.Should().Contain(message => message.Contains("PublicServiceUrl", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void S3_RustFsDevSettings_AreAllowedInDevelopmentAndTesting(string environment)
    {
        var s3 = ValidS3Config();
        s3["FileStorage:S3:ServiceUrl"] = "http://s3:9000";
        s3["FileStorage:S3:Region"] = "us-east-1";
        s3["FileStorage:S3:Bucket"] = "worklane-dev-files";
        s3["FileStorage:S3:ForcePathStyle"] = "true";
        s3["FileStorage:S3:RequireHttps"] = "false";
        s3["FileStorage:S3:PublicServiceUrl"] = "http://localhost:9000";
        using var host = BuildHost("S3", s3, environment);
        var act = () => Validate(host);
        act.Should().NotThrow();
    }

    [Fact]
    public void Client_UsesPathStyleCustomEndpointAndDisablesDefaultChecksums()
    {
        var options = new S3StorageOptions
        {
            ServiceUrl = "https://placeholder.r2.cloudflarestorage.com",
            Region = "auto",
            Bucket = "worklane-prod-files",
            AccessKey = "placeholder-access-key",
            SecretKey = "placeholder-secret-key",
            ForcePathStyle = true,
        };

        using var client = S3ClientFactory.Create(options);
        var config = client.Config.Should().BeOfType<AmazonS3Config>().Subject;
        config.ServiceURL.TrimEnd('/').Should().Be(options.ServiceUrl);
        config.ForcePathStyle.Should().BeTrue();
        config.AuthenticationRegion.Should().Be("auto");
        config.RequestChecksumCalculation.Should().Be(RequestChecksumCalculation.WHEN_REQUIRED);
        config.ResponseChecksumValidation.Should().Be(ResponseChecksumValidation.WHEN_REQUIRED);
    }

    [Fact]
    public void PutRequest_SetsContentTypeAndDoesNotSetAnAclOrChecksum()
    {
        var options = new S3StorageOptions
        {
            ServiceUrl = "https://placeholder.r2.cloudflarestorage.com",
            Bucket = "worklane-prod-files",
        };
        using var body = new MemoryStream([1, 2, 3]);
        var request = S3FileStorage.CreatePutRequest(options, "ws/a/att/b", body, 3);
        var fresh = new PutObjectRequest();

        request.ContentType.Should().Be("application/octet-stream");
        request.Headers.ContentDisposition.Should().Be("attachment");
        request.Headers.ContentLength.Should().Be(3);
        request.IfNoneMatch.Should().Be("*");
        request.UseChunkEncoding.Should().BeFalse();
        request.DisableDefaultChecksumValidation.Should().BeTrue();
        request.DisablePayloadSigning.Should().BeFalse();
        request.CannedACL.Should().Be(fresh.CannedACL);
        request.ChecksumAlgorithm.Should().Be(fresh.ChecksumAlgorithm);
    }

    [Theory]
    [InlineData("ws/abc/att/def")]
    [InlineData("users/abc/avatar/def/64.webp")]
    public void Key_AcceptsApplicationLayout(string key)
    {
        S3ObjectKey.Normalize(key).Should().Be(key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("../etc/passwd")]
    [InlineData("a/../../b")]
    [InlineData("/abs/path")]
    [InlineData("foo\\bar")]
    [InlineData("a//b")]
    [InlineData("a/./b")]
    [InlineData("foo/bar/..")]
    [InlineData("foo..bar")]
    [InlineData("C:/windows")]
    public void Key_RejectsTraversalAndRootedPaths(string? key)
    {
        var act = () => S3ObjectKey.Normalize(key);
        act.Should().Throw<InvalidOperationException>().WithMessage("Invalid storage key.");
    }

    [Fact]
    public async Task OpenRead_MissingObject_MatchesLocalNotFound()
    {
        var storage = StorageThatThrows(new AmazonS3Exception(
            "missing", ErrorType.Receiver, "NoSuchKey", "req", HttpStatusCode.NotFound));
        var act = () => storage.OpenReadAsync("ws/a/att/b");
        (await act.Should().ThrowAsync<NotFoundException>()).Which.Message.Should().Be("The stored file is missing.");
    }

    [Fact]
    public async Task Delete_MissingObject_IsIdempotent_AndOtherErrorsPropagate()
    {
        var missing = StorageThatThrows(new AmazonS3Exception(
            "missing", ErrorType.Receiver, "NotFound", "req", HttpStatusCode.NotFound));
        var act = () => missing.DeleteAsync("ws/a/att/b");
        await act.Should().NotThrowAsync();

        var denied = StorageThatThrows(new AmazonS3Exception(
            "denied", ErrorType.Sender, "AccessDenied", "req", HttpStatusCode.Forbidden));
        var deniedAct = () => denied.DeleteAsync("ws/a/att/b");
        await deniedAct.Should().ThrowAsync<AmazonS3Exception>();
    }

    [Fact]
    public async Task Save_InvalidKey_DoesNotCallTheClient()
    {
        var proxy = new RecordingS3();
        var storage = Storage(proxy, NewTemp());
        var act = () => storage.SaveAsync("../escape", new MemoryStream([1]), 8);
        await act.Should().ThrowAsync<InvalidOperationException>();
        proxy.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Save_Oversize_ThrowsAndLeavesNoSpool()
    {
        var temp = NewTemp();
        var proxy = new RecordingS3();
        var storage = Storage(proxy, temp);
        await using var body = new MemoryStream(new byte[16]);
        var act = () => storage.SaveAsync("tenant/task/file", body, 8);
        await act.Should().ThrowAsync<PayloadTooLargeException>();
        proxy.Calls.Should().NotContain("PutObjectAsync");
        Directory.EnumerateFiles(temp, "*", SearchOption.AllDirectories).Should().BeEmpty();
        Directory.Delete(temp, recursive: true);
    }

    [Fact]
    public async Task Save_PreconditionFailed_ReportsThatTheKeyExists()
    {
        var storage = StorageThatThrows(new AmazonS3Exception(
            "exists", ErrorType.Sender, "PreconditionFailed", "req", HttpStatusCode.PreconditionFailed));
        await using var body = new MemoryStream([1, 2, 3]);
        var act = () => storage.SaveAsync("ws/a/att/b", body, 8);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already exists*");
    }

    [Fact]
    public void ErrorMapping_TreatsNotFoundAsMissing_AndLeavesOtherCodes()
    {
        var missing = new AmazonS3Exception("x", ErrorType.Receiver, "NoSuchKey", "r", HttpStatusCode.NotFound);
        S3StorageErrors.IsMissingObject(missing).Should().BeTrue();
        S3StorageErrors.MapRead(missing).Should().BeOfType<NotFoundException>();

        var other = new AmazonS3Exception("x", ErrorType.Sender, "InternalError", "r", HttpStatusCode.InternalServerError);
        S3StorageErrors.IsMissingObject(other).Should().BeFalse();
        S3StorageErrors.MapRead(other).Should().BeSameAs(other);
    }

    [Fact]
    public void ComposeDev_PinsTheRustFsServerImageByDigest()
    {
        var image = RustFsComposeImage.ReadServerImage();
        image.Should().StartWith("rustfs/rustfs:");
        image.Should().Contain("@sha256:");
    }

    private static S3FileStorage StorageThatThrows(Exception error)
    {
        var proxy = new RecordingS3 { Error = error };
        return Storage(proxy, NewTemp());
    }

    private static S3FileStorage Storage(RecordingS3 proxy, string temp)
    {
        var client = DispatchProxy.Create<IAmazonS3, RecordingS3>();
        var recording = (RecordingS3)(object)client;
        recording.Error = proxy.Error;
        recording.Calls.Clear();
        proxy.Calls = recording.Calls;
        return new S3FileStorage(
            client,
            Options.Create(new S3StorageOptions
            {
                ServiceUrl = "https://placeholder.r2.cloudflarestorage.com",
                Region = "auto",
                Bucket = "worklane-prod-files",
                AccessKey = "placeholder-access-key",
                SecretKey = "placeholder-secret-key",
            }),
            NullLogger<S3FileStorage>.Instance,
            temp);
    }

    private static string NewTemp()
    {
        var path = Path.Combine(Path.GetTempPath(), "wl-s3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static Dictionary<string, string?> ValidS3Config() => new()
    {
        ["FileStorage:S3:ServiceUrl"] = "https://placeholder.r2.cloudflarestorage.com",
        ["FileStorage:S3:Region"] = "auto",
        ["FileStorage:S3:Bucket"] = "worklane-prod-files",
        ["FileStorage:S3:AccessKey"] = "placeholder-access-key",
        ["FileStorage:S3:SecretKey"] = "placeholder-secret-key",
        ["FileStorage:S3:ForcePathStyle"] = "true",
        ["FileStorage:S3:RequireHttps"] = "true",
        ["FileStorage:S3:PresignExpirySeconds"] = "60",
    };

    private static ServiceProvider BuildHost(string? provider, Dictionary<string, string?>? s3, string environment = "Development")
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = "Host=localhost;Database=worklane;Username=worklane;Password=worklane",
            ["Jwt:Secret"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ["Frontend:BaseUrl"] = "https://app.example.test",
            ["FileStorage:LocalPath"] = NewTemp(),
        };
        if (provider is not null)
            values["FileStorage:Provider"] = provider;
        if (s3 is not null)
        {
            foreach (var pair in s3)
                values[pair.Key] = pair.Value;
        }

        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new HostingEnvironment
        {
            EnvironmentName = environment,
            ContentRootPath = Path.GetTempPath(),
        });
        services.AddInfrastructure(config);
        return services.BuildServiceProvider();
    }

    private static void Validate(ServiceProvider provider)
    {
        foreach (var validator in provider.GetServices<IStartupValidator>())
            validator.Validate();
    }

    private class RecordingS3 : DispatchProxy
    {
        public Exception? Error { get; set; }
        public List<string> Calls { get; set; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Calls.Add(targetMethod?.Name ?? "");
            if (Error is not null)
                throw Error;
            throw new InvalidOperationException(targetMethod?.Name ?? "s3");
        }
    }
}

internal static class RustFsComposeImage
{
    public static string ReadServerImage()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                var compose = Path.Combine(dir.FullName, "compose.dev.yml");
                if (File.Exists(compose))
                    return Read(compose);
                dir = dir.Parent;
            }
        }

        throw new InvalidOperationException("compose.dev.yml was not found.");
    }

    private static string Read(string path)
    {
        var inServer = false;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.StartsWith("s3:", StringComparison.Ordinal))
            {
                inServer = true;
                continue;
            }

            if (inServer && line.StartsWith("s3-init:", StringComparison.Ordinal))
                break;

            if (inServer && line.StartsWith("image:", StringComparison.Ordinal))
                return line["image:".Length..].Trim().Trim('"');
        }

        throw new InvalidOperationException("RustFS server image was not found in compose.dev.yml.");
    }
}
