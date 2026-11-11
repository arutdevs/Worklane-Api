using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Worklane.Application.Common.Options;
using Worklane.IntegrationTests.Infrastructure;

namespace Worklane.IntegrationTests.Imports;

public class ImportSignalRTests : IClassFixture<WorklaneWebApplicationFactory>
{
    private readonly WorklaneWebApplicationFactory _factory;

    public ImportSignalRTests(WorklaneWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Commit_NotifiesOnlyTheProject_AndRefetchesPastTheFanout()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, $"import-hub-{Guid.NewGuid():N}@example.com");
        var other = await board.Client.PostAsJsonAsync($"/api/workspaces/{board.User.Workspace.Id}/projects", new { name = "Away", key = "AW" });
        other.EnsureSuccessStatusCode();
        var otherId = (await other.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions)).GetProperty("id").GetGuid();

        await using var home = BoardTestSetup.Hub(_factory, board.User.Tokens.AccessToken);
        await using var away = BoardTestSetup.Hub(_factory, board.User.Tokens.AccessToken);
        var created = 0;
        var completed = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stray = 0;
        home.On<JsonElement>("TaskCreated", _ => Interlocked.Increment(ref created));
        home.On<JsonElement>("ImportCompleted", payload => completed.TrySetResult(payload.Clone()));
        away.On<JsonElement>("TaskCreated", _ => Interlocked.Increment(ref stray));
        away.On<JsonElement>("ImportCompleted", _ => Interlocked.Increment(ref stray));
        await home.StartAsync();
        await away.StartAsync();
        await home.InvokeAsync("JoinProject", board.Project.Id);
        await away.InvokeAsync("JoinProject", otherId);

        await ImportHttp.CommitAsync(board, ImportHttp.SimpleCsv("Live", "LIVE-1"), "generic");
        var evt = await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        evt.GetProperty("refetch").GetBoolean().Should().BeFalse();
        evt.GetProperty("createdCount").GetInt32().Should().Be(1);
        evt.GetProperty("projectId").GetGuid().Should().Be(board.Project.Id);
        created.Should().Be(1);
        await Task.Delay(TimeSpan.FromMilliseconds(400));
        stray.Should().Be(0);
    }
}

public class ImportFanoutTests : IClassFixture<ImportFanoutFactory>
{
    private readonly ImportFanoutFactory _factory;

    public ImportFanoutTests(ImportFanoutFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task OverTheFanout_SendsOnlyImportCompleted()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, $"import-fanout-{Guid.NewGuid():N}@example.com");
        await using var home = BoardTestSetup.Hub(_factory, board.User.Tokens.AccessToken);
        var created = 0;
        var completed = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        home.On<JsonElement>("TaskCreated", _ => Interlocked.Increment(ref created));
        home.On<JsonElement>("ImportCompleted", payload => completed.TrySetResult(payload.Clone()));
        await home.StartAsync();
        await home.InvokeAsync("JoinProject", board.Project.Id);

        var csv = "Title,External Id\nOne,F-1\nTwo,F-2\n";
        await ImportHttp.CommitAsync(board, csv, "generic");
        var evt = await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        evt.GetProperty("refetch").GetBoolean().Should().BeTrue();
        evt.GetProperty("createdCount").GetInt32().Should().Be(2);
        await Task.Delay(TimeSpan.FromMilliseconds(400));
        created.Should().Be(0);
    }
}

public class ImportFanoutFactory : WorklaneWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Imports:RealtimeFanoutLimit"] = "1"
            });
        });
    }
}

public class ImportRateLimitTests : IClassFixture<ImportRateLimitFactory>
{
    private readonly ImportRateLimitFactory _factory;

    public ImportRateLimitTests(ImportRateLimitFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Upload_OverThePolicy_IsRateLimitedWithoutLimitNumbers()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, $"import-rate-{Guid.NewGuid():N}@example.com");
        (await ImportHttp.UploadAsync(board.Client, board.Project.Id, "a.csv", ImportHttp.SimpleCsv("A", "R-1")))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        (await ImportHttp.UploadAsync(board.Client, board.Project.Id, "b.csv", ImportHttp.SimpleCsv("B", "R-2")))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        var limited = await ImportHttp.UploadAsync(board.Client, board.Project.Id, "c.csv", ImportHttp.SimpleCsv("C", "R-3"));
        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter.Should().NotBeNull();
        var body = await ImportHttp.ReadAsync(limited);
        body.GetProperty("code").GetString().Should().Be("rate_limited");
        body.GetProperty("scope").GetString().Should().Be("user");
        body.TryGetProperty("permitLimit", out _).Should().BeFalse();
        body.TryGetProperty("limit", out _).Should().BeFalse();
    }
}

public class ImportRateLimitFactory : WorklaneWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimit:ImportUpload:PermitLimit"] = "2",
                ["RateLimit:ImportUpload:WindowSeconds"] = "3600"
            });
        });
    }
}

public class ImportSizeTests : IClassFixture<ImportSizeFactory>
{
    private readonly ImportSizeFactory _factory;

    public ImportSizeTests(ImportSizeFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task OversizedFile_IsFileTooLarge_AndDeclaredBody_IsRequestTooLarge()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, $"import-size-{Guid.NewGuid():N}@example.com");
        var settings = _factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ImportSettings>>().Value;
        var overFile = await ImportHttp.UploadBytesAsync(
            board.Client,
            board.Project.Id,
            "big.csv",
            Encoding.UTF8.GetBytes(new string('a', (int)settings.MaxBytes + 1)),
            "generic");
        overFile.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        var fileProblem = await ImportHttp.ReadAsync(overFile);
        fileProblem.GetProperty("code").GetString().Should().Be("file_too_large");
        fileProblem.GetProperty("maxBytes").GetInt64().Should().Be(settings.MaxBytes);

        var ceiling = settings.RequestLimit + 1;
        var overBody = await ImportHttp.UploadBytesAsync(
            board.Client,
            board.Project.Id,
            "huge.csv",
            new byte[ceiling],
            "generic");
        overBody.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        var requestProblem = await ImportHttp.ReadAsync(overBody);
        requestProblem.GetProperty("code").GetString().Should().Be("request_too_large");
        requestProblem.TryGetProperty("maxBytes", out _).Should().BeFalse();
    }
}

public class ImportSizeFactory : WorklaneWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Imports:MaxBytes"] = "64"
            });
        });
    }
}
