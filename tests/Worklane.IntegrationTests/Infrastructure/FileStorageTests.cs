using FluentAssertions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Worklane.Application.Common.Exceptions;
using Worklane.Application.Common.Options;
using Worklane.Infrastructure.Services;

namespace Worklane.IntegrationTests.Infrastructure;

public class FileStorageTests
{
    [Fact]
    public void DefaultMaxBytes_IsTenMegabytes()
    {
        new FileStorageSettings().MaxBytes.Should().Be(10 * 1024 * 1024);
    }

    [Fact]
    public async Task Save_RejectsKeysThatEscapeTheRoot_AndDeletesAPartialOversizeFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "worklane-storage", Guid.NewGuid().ToString("N"));
        var storage = new LocalFileStorage(
            Options.Create(new FileStorageSettings { LocalPath = root, MaxBytes = 8 }),
            new TestHostEnvironment(Directory.GetCurrentDirectory()),
            NullLogger<LocalFileStorage>.Instance);

        var act = () => storage.SaveAsync("../escape", new MemoryStream([1]), 8);
        await act.Should().ThrowAsync<InvalidOperationException>();

        await using var tooBig = new MemoryStream(new byte[16]);
        var oversize = () => storage.SaveAsync("tenant/task/file", tooBig, 8);
        await oversize.Should().ThrowAsync<PayloadTooLargeException>();
        Directory.Exists(Path.Combine(root, "tenant")).Should().BeTrue();
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Should().BeEmpty();

        await using var ok = new MemoryStream([1, 2, 3]);
        var written = await storage.SaveAsync("tenant/task/file", ok, 8);
        written.Should().Be(3);
        await using var read = await storage.OpenReadAsync("tenant/task/file");
        var buffer = new byte[3];
        (await read.ReadAsync(buffer)).Should().Be(3);
        buffer.Should().Equal(1, 2, 3);

        Directory.Delete(root, recursive: true);
    }

    [Theory]
    [InlineData("image/jpg", "image/jpeg")]
    [InlineData("Image/PNG; charset=binary", "image/png")]
    [InlineData("  ", null)]
    public void NormalizeContentType_CanonicalizesTheAllowList(string? input, string? expected)
    {
        TaskAttachmentService.NormalizeContentType(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("../../etc/passwd.png", "passwd.png")]
    [InlineData("", "file")]
    public void SanitizeFileName_DropsDirectories(string input, string expected)
    {
        TaskAttachmentService.SanitizeFileName(input).Should().Be(expected);
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public TestHostEnvironment(string contentRoot) => ContentRootPath = contentRoot;

        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
