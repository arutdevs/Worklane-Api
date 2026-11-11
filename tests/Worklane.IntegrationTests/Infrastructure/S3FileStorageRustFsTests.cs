using System.Net;
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Worklane.Application.Common.Exceptions;
using Worklane.Application.Common.Options;
using Worklane.Infrastructure.Services;

namespace Worklane.IntegrationTests.Infrastructure;

public sealed class S3FileStorageRustFsTests : IClassFixture<RustFsFixture>
{
    private readonly RustFsFixture _fixture;

    public S3FileStorageRustFsTests(RustFsFixture fixture) => _fixture = fixture;

    [DockerFact]
    public async Task PutGetDelete_RoundTrip_AndMissingDeleteIsIdempotent()
    {
        var storage = _fixture.Storage;
        var key = "ws/" + Id() + "/att/" + Id();
        var payload = "round-trip"u8.ToArray();
        await using var body = new NonSeekableStream(new MemoryStream(payload));

        var written = await storage.SaveAsync(key, body, 1024);
        written.Should().Be(payload.Length);

        var head = await _fixture.Probe.GetObjectMetadataAsync(_fixture.Bucket, key);
        head.Headers.ContentType.Should().StartWith("application/octet-stream");
        head.Headers.ContentDisposition.Should().Be("attachment");

        await using var read = await storage.OpenReadAsync(key);
        var buffer = new byte[payload.Length];
        (await read.ReadAsync(buffer)).Should().Be(payload.Length);
        buffer.Should().Equal(payload);

        await storage.DeleteAsync(key);
        await storage.DeleteAsync(key);
        var missing = () => storage.OpenReadAsync(key);
        (await missing.Should().ThrowAsync<NotFoundException>()).Which.Message.Should().Be("The stored file is missing.");

        var gone = () => _fixture.Probe.GetObjectMetadataAsync(_fixture.Bucket, key);
        (await gone.Should().ThrowAsync<AmazonS3Exception>()).Which.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [DockerFact]
    public async Task SecondPut_DoesNotReplaceTheObject()
    {
        var key = "ws/" + Id() + "/att/" + Id();
        await _fixture.Storage.SaveAsync(key, new MemoryStream([1, 2, 3]), 1024);
        var again = () => _fixture.Storage.SaveAsync(key, new MemoryStream([9]), 1024);
        await again.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already exists*");

        await using var read = await _fixture.Storage.OpenReadAsync(key);
        var buffer = new byte[3];
        (await read.ReadAsync(buffer)).Should().Be(3);
        buffer.Should().Equal(1, 2, 3);
        await _fixture.Storage.DeleteAsync(key);
    }

    [DockerFact]
    public async Task OversizeAndTraversal_StoreNothing()
    {
        var key = "ws/" + Id() + "/att/" + Id();
        var oversize = () => _fixture.Storage.SaveAsync(key, new MemoryStream(new byte[16]), 8);
        await oversize.Should().ThrowAsync<PayloadTooLargeException>();
        var missing = () => _fixture.Storage.OpenReadAsync(key);
        await missing.Should().ThrowAsync<NotFoundException>();

        var escape = () => _fixture.Storage.SaveAsync("../escape", new MemoryStream([1]), 8);
        await escape.Should().ThrowAsync<InvalidOperationException>();
    }

    private static string Id() => Guid.NewGuid().ToString("N");

    private sealed class NonSeekableStream : Stream
    {
        private readonly Stream _inner;
        public NonSeekableStream(Stream inner) => _inner = inner;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}

public sealed class RustFsFixture : IAsyncLifetime
{
    private IContainer? _container;

    public string Bucket { get; private set; } = "";
    public S3FileStorage Storage { get; private set; } = null!;
    public IAmazonS3 Probe { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        if (!DockerProbe.IsAvailable)
            return;

        var accessKey = Hex(16);
        var secretKey = Hex(24);
        Bucket = "wl-it-" + Hex(4);
        var image = RustFsComposeImage.ReadServerImage();
        _container = new ContainerBuilder(image)
            .WithCommand("/data")
            .WithEnvironment("RUSTFS_ACCESS_KEY", accessKey)
            .WithEnvironment("RUSTFS_SECRET_KEY", secretKey)
            .WithEnvironment("RUSTFS_CONSOLE_ENABLE", "false")
            .WithEnvironment("RUSTFS_CHECK_UPDATE", "false")
            .WithPortBinding(9000, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request =>
                request.ForPath("/health").ForPort(9000)))
            .Build();

        await _container.StartAsync();
        var port = _container.GetMappedPublicPort(9000);
        var options = new S3StorageOptions
        {
            ServiceUrl = $"http://127.0.0.1:{port}",
            Region = "us-east-1",
            Bucket = Bucket,
            AccessKey = accessKey,
            SecretKey = secretKey,
            ForcePathStyle = true,
            RequireHttps = false,
        };
        Probe = S3ClientFactory.Create(options);
        await Probe.PutBucketAsync(new PutBucketRequest { BucketName = Bucket });
        Storage = new S3FileStorage(Probe, Options.Create(options), NullLogger<S3FileStorage>.Instance);
    }

    public async Task DisposeAsync()
    {
        if (Probe is IDisposable disposable)
            disposable.Dispose();
        if (_container is not null)
            await _container.DisposeAsync();
    }

    private static string Hex(int bytes)
    {
        var value = Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();
        if (value.Contains("rustfsadmin", StringComparison.Ordinal))
            throw new InvalidOperationException("Generated credential used the rejected default name.");
        return value;
    }
}

public sealed class DockerFactAttribute : FactAttribute
{
    public DockerFactAttribute()
    {
        if (!DockerProbe.IsAvailable)
            Skip = "Docker is not available; RustFS Testcontainers test was skipped.";
    }
}

internal static class DockerProbe
{
    private static readonly Lazy<bool> Available = new(Detect);

    public static bool IsAvailable => Available.Value;

    private static bool Detect()
    {
        try
        {
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "docker",
                    ArgumentList = { "info" },
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                }
            };
            if (!process.Start())
                return false;
            if (!process.WaitForExit(TimeSpan.FromSeconds(5)))
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { }
                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
