using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
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
using Worklane.Domain.Enums;
using Worklane.Infrastructure.Persistence;
using Worklane.IntegrationTests.Infrastructure;
using Worklane.WebApi.Filters;
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
    public async Task Cors_ExposesContentDispositionAndRetryAfter()
    {
        var client = _factory.CreateClient();
        foreach (var origin in new[] { "http://localhost:4200", "http://localhost:8080" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login");
            request.Headers.TryAddWithoutValidation("Origin", origin);
            request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");

            var response = await client.SendAsync(request);
            response.Headers.TryGetValues("Access-Control-Allow-Origin", out var allowed).Should().BeTrue();
            allowed.Should().ContainSingle().Which.Should().Be(origin);
            response.Headers.TryGetValues("Access-Control-Expose-Headers", out var exposed).Should().BeTrue();
            var header = string.Join(',', exposed ?? []);
            header.Should().Contain("Content-Disposition");
            header.Should().Contain("Retry-After");
        }
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
    public async Task DeleteColumn_RejectsTargetFromAnotherProjectOrWorkspace()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "delete-column-owner@test.com", "Owner");
        var task = await BoardTestSetup.CreateTaskAsync(board, "Must stay");

        var sameWorkspace = await CreateForeignColumn(board.Client, board.User.Workspace.Id, "DC", "ForeignSameWorkspace");
        var outsider = await TestAuthHelper.RegisterAsync(_factory.CreateClient(), "delete-column-other@test.com", "Outsider");
        var otherProject = await outsider.Client.PostAsJsonAsync($"/api/workspaces/{outsider.Workspace.Id}/projects",
            new { name = "Other", key = "DY" });
        otherProject.EnsureSuccessStatusCode();
        var other = (await otherProject.Content.ReadFromJsonAsync<Worklane.Application.Projects.Dtos.ProjectDto>(TestAuthHelper.JsonOptions))!;
        var otherColumns = await (await outsider.Client.GetAsync($"/api/projects/{other.Id}/columns"))
            .Content.ReadFromJsonAsync<List<BoardColumnDto>>(TestAuthHelper.JsonOptions);
        var otherColumn = otherColumns!.Single(c => c.Name == "To Do");

        foreach (var targetColumnId in new[] { sameWorkspace, otherColumn.Id })
        {
            var deleted = await board.Client.DeleteAsync(
                $"/api/columns/{board.Todo.Id}?targetColumnId={targetColumnId}");
            deleted.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        var reloaded = await (await board.Client.GetAsync($"/api/tasks/{task.Id}"))
            .Content.ReadFromJsonAsync<TaskItemDto>(TestAuthHelper.JsonOptions);
        reloaded!.ColumnId.Should().Be(board.Todo.Id);

        var columns = await (await board.Client.GetAsync($"/api/projects/{board.Project.Id}/columns"))
            .Content.ReadFromJsonAsync<List<BoardColumnDto>>(TestAuthHelper.JsonOptions);
        columns!.Should().Contain(column => column.Id == board.Todo.Id);
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
        var rejectedBody = await rejected.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        rejectedBody.GetProperty("code").GetString().Should().Be("assignee_not_member");

        var accepted = await board.Client.PostAsJsonAsync($"/api/projects/{board.Project.Id}/tasks", new
        {
            title = "Mine",
            priority = "Medium",
            assigneeId = board.User.Tokens.User.Id
        });
        accepted.StatusCode.Should().Be(HttpStatusCode.Created);

        var task = (await accepted.Content.ReadFromJsonAsync<TaskItemDto>(TestAuthHelper.JsonOptions))!;
        var patched = await board.Client.PatchAsJsonAsync($"/api/tasks/{task.Id}", new { assigneeId = outsider.Tokens.User.Id });
        patched.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var patchedBody = await patched.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions);
        patchedBody.GetProperty("code").GetString().Should().Be("assignee_not_member");
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
    public async Task OneConnection_JoiningTwoProjects_IsRemovedFromBoth()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "hub-two-owner@test.com", "Owner");
        var task = await BoardTestSetup.CreateTaskAsync(board, "First");
        var secondResponse = await board.Client.PostAsJsonAsync($"/api/workspaces/{board.User.Workspace.Id}/projects",
            new { name = "Second", key = "SC" });
        secondResponse.EnsureSuccessStatusCode();
        var second = (await secondResponse.Content.ReadFromJsonAsync<Worklane.Application.Projects.Dtos.ProjectDto>(TestAuthHelper.JsonOptions))!;
        var secondColumns = await (await board.Client.GetAsync($"/api/projects/{second.Id}/columns"))
            .Content.ReadFromJsonAsync<List<BoardColumnDto>>(TestAuthHelper.JsonOptions);
        var secondTaskResponse = await board.Client.PostAsJsonAsync($"/api/projects/{second.Id}/tasks", new
        {
            title = "Second",
            priority = "Medium",
            columnId = secondColumns!.Single(c => c.Name == "To Do").Id
        });
        secondTaskResponse.EnsureSuccessStatusCode();
        var secondTask = (await secondTaskResponse.Content.ReadFromJsonAsync<TaskItemDto>(TestAuthHelper.JsonOptions))!;

        var member = await TestAuthHelper.RegisterAsync(_factory.CreateClient(), "hub-two-member@test.com", "Member");
        await BoardTestSetup.InviteAsync(board.Client, board.User.Workspace.Id, member.Tokens.User.Email, "Member");

        await using var connection = BoardTestSetup.Hub(_factory, member.Tokens.AccessToken);
        var seen = new List<Guid>();
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<JsonElement>("CommentCreated", payload =>
        {
            lock (seen)
            {
                seen.Add(payload.GetProperty("projectId").GetGuid());
                if (seen.Count >= 2) both.TrySetResult();
            }
        });
        await connection.StartAsync();
        await connection.InvokeAsync("JoinProject", board.Project.Id);
        await connection.InvokeAsync("JoinProject", second.Id);

        (await board.Client.PostAsJsonAsync($"/api/tasks/{task.Id}/comments", new { body = "one" })).EnsureSuccessStatusCode();
        (await board.Client.PostAsJsonAsync($"/api/tasks/{secondTask.Id}/comments", new { body = "two" })).EnsureSuccessStatusCode();
        await both.Task.WaitAsync(TimeSpan.FromSeconds(10));
        seen.Should().BeEquivalentTo(new[] { board.Project.Id, second.Id });

        (await board.Client.DeleteAsync($"/api/workspaces/{board.User.Workspace.Id}/members/{member.Tokens.User.Id}"))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var after = new List<Guid>();
        connection.On<JsonElement>("CommentCreated", payload =>
        {
            lock (after) after.Add(payload.GetProperty("projectId").GetGuid());
        });
        (await board.Client.PostAsJsonAsync($"/api/tasks/{task.Id}/comments", new { body = "later-one" })).EnsureSuccessStatusCode();
        (await board.Client.PostAsJsonAsync($"/api/tasks/{secondTask.Id}/comments", new { body = "later-two" })).EnsureSuccessStatusCode();
        await Task.Delay(TimeSpan.FromSeconds(1));
        after.Should().BeEmpty();

        var rejoin = () => connection.InvokeAsync("JoinProject", board.Project.Id);
        await rejoin.Should().ThrowAsync<Microsoft.AspNetCore.SignalR.HubException>();
        await connection.StopAsync();
    }

    [Fact]
    public async Task JoinProject_RejectsAnArchivedProject()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "hub-archived@test.com");
        await using var connection = BoardTestSetup.Hub(_factory, board.User.Tokens.AccessToken);
        await connection.StartAsync();
        (await board.Client.DeleteAsync($"/api/projects/{board.Project.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var join = () => connection.InvokeAsync("JoinProject", board.Project.Id);
        await join.Should().ThrowAsync<Microsoft.AspNetCore.SignalR.HubException>();
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
    public async Task FrameworkError_UsesAFixedMessage_AndKeepsValidationText()
    {
        const string secret = "SECRET_KESTREL_INTERNAL_PATH";

        var badRequest = new ExceptionHandlingMiddleware(
            _ => throw new BadHttpRequestException(secret, StatusCodes.Status400BadRequest),
            NullLogger<ExceptionHandlingMiddleware>.Instance);
        var badRequestContext = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
        await badRequest.InvokeAsync(badRequestContext);
        badRequestContext.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        badRequestContext.Response.Body.Position = 0;
        var badRequestBody = await new StreamReader(badRequestContext.Response.Body).ReadToEndAsync();
        badRequestBody.Should().NotContain(secret);
        badRequestBody.Should().Contain("The request is invalid.");
        JsonDocument.Parse(badRequestBody).RootElement.GetProperty("code").GetString().Should().Be("invalid_request_body");

        var tooLarge = new ExceptionHandlingMiddleware(
            _ => throw new InvalidDataException(secret, new BadHttpRequestException(secret, StatusCodes.Status413PayloadTooLarge)),
            NullLogger<ExceptionHandlingMiddleware>.Instance);
        var tooLargeContext = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
        await tooLarge.InvokeAsync(tooLargeContext);
        tooLargeContext.Response.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
        tooLargeContext.Response.Body.Position = 0;
        var tooLargeBody = await new StreamReader(tooLargeContext.Response.Body).ReadToEndAsync();
        tooLargeBody.Should().NotContain(secret);
        tooLargeBody.Should().Contain("The request body exceeds the size limit.");
        JsonDocument.Parse(tooLargeBody).RootElement.GetProperty("code").GetString().Should().Be("request_too_large");

        var validation = new ExceptionHandlingMiddleware(
            _ => throw new FluentValidation.ValidationException("Body is required."),
            NullLogger<ExceptionHandlingMiddleware>.Instance);
        var validationContext = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
        await validation.InvokeAsync(validationContext);
        validationContext.Response.Body.Position = 0;
        var validationBody = await new StreamReader(validationContext.Response.Body).ReadToEndAsync();
        validationBody.Should().Contain("Body is required.");
        JsonDocument.Parse(validationBody).RootElement.GetProperty("code").GetString().Should().Be("validation_failed");
    }

    [Fact]
    public void PayloadTooLarge_IsDetectedFromTheExceptionType()
    {
        var messageOnly = new ModelStateDictionary();
        messageOnly.AddModelError(string.Empty, "Request body too large. The max request body size is 10 bytes.");
        var messageContext = new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor(), messageOnly);
        ExceptionHandlingMiddleware.InvalidModelStateResponse(messageContext, _ => new BadRequestResult())
            .Should().BeOfType<BadRequestResult>();

        var typed = new ModelStateDictionary();
        typed.TryAddModelException(
            string.Empty,
            new InvalidDataException(
                "no marker text",
                new BadHttpRequestException("no marker text", StatusCodes.Status413PayloadTooLarge)));
        var typedContext = new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor(), typed);
        var result = ExceptionHandlingMiddleware.InvalidModelStateResponse(typedContext, _ => new BadRequestResult());
        var tooLargeResult = result.Should().BeOfType<ObjectResult>().Subject;
        tooLargeResult.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
        tooLargeResult.Value.Should().BeOfType<ProblemDetails>().Which.Extensions["code"].Should().Be("request_too_large");
    }

    [Fact]
    public async Task Migration_RedactsActivityForCommentsDeletedBeforeThisRelease()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "redact-legacy@test.com");
        var task = await BoardTestSetup.CreateTaskAsync(board, "Legacy");
        var live = await (await board.Client.PostAsJsonAsync($"/api/tasks/{task.Id}/comments", new { body = "still-here" }))
            .Content.ReadFromJsonAsync<CommentDto>(TestAuthHelper.JsonOptions);

        var legacyId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
            db.TaskActivities.Add(new Worklane.Domain.Entities.TaskActivity
            {
                Id = Guid.NewGuid(),
                TaskId = task.Id,
                Type = TaskActivityType.CommentAdded,
                ActorUserId = board.User.Tokens.User.Id,
                CreatedAt = DateTimeOffset.UtcNow,
                Details = $$"""{"commentId":"{{legacyId}}","body":"legacy-secret","oldValue":"legacy-secret"}"""
            });
            db.TaskActivities.Add(new Worklane.Domain.Entities.TaskActivity
            {
                Id = Guid.NewGuid(),
                TaskId = task.Id,
                Type = TaskActivityType.CommentEdited,
                ActorUserId = board.User.Tokens.User.Id,
                CreatedAt = DateTimeOffset.UtcNow,
                Details = $$"""{"commentId":"{{legacyId}}","oldValue":"legacy-secret","newValue":"legacy-secret","edited":true}"""
            });
            db.TaskActivities.Add(new Worklane.Domain.Entities.TaskActivity
            {
                Id = Guid.NewGuid(),
                TaskId = task.Id,
                Type = TaskActivityType.CommentAdded,
                ActorUserId = board.User.Tokens.User.Id,
                CreatedAt = DateTimeOffset.UtcNow,
                Details = $$"""{"commentId":"{{live!.Id}}","body":"still-here"}"""
            });
            db.TaskActivities.Add(new Worklane.Domain.Entities.TaskActivity
            {
                Id = Guid.NewGuid(),
                TaskId = task.Id,
                Type = TaskActivityType.CommentEdited,
                ActorUserId = board.User.Tokens.User.Id,
                CreatedAt = DateTimeOffset.UtcNow,
                Details = $$"""{"commentId":"{{live.Id}}","oldValue":"still-here","newValue":"still-here","edited":true}"""
            });
            await db.SaveChangesAsync();

            // ExecuteSqlRaw formats {n} placeholders. The redaction regex uses
            // {8}/{4}/{12}, so it has to run as a raw command. Twice, because the
            // statement must be safe to run again.
            var connection = db.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open)
                await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = DeletedCommentActivityRedaction.Sql;
            await command.ExecuteNonQueryAsync();
            await command.ExecuteNonQueryAsync();
        }

        var activity = await (await board.Client.GetAsync($"/api/tasks/{task.Id}/activity"))
            .Content.ReadFromJsonAsync<List<TaskActivityDto>>(TestAuthHelper.JsonOptions);
        var legacyEdit = activity!.Single(row =>
            row.Type == TaskActivityType.CommentEdited &&
            row.Details!.Value.GetProperty("commentId").GetGuid() == legacyId);
        legacyEdit.Details!.Value.TryGetProperty("oldValue", out _).Should().BeFalse();
        legacyEdit.Details.Value.TryGetProperty("newValue", out _).Should().BeFalse();
        var liveEdit = activity!.Single(row =>
            row.Type == TaskActivityType.CommentEdited &&
            row.Details!.Value.GetProperty("commentId").GetGuid() == live!.Id);
        liveEdit.Details!.Value.GetProperty("edited").GetBoolean().Should().BeTrue();
        liveEdit.Details.Value.TryGetProperty("oldValue", out _).Should().BeFalse();
        liveEdit.Details.Value.TryGetProperty("newValue", out _).Should().BeFalse();
        var text = await (await board.Client.GetAsync($"/api/tasks/{task.Id}/activity")).Content.ReadAsStringAsync();
        text.Should().NotContain("legacy-secret");
        text.Should().NotContain("still-here");
        text.Should().Contain(live.Id.ToString());
        var comments = await (await board.Client.GetAsync($"/api/tasks/{task.Id}/comments"))
            .Content.ReadFromJsonAsync<List<CommentDto>>(TestAuthHelper.JsonOptions);
        comments!.Single().Body.Should().Be("still-here");
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

        await TaskAttachmentTests.AssertUnsupportedFileType(
            await Upload(board.Client, task.Id, SampleFiles.Png, "image/png", "shot.png"),
            ".png");
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
    public async Task AttachmentUpload_OverTheRequestSizeLimit_Is413_OtherRoutesStayAtTwoMegabytes()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "kestrel-owner@test.com");
        var task = await BoardTestSetup.CreateTaskAsync(board, "Big");

        var settings = _factory.Services.GetRequiredService<IOptions<FileStorageSettings>>().Value;
        var transportLimit = settings.AttachmentRequestLimit;
        var dataSource = _factory.Services.GetRequiredService<EndpointDataSource>();
        var uploadEndpoint = dataSource.Endpoints
            .FirstOrDefault(endpoint => endpoint.Metadata.GetMetadata<AttachmentUploadLimitAttribute>() is not null);
        uploadEndpoint.Should().NotBeNull();
        var uploadLimit = uploadEndpoint!.Metadata.GetMetadata<IRequestSizeLimitMetadata>();
        uploadLimit.Should().NotBeNull();
        uploadLimit!.MaxRequestBodySize.Should().Be(settings.AttachmentRequestLimit);
        settings.MaxBytes.Should().Be(3 * 1024 * 1024);
        transportLimit.Should().Be(settings.MaxBytes + FileStorageSettings.UploadEnvelopeBytes);
        new FileStorageSettings().AttachmentRequestLimit.Should().Be(11 * 1024 * 1024);
        FileStorageSettings.GlobalRequestBodyLimitBytes.Should().Be(2 * 1024 * 1024);
        transportLimit.Should().BeGreaterThan(FileStorageSettings.GlobalRequestBodyLimitBytes);

        var overGlobal = new byte[FileStorageSettings.GlobalRequestBodyLimitBytes + 1];
        using (var form = new MultipartFormDataContent())
        {
            var file = new ByteArrayContent(overGlobal);
            file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(file, "file", "mid.png");
            var raised = await board.Client.PostAsync($"/api/tasks/{task.Id}/attachments", form);
            var raisedBody = await raised.Content.ReadAsStringAsync();
            raised.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType, raisedBody);
            var raisedProblem = JsonDocument.Parse(raisedBody).RootElement;
            raisedProblem.GetProperty("code").GetString().Should().Be("unsupported_file_type");
            raisedProblem.GetProperty("extension").GetString().Should().Be(".png");
        }

        var overFile = new byte[settings.MaxBytes + 1];
        SampleFiles.Png.CopyTo(overFile, 0);
        using (var form = new MultipartFormDataContent())
        {
            var file = new ByteArrayContent(overFile);
            file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(file, "file", "cap.png");
            var capped = await board.Client.PostAsync($"/api/tasks/{task.Id}/attachments", form);
            var cappedBody = await capped.Content.ReadAsStringAsync();
            capped.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge, cappedBody);
            cappedBody.Should().Contain("File exceeds the maximum size");
            var cappedProblem = JsonDocument.Parse(cappedBody).RootElement;
            cappedProblem.GetProperty("code").GetString().Should().Be("file_too_large");
            cappedProblem.GetProperty("maxBytes").GetInt64().Should().Be(settings.MaxBytes);
        }

        var overEndpoint = new byte[transportLimit + 1];
        using (var form = new MultipartFormDataContent())
        {
            var file = new ByteArrayContent(overEndpoint);
            file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(file, "file", "big.png");
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/tasks/{task.Id}/attachments")
            {
                Content = form
            };
            // The endpoint ceiling is enforced while the body is still being written.
            // Expect: 100-continue lets Kestrel answer 413 from Content-Length
            // before this client copies the rest of an 11 MB body.
            request.Headers.ExpectContinue = true;
            var upload = await board.Client.SendAsync(request);
            var uploadBody = await upload.Content.ReadAsStringAsync();
            upload.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge, uploadBody);
            upload.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
            uploadBody.Should().Contain("exceeds the size limit");
            uploadBody.Should().NotContain("File exceeds the maximum size");
            JsonDocument.Parse(uploadBody).RootElement.GetProperty("code").GetString().Should().Be("request_too_large");
        }

        var loginBody = new ByteArrayContent(overGlobal);
        loginBody.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var login = await board.Client.PostAsync("/api/auth/login", loginBody);
        var loginText = await login.Content.ReadAsStringAsync();
        login.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge, loginText);
        login.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        JsonDocument.Parse(loginText).RootElement.GetProperty("code").GetString().Should().Be("request_too_large");

        // The oversized login above makes Kestrel abort that connection. A new client
        // avoids sending the follow-up on the pooled connection that was just reset,
        // and checks that a small body is not 413.
        using var followUp = _factory.CreateClient();
        var small = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        (await followUp.PostAsync("/api/auth/login", small)).StatusCode.Should().NotBe(HttpStatusCode.RequestEntityTooLarge);
    }
}

public class KestrelUploadFactory : WorklaneWebApplicationFactory
{
    public KestrelUploadFactory()
    {
        UseKestrel();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FileStorage:MaxBytes"] = (3 * 1024 * 1024).ToString()
            });
        });
    }
}
