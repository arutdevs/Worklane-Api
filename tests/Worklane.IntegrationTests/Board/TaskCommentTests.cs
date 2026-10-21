using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR.Client;
using Worklane.Application.Board.Dtos;
using Worklane.Domain.Enums;
using Worklane.IntegrationTests.Infrastructure;

namespace Worklane.IntegrationTests.Board;

public class TaskCommentTests : IClassFixture<WorklaneWebApplicationFactory>
{
    private readonly WorklaneWebApplicationFactory _factory;

    public TaskCommentTests(WorklaneWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Comments_CreateListEditAndDelete_WithAuthorAndAdminRules()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "comments-owner@test.com", "Owner");
        var task = await BoardTestSetup.CreateTaskAsync(board, "Discuss");

        var member = await TestAuthHelper.RegisterAsync(_factory.CreateClient(), "comments-member@test.com", "Member");
        var other = await TestAuthHelper.RegisterAsync(_factory.CreateClient(), "comments-other@test.com", "Other");
        var admin = await TestAuthHelper.RegisterAsync(_factory.CreateClient(), "comments-admin@test.com", "Admin");
        await BoardTestSetup.InviteAsync(board.Client, board.User.Workspace.Id, member.Tokens.User.Email, "Member");
        await BoardTestSetup.InviteAsync(board.Client, board.User.Workspace.Id, other.Tokens.User.Email, "Member");
        await BoardTestSetup.InviteAsync(board.Client, board.User.Workspace.Id, admin.Tokens.User.Email, "Admin");

        var firstResponse = await member.Client.PostAsJsonAsync($"/api/tasks/{task.Id}/comments", new { body = "  First note  " });
        firstResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var first = (await firstResponse.Content.ReadFromJsonAsync<CommentDto>(TestAuthHelper.JsonOptions))!;
        first.Body.Should().Be("First note");
        first.UpdatedAt.Should().BeNull();
        first.Author.Should().Be(new UserRef(member.Tokens.User.Id, "Member"));
        first.TaskId.Should().Be(task.Id);

        var secondResponse = await member.Client.PostAsJsonAsync($"/api/tasks/{task.Id}/comments", new { body = "Second" });
        secondResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var listed = await (await board.Client.GetAsync($"/api/tasks/{task.Id}/comments"))
            .Content.ReadFromJsonAsync<List<CommentDto>>(TestAuthHelper.JsonOptions);
        listed!.Select(c => c.Body).Should().Equal("First note", "Second");

        var editedResponse = await member.Client.PatchAsJsonAsync($"/api/comments/{first.Id}", new { body = "First note, revised" });
        editedResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var edited = (await editedResponse.Content.ReadFromJsonAsync<CommentDto>(TestAuthHelper.JsonOptions))!;
        edited.Body.Should().Be("First note, revised");
        edited.UpdatedAt.Should().NotBeNull();
        edited.CreatedAt.Should().BeCloseTo(first.CreatedAt, TimeSpan.FromMilliseconds(1));

        (await other.Client.PatchAsJsonAsync($"/api/comments/{first.Id}", new { body = "Nope" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await admin.Client.PatchAsJsonAsync($"/api/comments/{first.Id}", new { body = "Nope" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await other.Client.DeleteAsync($"/api/comments/{first.Id}"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await admin.Client.DeleteAsync($"/api/comments/{first.Id}"))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        var remaining = await (await board.Client.GetAsync($"/api/tasks/{task.Id}/comments"))
            .Content.ReadFromJsonAsync<List<CommentDto>>(TestAuthHelper.JsonOptions);
        remaining.Should().ContainSingle().Which.Body.Should().Be("Second");

        var second = remaining![0];
        (await member.Client.DeleteAsync($"/api/comments/{second.Id}"))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var activity = await (await board.Client.GetAsync($"/api/tasks/{task.Id}/activity"))
            .Content.ReadFromJsonAsync<List<TaskActivityDto>>(TestAuthHelper.JsonOptions);
        activity!.Select(a => a.Type).Should().Contain(new[]
        {
            TaskActivityType.CommentAdded,
            TaskActivityType.CommentEdited,
            TaskActivityType.CommentDeleted
        });
    }

    [Fact]
    public async Task EmptyComment_IsRejected()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "comments-empty@test.com");
        var task = await BoardTestSetup.CreateTaskAsync(board, "Card");

        var response = await board.Client.PostAsJsonAsync($"/api/tasks/{task.Id}/comments", new { body = "   " });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Viewer_CanList_ButCannotComment()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "comments-viewer-owner@test.com");
        var task = await BoardTestSetup.CreateTaskAsync(board, "Read only");
        var viewer = await TestAuthHelper.RegisterAsync(_factory.CreateClient(), "comments-viewer@test.com");
        await BoardTestSetup.InviteAsync(board.Client, board.User.Workspace.Id, viewer.Tokens.User.Email, "Viewer");

        (await viewer.Client.GetAsync($"/api/tasks/{task.Id}/comments")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await viewer.Client.PostAsJsonAsync($"/api/tasks/{task.Id}/comments", new { body = "Hi" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var comment = await (await board.Client.PostAsJsonAsync($"/api/tasks/{task.Id}/comments", new { body = "Owner note" }))
            .Content.ReadFromJsonAsync<CommentDto>(TestAuthHelper.JsonOptions);
        (await viewer.Client.DeleteAsync($"/api/comments/{comment!.Id}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task DemotedAuthor_CannotDeleteTheirComment()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "comments-demote-owner@test.com", "Owner");
        var task = await BoardTestSetup.CreateTaskAsync(board, "Demote");
        var member = await TestAuthHelper.RegisterAsync(_factory.CreateClient(), "comments-demote-member@test.com", "Author");
        await BoardTestSetup.InviteAsync(board.Client, board.User.Workspace.Id, member.Tokens.User.Email, "Member");

        var comment = await (await member.Client.PostAsJsonAsync($"/api/tasks/{task.Id}/comments", new { body = "Mine" }))
            .Content.ReadFromJsonAsync<CommentDto>(TestAuthHelper.JsonOptions);
        (await board.Client.PatchAsJsonAsync(
            $"/api/workspaces/{board.User.Workspace.Id}/members/{member.Tokens.User.Id}",
            new { role = "Viewer" })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await member.Client.DeleteAsync($"/api/comments/{comment!.Id}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var stillThere = await (await board.Client.GetAsync($"/api/tasks/{task.Id}/comments"))
            .Content.ReadFromJsonAsync<List<CommentDto>>(TestAuthHelper.JsonOptions);
        stillThere.Should().ContainSingle().Which.Id.Should().Be(comment.Id);
    }

    [Fact]
    public async Task Outsider_CannotReadOrChangeComments()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "comments-tenant-owner@test.com");
        var task = await BoardTestSetup.CreateTaskAsync(board, "Hidden");
        var created = await (await board.Client.PostAsJsonAsync($"/api/tasks/{task.Id}/comments", new { body = "Secret" }))
            .Content.ReadFromJsonAsync<CommentDto>(TestAuthHelper.JsonOptions);
        var outsider = await TestAuthHelper.RegisterAsync(_factory.CreateClient(), "comments-tenant-out@test.com");

        (await outsider.Client.GetAsync($"/api/tasks/{task.Id}/comments")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await outsider.Client.PatchAsJsonAsync($"/api/comments/{created!.Id}", new { body = "Stolen" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await outsider.Client.DeleteAsync($"/api/comments/{created.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SignalR_BroadcastsCommentCreatedUpdatedAndDeleted()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "comments-hub@test.com", "Hub User");
        var task = await BoardTestSetup.CreateTaskAsync(board, "Live");

        await using var connection = BoardTestSetup.Hub(_factory, board.User.Tokens.AccessToken);
        var created = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var updated = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deleted = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<JsonElement>("CommentCreated", payload => created.TrySetResult(payload));
        connection.On<JsonElement>("CommentUpdated", payload => updated.TrySetResult(payload));
        connection.On<JsonElement>("CommentDeleted", payload => deleted.TrySetResult(payload));

        await connection.StartAsync();
        await connection.InvokeAsync("JoinProject", board.Project.Id);

        var comment = await (await board.Client.PostAsJsonAsync($"/api/tasks/{task.Id}/comments", new { body = "Hello" }))
            .Content.ReadFromJsonAsync<CommentDto>(TestAuthHelper.JsonOptions);
        var createdPayload = await created.Task.WaitAsync(TimeSpan.FromSeconds(10));
        createdPayload.GetProperty("projectId").GetGuid().Should().Be(board.Project.Id);
        createdPayload.GetProperty("id").GetGuid().Should().Be(comment!.Id);
        createdPayload.GetProperty("taskId").GetGuid().Should().Be(task.Id);
        createdPayload.GetProperty("body").GetString().Should().Be("Hello");
        createdPayload.GetProperty("updatedAt").ValueKind.Should().Be(JsonValueKind.Null);
        createdPayload.GetProperty("author").GetProperty("userId").GetGuid().Should().Be(board.User.Tokens.User.Id);
        createdPayload.GetProperty("author").GetProperty("displayName").GetString().Should().Be("Hub User");

        await board.Client.PatchAsJsonAsync($"/api/comments/{comment.Id}", new { body = "Hello again" });
        var updatedPayload = await updated.Task.WaitAsync(TimeSpan.FromSeconds(10));
        updatedPayload.GetProperty("projectId").GetGuid().Should().Be(board.Project.Id);
        updatedPayload.GetProperty("body").GetString().Should().Be("Hello again");
        updatedPayload.GetProperty("updatedAt").GetDateTimeOffset().Should().NotBe(default);

        await board.Client.DeleteAsync($"/api/comments/{comment.Id}");
        var deletedPayload = await deleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        deletedPayload.GetProperty("projectId").GetGuid().Should().Be(board.Project.Id);
        deletedPayload.GetProperty("taskId").GetGuid().Should().Be(task.Id);
        deletedPayload.GetProperty("commentId").GetGuid().Should().Be(comment.Id);

        await connection.StopAsync();
    }

    [Fact]
    public async Task Edit_DoesNotStoreOldValueInActivity()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "comments-edit-redact@test.com");
        var task = await BoardTestSetup.CreateTaskAsync(board, "Edit");
        const string original = "old-value-secret";
        const string revised = "new-value-secret";

        var created = await (await board.Client.PostAsJsonAsync($"/api/tasks/{task.Id}/comments", new { body = original }))
            .Content.ReadFromJsonAsync<CommentDto>(TestAuthHelper.JsonOptions);
        (await board.Client.PatchAsJsonAsync($"/api/comments/{created!.Id}", new { body = revised }))
            .EnsureSuccessStatusCode();

        var activity = await (await board.Client.GetAsync($"/api/tasks/{task.Id}/activity"))
            .Content.ReadFromJsonAsync<List<TaskActivityDto>>(TestAuthHelper.JsonOptions);
        var edited = activity!.Single(a => a.Type == TaskActivityType.CommentEdited);
        edited.Details!.Value.GetProperty("commentId").GetGuid().Should().Be(created.Id);
        edited.Details.Value.GetProperty("edited").GetBoolean().Should().BeTrue();
        edited.Details.Value.TryGetProperty("oldValue", out _).Should().BeFalse();
        edited.Details.Value.TryGetProperty("newValue", out _).Should().BeFalse();
        edited.Details.Value.TryGetProperty("body", out _).Should().BeFalse();
        var added = activity!.Single(a => a.Type == TaskActivityType.CommentAdded);
        added.Details!.Value.GetProperty("commentId").GetGuid().Should().Be(created.Id);
        added.Details.Value.TryGetProperty("body", out _).Should().BeFalse();

        var text = await (await board.Client.GetAsync($"/api/tasks/{task.Id}/activity")).Content.ReadAsStringAsync();
        text.Should().NotContain(original);
        text.Should().NotContain(revised);
    }

    [Fact]
    public async Task Delete_ScrubsCommentTextFromActivity()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "comments-scrub@test.com");
        var task = await BoardTestSetup.CreateTaskAsync(board, "Redact me");
        const string original = "super-secret-phrase";
        const string revised = "other-secret-phrase";

        var created = await (await board.Client.PostAsJsonAsync($"/api/tasks/{task.Id}/comments", new { body = original }))
            .Content.ReadFromJsonAsync<CommentDto>(TestAuthHelper.JsonOptions);
        (await board.Client.PatchAsJsonAsync($"/api/comments/{created!.Id}", new { body = revised }))
            .EnsureSuccessStatusCode();

        var before = await (await board.Client.GetAsync($"/api/tasks/{task.Id}/activity"))
            .Content.ReadFromJsonAsync<List<TaskActivityDto>>(TestAuthHelper.JsonOptions);
        var editedRow = before!.Single(a => a.Type == TaskActivityType.CommentEdited);
        editedRow.Details!.Value.GetProperty("commentId").GetGuid().Should().Be(created.Id);
        editedRow.Details.Value.GetProperty("edited").GetBoolean().Should().BeTrue();
        editedRow.Details.Value.TryGetProperty("oldValue", out _).Should().BeFalse();
        editedRow.Details.Value.TryGetProperty("newValue", out _).Should().BeFalse();
        var beforeText = await (await board.Client.GetAsync($"/api/tasks/{task.Id}/activity")).Content.ReadAsStringAsync();
        beforeText.Should().NotContain(original);
        beforeText.Should().NotContain(revised);

        (await board.Client.DeleteAsync($"/api/comments/{created.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var after = await (await board.Client.GetAsync($"/api/tasks/{task.Id}/activity")).Content.ReadAsStringAsync();
        after.Should().NotContain(original);
        after.Should().NotContain(revised);
        after.Should().Contain("CommentDeleted");
        after.Should().Contain(created.Id.ToString());
    }

    [Fact]
    public async Task AdminOfAnotherWorkspace_CannotEditAComment()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "comments-ws-a@test.com", "Owner A");
        var task = await BoardTestSetup.CreateTaskAsync(board, "Private");
        var comment = await (await board.Client.PostAsJsonAsync($"/api/tasks/{task.Id}/comments", new { body = "Stay here" }))
            .Content.ReadFromJsonAsync<CommentDto>(TestAuthHelper.JsonOptions);

        var otherAdmin = await TestAuthHelper.RegisterAsync(_factory.CreateClient(), "comments-ws-b@test.com", "Owner B");

        (await otherAdmin.Client.PatchAsJsonAsync($"/api/comments/{comment!.Id}", new { body = "Taken" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SignalR_TaskUpdatedCarriesCommentCount()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "comments-counts-hub@test.com");
        var task = await BoardTestSetup.CreateTaskAsync(board, "Badge");

        await using var connection = BoardTestSetup.Hub(_factory, board.User.Tokens.AccessToken);
        var updates = new List<JsonElement>();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<JsonElement>("TaskUpdated", payload =>
        {
            updates.Add(payload.Clone());
            if (updates.Count == 1) first.TrySetResult();
            if (updates.Count >= 2) second.TrySetResult();
        });
        await connection.StartAsync();
        await connection.InvokeAsync("JoinProject", board.Project.Id);

        var comment = await (await board.Client.PostAsJsonAsync($"/api/tasks/{task.Id}/comments", new { body = "One" }))
            .Content.ReadFromJsonAsync<CommentDto>(TestAuthHelper.JsonOptions);
        await first.Task.WaitAsync(TimeSpan.FromSeconds(10));
        (await board.Client.PatchAsJsonAsync($"/api/comments/{comment!.Id}", new { body = "One revised" }))
            .EnsureSuccessStatusCode();
        await Task.Delay(TimeSpan.FromSeconds(1));
        updates.Should().ContainSingle();

        await board.Client.DeleteAsync($"/api/comments/{comment.Id}");
        await second.Task.WaitAsync(TimeSpan.FromSeconds(10));

        updates[0].GetProperty("id").GetGuid().Should().Be(task.Id);
        updates[0].GetProperty("commentCount").GetInt32().Should().Be(1);
        updates[1].GetProperty("commentCount").GetInt32().Should().Be(0);

        await connection.StopAsync();
    }
}
