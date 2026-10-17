using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Worklane.Application.Board.Dtos;
using Worklane.Application.Board.Interfaces;
using Worklane.Application.Common.Exceptions;
using Worklane.Application.Projects.Dtos;
using Worklane.Application.Workspaces.Dtos;
using Worklane.Domain.Entities;
using Worklane.Infrastructure.Persistence;
using Worklane.Infrastructure.Services;
using Worklane.IntegrationTests.Infrastructure;

namespace Worklane.IntegrationTests.Board;

public class RateLimitAndQuotaTests : IClassFixture<RateLimitAndQuotaFactory>
{
    private readonly RateLimitAndQuotaFactory _factory;

    public RateLimitAndQuotaTests(RateLimitAndQuotaFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task AttachmentAndCommentWrites_AreLimitedPerUserAndWorkspace()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "limit-owner@test.com", "Owner");
        var task = await BoardTestSetup.CreateTaskAsync(board, "Limited");
        var member = await TestAuthHelper.RegisterAsync(_factory.CreateClient(), "limit-member@test.com", "Member");
        await BoardTestSetup.InviteAsync(board.Client, board.User.Workspace.Id, member.Tokens.User.Email, "Member");

        (await Comment(board.Client, task.Id, "one")).StatusCode.Should().Be(HttpStatusCode.Created);
        (await Comment(board.Client, task.Id, "two")).StatusCode.Should().Be(HttpStatusCode.Created);
        var blocked = await Comment(board.Client, task.Id, "three");
        var blockedBody = await blocked.Content.ReadAsStringAsync();
        blocked.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, blockedBody);
        blocked.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        blocked.Headers.RetryAfter.Should().NotBeNull();
        blocked.Headers.RetryAfter!.Delta.Should().NotBeNull();
        blocked.Headers.RetryAfter.Delta!.Value.TotalSeconds.Should().BeGreaterThan(0);

        (await Upload(board.Client, task.Id, SampleFiles.Png, "still.png")).StatusCode.Should().Be(HttpStatusCode.Created);
        (await Comment(member.Client, task.Id, "member")).StatusCode.Should().Be(HttpStatusCode.Created);

        var otherWorkspace = await (await board.Client.PostAsJsonAsync("/api/workspaces", new { name = "Elsewhere" }))
            .Content.ReadFromJsonAsync<WorkspaceDto>(TestAuthHelper.JsonOptions);
        var otherProject = await (await board.Client.PostAsJsonAsync($"/api/workspaces/{otherWorkspace!.Id}/projects",
            new { name = "Elsewhere", key = "EW" })).Content.ReadFromJsonAsync<ProjectDto>(TestAuthHelper.JsonOptions);
        var columns = await (await board.Client.GetAsync($"/api/projects/{otherProject!.Id}/columns"))
            .Content.ReadFromJsonAsync<List<BoardColumnDto>>(TestAuthHelper.JsonOptions);
        var otherTask = await (await board.Client.PostAsJsonAsync($"/api/projects/{otherProject.Id}/tasks", new
        {
            title = "Other",
            priority = "Medium",
            columnId = columns!.Single(c => c.Name == "To Do").Id
        })).Content.ReadFromJsonAsync<TaskItemDto>(TestAuthHelper.JsonOptions);

        (await Comment(board.Client, otherTask!.Id, "fresh bucket")).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Upload_OverTheWorkspaceQuota_Is409()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "quota-owner@test.com");
        var task = await BoardTestSetup.CreateTaskAsync(board, "Quota");

        (await Upload(board.Client, task.Id, SampleFiles.Png, "shot.png")).StatusCode.Should().Be(HttpStatusCode.Created);
        var over = await Upload(board.Client, task.Id, SampleFiles.Jpeg, "shot.jpg");
        var body = await over.Content.ReadAsStringAsync();
        over.StatusCode.Should().Be(HttpStatusCode.Conflict, body);
        body.Should().Contain("quota");
    }

    [Fact]
    public async Task ConcurrentUpload_WaitsForTheWorkspaceQuotaLock()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "quota-race@test.com");
        var task = await BoardTestSetup.CreateTaskAsync(board, "Race");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync();
        await WorkspaceQuotaLock.AcquireAsync(db.Database, board.User.Workspace.Id, CancellationToken.None);
        db.TaskAttachments.Add(new TaskAttachment
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            UploadedById = board.User.Tokens.User.Id,
            FileName = "held.png",
            ContentType = "image/png",
            SizeBytes = SampleFiles.Png.Length,
            StorageKey = $"{board.User.Workspace.Id:N}/{task.Id:N}/{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var upload = Upload(board.Client, task.Id, SampleFiles.Png, "second.png");
        var sawWaiter = false;
        for (var i = 0; i < 50 && !sawWaiter && !upload.IsCompleted; i++)
        {
            await Task.Delay(100);
            var connection = db.Database.GetDbConnection();
            await using var command = connection.CreateCommand();
            command.Transaction = tx.GetDbTransaction();
            command.CommandText = """
                SELECT COUNT(*)::bigint FROM pg_locks
                WHERE locktype = 'advisory' AND NOT granted
                """;
            var count = (long)(await command.ExecuteScalarAsync())!;
            sawWaiter = count > 0;
        }

        sawWaiter.Should().BeTrue("the upload should block on the workspace advisory lock");
        await tx.CommitAsync();

        var response = await upload;
        var responseBody = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Conflict, responseBody);

        using var check = _factory.Services.CreateScope();
        var stored = await check.ServiceProvider.GetRequiredService<WorklaneDbContext>().TaskAttachments
            .IgnoreQueryFilters()
            .CountAsync(a => a.TaskId == task.Id);
        stored.Should().Be(1);
    }

    private static Task<HttpResponseMessage> Comment(HttpClient client, Guid taskId, string body) =>
        client.PostAsJsonAsync($"/api/tasks/{taskId}/comments", new { body });

    private static Task<HttpResponseMessage> Upload(HttpClient client, Guid taskId, byte[] bytes, string fileName)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "file", fileName);
        return client.PostAsync($"/api/tasks/{taskId}/attachments", content);
    }
}

public class RateLimitAndQuotaFactory : WorklaneWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimit:Attachments:PermitLimit"] = "2",
                ["RateLimit:Attachments:WindowSeconds"] = "60",
                ["RateLimit:Comments:PermitLimit"] = "2",
                ["RateLimit:Comments:WindowSeconds"] = "60",
                ["FileStorage:WorkspaceQuotaBytes"] = SampleFiles.Png.Length.ToString(),
            });
        });
    }
}

public class BroadcastFailureTests : IClassFixture<BroadcastFailureFactory>
{
    private readonly BroadcastFailureFactory _factory;

    public BroadcastFailureTests(BroadcastFailureFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Delete_StaysSuccessfulWhenTheBroadcastThrows()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "broadcast-owner@test.com");
        var task = await BoardTestSetup.CreateTaskAsync(board, "Saved");

        var created = await board.Client.PostAsJsonAsync($"/api/tasks/{task.Id}/comments", new { body = "kept" });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var comment = (await created.Content.ReadFromJsonAsync<CommentDto>(TestAuthHelper.JsonOptions))!;

        var deleted = await board.Client.DeleteAsync($"/api/comments/{comment.Id}");
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var comments = await (await board.Client.GetAsync($"/api/tasks/{task.Id}/comments"))
            .Content.ReadFromJsonAsync<List<CommentDto>>(TestAuthHelper.JsonOptions);
        comments.Should().BeEmpty();

        var uploaded = await Upload(board.Client, task.Id);
        uploaded.StatusCode.Should().Be(HttpStatusCode.Created);
        var file = (await uploaded.Content.ReadFromJsonAsync<AttachmentDto>(TestAuthHelper.JsonOptions))!;
        (await board.Client.DeleteAsync($"/api/attachments/{file.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var files = await (await board.Client.GetAsync($"/api/tasks/{task.Id}/attachments"))
            .Content.ReadFromJsonAsync<List<AttachmentDto>>(TestAuthHelper.JsonOptions);
        files.Should().BeEmpty();
    }

    private static Task<HttpResponseMessage> Upload(HttpClient client, Guid taskId)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(SampleFiles.Png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(file, "file", "shot.png");
        return client.PostAsync($"/api/tasks/{taskId}/attachments", content);
    }
}

public class BroadcastFailureFactory : WorklaneWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.AddScoped<IBoardNotifier, DeleteBroadcastFails>();
        });
    }

    private sealed class DeleteBroadcastFails : IBoardNotifier
    {
        public Task TaskCreatedAsync(Guid projectId, TaskItemDto task, CancellationToken ct = default) => Task.CompletedTask;
        public Task TaskMovedAsync(Guid projectId, TaskItemDto task, CancellationToken ct = default) => Task.CompletedTask;
        public Task TaskUpdatedAsync(Guid projectId, TaskItemDto task, CancellationToken ct = default) =>
            throw new NotFoundException("TaskItem", task.Id);
        public Task CommentCreatedAsync(Guid projectId, CommentBroadcastDto comment, CancellationToken ct = default) => Task.CompletedTask;
        public Task CommentUpdatedAsync(Guid projectId, CommentBroadcastDto comment, CancellationToken ct = default) => Task.CompletedTask;
        public Task CommentDeletedAsync(Guid projectId, CommentDeletedEvent payload, CancellationToken ct = default) =>
            throw new InvalidOperationException("comment broadcast failed");
        public Task AttachmentAddedAsync(Guid projectId, AttachmentBroadcastDto attachment, CancellationToken ct = default) => Task.CompletedTask;
        public Task AttachmentDeletedAsync(Guid projectId, AttachmentDeletedEvent payload, CancellationToken ct = default) =>
            throw new InvalidOperationException("attachment broadcast failed");
    }
}
