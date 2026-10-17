using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Worklane.Application.Common.Exceptions;
using Worklane.Application.Common.Options;
using Worklane.Infrastructure;
using Worklane.Infrastructure.Services;

namespace Worklane.IntegrationTests.Infrastructure;

public class FileStorageTests
{
    [Fact]
    public void DefaultMaxBytes_IsTenMegabytes()
    {
        var settings = new FileStorageSettings();
        settings.MaxBytes.Should().Be(10 * 1024 * 1024);
        settings.AttachmentRequestLimit.Should().Be(11 * 1024 * 1024);
    }

    [Theory]
    [InlineData("FileStorage:MaxBytes", "0")]
    [InlineData("FileStorage:MaxBytes", "-1")]
    [InlineData("FileStorage:WorkspaceQuotaBytes", "0")]
    [InlineData("FileStorage:WorkspaceQuotaBytes", "-8")]
    public void NonPositiveStorageLimits_FailValidateOnStart(string key, string value)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = "Host=localhost;Database=worklane;Username=worklane;Password=worklane",
            [key] = value
        }).Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging();
        services.AddInfrastructure(config);
        using var provider = services.BuildServiceProvider();

        var act = () =>
        {
            foreach (var validator in provider.GetServices<IStartupValidator>())
                validator.Validate();
        };

        act.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().Contain(message => message.Contains(key, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("0", 0L)]
    [InlineData("-5", -5L)]
    public void ConfiguredNonPositiveLimits_AreBound(string raw, long expected)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FileStorage:MaxBytes"] = raw,
            ["FileStorage:WorkspaceQuotaBytes"] = raw
        }).Build();

        var settings = FileStorageSettings.FromConfiguration(config);
        settings.MaxBytes.Should().Be(expected);
        settings.WorkspaceQuotaBytes.Should().Be(expected);
    }

    [Fact]
    public void TextScan_RejectsAMarkerSplitAcrossChunks()
    {
        var checker = StreamingTextChecker.Create(".csv", "a,b\n"u8);
        checker.Accept("aaaa,<scr"u8);
        var act = () => checker.Accept("ipt>x\n"u8);
        act.Should().Throw<UnsupportedMediaTypeException>();
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

    [Fact]
    public void ConfiguredAllowList_ReplacesTheDefault()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FileStorage:AllowedContentTypes:0"] = "image/png",
            ["FileStorage:AllowedExtensions:0"] = ".png",
        }).Build();

        var settings = FileStorageSettings.FromConfiguration(config);

        settings.AllowedContentTypes.Should().Equal("image/png");
        settings.AllowedExtensions.Should().Equal(".png");
        settings.MaxBytes.Should().Be(10 * 1024 * 1024);
        settings.AttachmentRequestLimit.Should().Be(11 * 1024 * 1024);
    }

    [Fact]
    public void MissingAllowList_KeepsTheBuiltInDefault()
    {
        var settings = FileStorageSettings.FromConfiguration(new ConfigurationBuilder().Build());
        settings.AllowedExtensions.Should().Contain(".png").And.Contain(".pdf");
        settings.AllowedContentTypes.Should().Contain("image/png");
    }

    [Fact]
    public void Signature_MatchesExtensionAndIgnoresAMisleadingHeader()
    {
        FileSignature.Matches(".png", SampleFiles.Png).Should().BeTrue();
        FileSignature.Matches(".jpg", SampleFiles.Jpeg).Should().BeTrue();
        FileSignature.Matches(".jpeg", SampleFiles.Jpeg).Should().BeTrue();
        FileSignature.Matches(".pdf", SampleFiles.Pdf).Should().BeTrue();
        FileSignature.Matches(".txt", SampleFiles.Text).Should().BeTrue();
        FileSignature.Matches(".png", SampleFiles.Html).Should().BeFalse();
        FileSignature.Matches(".jpg", SampleFiles.Png).Should().BeFalse();
        FileSignature.Matches(".exe", SampleFiles.Png).Should().BeFalse();
        FileSignature.ContentTypeForExtension(".jpg").Should().Be("image/jpeg");
    }

    [Fact]
    public void Signature_AcceptsThaiSingleByteAndUtf16_AndRejectsMarkup()
    {
        var tis620 = "col,"u8.ToArray().Concat(new byte[] { 0xCA, 0xC7, 0xD1, 0xCA, 0xB4, 0xD5 }).ToArray();
        var cp874 = "price,"u8.ToArray().Concat(new byte[] { 0x80, (byte)'1', (byte)'\n' }).ToArray();
        var utf16 = new List<byte> { 0xFF, 0xFE };
        foreach (var ch in "name,value\n")
        {
            utf16.Add((byte)ch);
            utf16.Add(0x00);
        }
        var utf16Script = new List<byte> { 0xFE, 0xFF };
        foreach (var ch in "<script>alert(1)</script>")
        {
            utf16Script.Add(0x00);
            utf16Script.Add((byte)ch);
        }

        FileSignature.Matches(".csv", tis620).Should().BeTrue();
        FileSignature.Matches(".csv", cp874).Should().BeTrue();
        FileSignature.Matches(".txt", utf16.ToArray()).Should().BeTrue();
        FileSignature.Matches(".csv", Encoding.UTF8.GetBytes("สวัสดี\n")).Should().BeTrue();
        FileSignature.Matches(".csv", "a<b,1\n"u8).Should().BeTrue();
        FileSignature.Matches(".txt", "<script>alert(1)</script>"u8).Should().BeFalse();
        FileSignature.Matches(".csv", "<html><body>x</body></html>"u8).Should().BeFalse();
        FileSignature.Matches(".txt", utf16Script.ToArray()).Should().BeFalse();
        FileSignature.Matches(".csv", new byte[] { (byte)'a', 0x01, (byte)'b' }).Should().BeFalse();
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
