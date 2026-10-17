using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Worklane.Application.Board.Dtos;
using Worklane.Application.Common.Exceptions;
using Worklane.Application.Common.Interfaces;
using Worklane.Application.Common.Options;
using Worklane.Infrastructure.Persistence;
using Worklane.IntegrationTests.Infrastructure;
using Worklane.WebApi.Hubs;
using Worklane.WebApi.Middleware;

namespace Worklane.IntegrationTests.Board;

public class SecurityReviewTests : IClassFixture<WorklaneWebApplicationFactory>
{
    private readonly WorklaneWebApplicationFactory _factory;

    public SecurityReviewTests(WorklaneWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task CreateMoveAndPatch_RejectColumnsFromAnotherProjectOrWorkspace()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "columns-owner@test.com", "Owner");
        var task = await BoardTestSetup.CreateTaskAsync(board, "Stay put");

        var sameWorkspace = await CreateForeignColumn(board.Client, board.User.Workspace.Id, "OT", "LeakProbeSameWorkspace");
        var outsider = await TestAuthHelper.RegisterAsync(_factory.CreateClient(), "columns-other@test.com", "Outsider");
        var otherProject = await outsider.Client.PostAsJsonAsync($"/api/workspaces/{outsider.Workspace.Id}/projects",
            new { name = "Other", key = "ZX" });
        otherProject.EnsureSuccessStatusCode();
        var other = (await otherProject.Content.ReadFromJsonAsync<Worklane.Application.Projects.Dtos.ProjectDto>(TestAuthHelper.JsonOptions))!;
        var otherColumns = await (await outsider.Client.GetAsync($"/api/projects/{other.Id}/columns"))
            .Content.ReadFromJsonAsync<List<BoardColumnDto>>(TestAuthHelper.JsonOptions);
        var otherColumn = otherColumns!.Single(c => c.Name == "To Do");
        (await outsider.Client.PatchAsJsonAsync($"/api/columns/{otherColumn.Id}", new { name = "LeakProbeOtherWorkspace" }))
            .EnsureSuccessStatusCode();

        foreach (var columnId in new[] { sameWorkspace, otherColumn.Id })
        {
            (await board.Client.PostAsJsonAsync($"/api/projects/{board.Project.Id}/tasks", new
            {
                title = "Should not exist",
                priority = "Medium",
                columnId
            })).StatusCode.Should().Be(HttpStatusCode.NotFound);

            (await board.Client.PostAsJsonAsync($"/api/tasks/{task.Id}/move", new { toColumnId = columnId, toIndex = 0 }))
                .StatusCode.Should().Be(HttpStatusCode.NotFound);

            (await board.Client.PatchAsJsonAsync($"/api/tasks/{task.Id}", new { columnId }))
                .StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        var reloaded = await (await board.Client.GetAsync($"/api/tasks/{task.Id}"))
            .Content.ReadFromJsonAsync<TaskItemDto>(TestAuthHelper.JsonOptions);
        reloaded!.ColumnId.Should().Be(board.Todo.Id);

        var activity = await (await board.Client.GetAsync($"/api/tasks/{task.Id}/activity")).Content.ReadAsStringAsync();
        activity.Should().NotContain("LeakProbeSameWorkspace");
        activity.Should().NotContain("LeakProbeOtherWorkspace");
        activity.Should().NotContain(sameWorkspace.ToString());
        activity.Should().NotContain(otherColumn.Id.ToString());
    }

    [Fact]
    public async Task Assignee_MustBeAWorkspaceMember()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "assignee-owner@test.com");
        var outsider = await TestAuthHelper.RegisterAsync(_factory.CreateClient(), "assignee-out@test.com");

        var rejected = await board.Client.PostAsJsonAsync($"/api/projects/{board.Project.Id}/tasks", new
        {
            title = "Not theirs",
            priority = "Medium",
            assigneeId = outsider.Tokens.User.Id
        });
        rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var accepted = await board.Client.PostAsJsonAsync($"/api/projects/{board.Project.Id}/tasks", new
        {
            title = "Mine",
            priority = "Medium",
            assigneeId = board.User.Tokens.User.Id
        });
        accepted.StatusCode.Should().Be(HttpStatusCode.Created);

        var task = (await accepted.Content.ReadFromJsonAsync<TaskItemDto>(TestAuthHelper.JsonOptions))!;
        (await board.Client.PatchAsJsonAsync($"/api/tasks/{task.Id}", new { assigneeId = outsider.Tokens.User.Id }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var reloaded = await (await board.Client.GetAsync($"/api/tasks/{task.Id}"))
            .Content.ReadFromJsonAsync<TaskItemDto>(TestAuthHelper.JsonOptions);
        reloaded!.AssigneeId.Should().Be(board.User.Tokens.User.Id);
    }

    [Fact]
    public async Task QueriesWithoutATenant_ReturnNoRows()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "tenant-closed@test.com");
        var task = await BoardTestSetup.CreateTaskAsync(board, "Hidden without a tenant");
        (await board.Client.PostAsJsonAsync($"/api/tasks/{task.Id}/comments", new { body = "note" })).EnsureSuccessStatusCode();
        (await UploadAsync(board.Client, task.Id, SampleFiles.Png, "image/png", "shot.png")).EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();

        (await db.WorkspaceMembers.CountAsync()).Should().Be(0);
        (await db.Projects.CountAsync()).Should().Be(0);
        (await db.BoardColumns.CountAsync()).Should().Be(0);
        (await db.TaskItems.CountAsync()).Should().Be(0);
        (await db.TaskComments.CountAsync()).Should().Be(0);
        (await db.TaskAttachments.CountAsync()).Should().Be(0);
        (await db.TaskActivities.CountAsync()).Should().Be(0);
        (await db.TaskItems.IgnoreQueryFilters().CountAsync(t => t.Id == task.Id)).Should().Be(1);
        (await db.BoardColumns.IgnoreQueryFilters().CountAsync(c => c.ProjectId == board.Project.Id)).Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task JoinProject_RequiresCurrentMembership_AndRemovalDropsTheGroup()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "hub-owner@test.com", "Owner");
        var task = await BoardTestSetup.CreateTaskAsync(board, "Live");
        var member = await TestAuthHelper.RegisterAsync(_factory.CreateClient(), "hub-member@test.com", "Member");
        await BoardTestSetup.InviteAsync(board.Client, board.User.Workspace.Id, member.Tokens.User.Email, "Member");
        var outsider = await TestAuthHelper.RegisterAsync(_factory.CreateClient(), "hub-outsider@test.com");

        await using var outsiderHub = BoardTestSetup.Hub(_factory, outsider.Tokens.AccessToken);
        await outsiderHub.StartAsync();
        var outsiderJoin = () => outsiderHub.InvokeAsync("JoinProject", board.Project.Id);
        await outsiderJoin.Should().ThrowAsync<Microsoft.AspNetCore.SignalR.HubException>();

        await using var memberHub = BoardTestSetup.Hub(_factory, member.Tokens.AccessToken);
        var received = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        memberHub.On<JsonElement>("CommentCreated", payload => received.TrySetResult(payload));
        await memberHub.StartAsync();
        await memberHub.InvokeAsync("JoinProject", board.Project.Id);

        (await board.Client.DeleteAsync($"/api/workspaces/{board.User.Workspace.Id}/members/{member.Tokens.User.Id}"))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await board.Client.PostAsJsonAsync($"/api/tasks/{task.Id}/comments", new { body = "After removal" }))
            .EnsureSuccessStatusCode();
        var winner = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(1)));
        winner.Should().NotBe(received.Task);

        var rejoin = () => memberHub.InvokeAsync("JoinProject", board.Project.Id);
        await rejoin.Should().ThrowAsync<Microsoft.AspNetCore.SignalR.HubException>();

        await outsiderHub.StopAsync();
        await memberHub.StopAsync();
    }

    [Fact]
    public async Task RemoveUserFromProject_DropsThatProjectsGroupOnly()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "hub-project@test.com");
        var task = await BoardTestSetup.CreateTaskAsync(board, "One project");

        await using var connection = BoardTestSetup.Hub(_factory, board.User.Tokens.AccessToken);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<JsonElement>("CommentCreated", _ => received.TrySetResult());
        await connection.StartAsync();
        await connection.InvokeAsync("JoinProject", board.Project.Id);

        using (var scope = _factory.Services.CreateScope())
        {
            var membership = scope.ServiceProvider.GetRequiredService<IRealtimeGroupMembership>();
            await membership.RemoveUserFromProjectAsync(board.Project.Id, board.User.Tokens.User.Id);
        }

        (await board.Client.PostAsJsonAsync($"/api/tasks/{task.Id}/comments", new { body = "Missed" })).EnsureSuccessStatusCode();
        var missed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(1)));
        missed.Should().NotBe(received.Task);

        await connection.InvokeAsync("JoinProject", board.Project.Id);
        var again = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<JsonElement>("CommentCreated", _ => again.TrySetResult());
        (await board.Client.PostAsJsonAsync($"/api/tasks/{task.Id}/comments", new { body = "Seen" })).EnsureSuccessStatusCode();
        await again.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await connection.StopAsync();
    }

    [Fact]
    public void SignalR_ClosesWhenTheAccessTokenExpires()
    {
        _ = _factory.CreateClient();
        var dataSource = _factory.Services.GetRequiredService<EndpointDataSource>();
        var options = dataSource.Endpoints
            .Select(endpoint => endpoint.Metadata.GetMetadata<HttpConnectionDispatcherOptions>())
            .FirstOrDefault(candidate => candidate is not null);
        options.Should().NotBeNull();
        options!.CloseOnAuthenticationExpiration.Should().BeTrue();
    }

    [Fact]
    public async Task ServerError_DoesNotEchoTheExceptionMessage()
    {
        var middleware = new ExceptionHandlingMiddleware(
            _ => throw new InvalidOperationException("SECRET_DB_PASSWORD"),
            NullLogger<ExceptionHandlingMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        body.Should().NotContain("SECRET_DB_PASSWORD");
        body.Should().Contain("An unexpected error occurred.");

        var clientError = new ExceptionHandlingMiddleware(
            _ => throw new BadRequestException("Assignee must be a member of this workspace."),
            NullLogger<ExceptionHandlingMiddleware>.Instance);
        var clientContext = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
        await clientError.InvokeAsync(clientContext);
        clientContext.Response.Body.Position = 0;
        var clientBody = await new StreamReader(clientContext.Response.Body).ReadToEndAsync();
        clientBody.Should().Contain("Assignee must be a member of this workspace.");
    }

    [Fact]
    public async Task MigrationDown_DoesNotStoreGuidEmptyForMissingToColumnId()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "migration-down@test.com");
        var task = await BoardTestSetup.CreateTaskAsync(board, "History");
        (await board.Client.PostAsJsonAsync($"/api/tasks/{task.Id}/comments", new { body = "temporary" }))
            .EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
        var migrator = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>().Database.GetService<IMigrator>()
            ?? throw new InvalidOperationException("IMigrator was not registered.");

        await migrator.MigrateAsync("20260822161509_AddWorkMetrics");

        var empty = await db.Database.SqlQuery<int>(
            $"""SELECT COUNT(*)::int AS "Value" FROM "TaskActivities" WHERE "ToColumnId" = {Guid.Empty}""")
            .ToListAsync();
        empty.Should().ContainSingle().Which.Should().Be(0);

        await migrator.MigrateAsync();
    }

    private static async Task<Guid> CreateForeignColumn(HttpClient client, Guid workspaceId, string key, string columnName)
    {
        var projectResponse = await client.PostAsJsonAsync($"/api/workspaces/{workspaceId}/projects",
            new { name = "Sibling", key });
        projectResponse.EnsureSuccessStatusCode();
        var project = (await projectResponse.Content.ReadFromJsonAsync<Worklane.Application.Projects.Dtos.ProjectDto>(TestAuthHelper.JsonOptions))!;
        var columns = await (await client.GetAsync($"/api/projects/{project.Id}/columns"))
            .Content.ReadFromJsonAsync<List<BoardColumnDto>>(TestAuthHelper.JsonOptions);
        var column = columns!.Single(c => c.Name == "To Do");
        (await client.PatchAsJsonAsync($"/api/columns/{column.Id}", new { name = columnName })).EnsureSuccessStatusCode();
        return column.Id;
    }

    private static async Task<HttpResponseMessage> UploadAsync(
        HttpClient client, Guid taskId, byte[] bytes, string contentType, string fileName)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(file, "file", fileName);
        return await client.PostAsync($"/api/tasks/{taskId}/attachments", content);
    }
}

public class RestrictedExtensionTests : IClassFixture<RestrictedExtensionFactory>
{
    private readonly RestrictedExtensionFactory _factory;

    public RestrictedExtensionTests(RestrictedExtensionFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ConfiguredExtensionList_ReplacesTheBuiltInOne()
    {
        var options = _factory.Services.GetRequiredService<IOptions<FileStorageSettings>>().Value;
        options.AllowedExtensions.Should().Equal(".txt");
        options.AllowedExtensions.Should().NotContain(".png");

        var board = await BoardTestSetup.CreateAsync(_factory, "ext-owner@test.com");
        var task = await BoardTestSetup.CreateTaskAsync(board, "Text only");

        (await Upload(board.Client, task.Id, SampleFiles.Png, "image/png", "shot.png"))
            .StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        (await Upload(board.Client, task.Id, SampleFiles.Text, "application/octet-stream", "note.txt"))
            .StatusCode.Should().Be(HttpStatusCode.Created);
    }

    private static async Task<HttpResponseMessage> Upload(
        HttpClient client, Guid taskId, byte[] bytes, string contentType, string fileName)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(file, "file", fileName);
        return await client.PostAsync($"/api/tasks/{taskId}/attachments", content);
    }
}

public class RestrictedExtensionFactory : WorklaneWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FileStorage:AllowedExtensions:0"] = ".txt",
                ["FileStorage:AllowedContentTypes:0"] = "text/plain",
            });
        });
    }
}

public class KestrelUploadLimitTests : IClassFixture<KestrelUploadFactory>
{
    private readonly KestrelUploadFactory _factory;

    public KestrelUploadLimitTests(KestrelUploadFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task AttachmentUpload_OverTheEndpointLimit_Is413_OtherEndpointsKeepThirtyMegabytes()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "kestrel-owner@test.com");
        var task = await BoardTestSetup.CreateTaskAsync(board, "Big");

        var options = _factory.Services.GetRequiredService<IOptions<FileStorageSettings>>().Value;
        var overEndpoint = new byte[options.AttachmentRequestLimit + 1];

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(overEndpoint);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "big.png");
        var upload = await board.Client.PostAsync($"/api/tasks/{task.Id}/attachments", form);
        var uploadBody = await upload.Content.ReadAsStringAsync();
        upload.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge, uploadBody);
        uploadBody.Should().Contain("exceeds the size limit");
        uploadBody.Should().NotContain("File exceeds the maximum size");

        var loginBody = new ByteArrayContent(overEndpoint);
        loginBody.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var login = await board.Client.PostAsync("/api/auth/login", loginBody);
        login.StatusCode.Should().NotBe(HttpStatusCode.RequestEntityTooLarge);
        options.AttachmentRequestLimit.Should().Be(WorklaneWebApplicationFactory.TestMaxUploadBytes + FileStorageSettings.UploadEnvelopeBytes);
        options.AttachmentRequestLimit.Should().BeLessThan(FileStorageSettings.GlobalRequestBodyLimitBytes);
    }
}

public class KestrelUploadFactory : WorklaneWebApplicationFactory
{
    public KestrelUploadFactory()
    {
        UseKestrel();
    }
}
