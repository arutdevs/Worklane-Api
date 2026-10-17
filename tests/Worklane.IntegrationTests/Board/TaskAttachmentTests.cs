using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR.Client;
using Worklane.Application.Board.Dtos;
using Worklane.Domain.Enums;
using Worklane.IntegrationTests.Infrastructure;

namespace Worklane.IntegrationTests.Board;

public class TaskAttachmentTests : IClassFixture<WorklaneWebApplicationFactory>
{
    private readonly WorklaneWebApplicationFactory _factory;

    public TaskAttachmentTests(WorklaneWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Attachments_UploadListDownloadAndDelete_StayInsideTheWorkspace()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "files-owner@test.com", "Owner");
        var task = await BoardTestSetup.CreateTaskAsync(board, "With a file");
        var member = await TestAuthHelper.RegisterAsync(_factory.CreateClient(), "files-member@test.com", "Member");
        var outsider = await TestAuthHelper.RegisterAsync(_factory.CreateClient(), "files-out@test.com", "Outsider");
        await BoardTestSetup.InviteAsync(board.Client, board.User.Workspace.Id, member.Tokens.User.Email, "Member");

        var payload = SampleFiles.Png;
        var uploaded = await UploadAsync(board.Client, task.Id, payload, "text/html", "../../notes.png");
        uploaded.StatusCode.Should().Be(HttpStatusCode.Created);
        var file = (await uploaded.Content.ReadFromJsonAsync<AttachmentDto>(TestAuthHelper.JsonOptions))!;
        file.FileName.Should().Be("notes.png");
        file.ContentType.Should().Be("image/png");
        file.SizeBytes.Should().Be(payload.Length);
        file.TaskId.Should().Be(task.Id);
        file.UploadedBy.Should().Be(new UserRef(board.User.Tokens.User.Id, "Owner"));

        var listed = await (await board.Client.GetAsync($"/api/tasks/{task.Id}/attachments"))
            .Content.ReadFromJsonAsync<List<AttachmentDto>>(TestAuthHelper.JsonOptions);
        listed.Should().ContainSingle().Which.Id.Should().Be(file.Id);

        var download = await board.Client.GetAsync($"/api/attachments/{file.Id}/download");
        download.StatusCode.Should().Be(HttpStatusCode.OK);
        download.Content.Headers.ContentType!.MediaType.Should().Be("application/octet-stream");
        download.Headers.GetValues("X-Content-Type-Options").Should().ContainSingle().Which.Should().Be("nosniff");
        download.Headers.GetValues("Content-Security-Policy").Should().ContainSingle().Which.Should().Contain("default-src 'none'");
        download.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        download.Content.Headers.ContentDisposition.FileName.Should().Contain("notes.png");
        (await download.Content.ReadAsByteArrayAsync()).Should().Equal(payload);

        (await outsider.Client.GetAsync($"/api/tasks/{task.Id}/attachments")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await outsider.Client.GetAsync($"/api/attachments/{file.Id}/download")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await outsider.Client.DeleteAsync($"/api/attachments/{file.Id}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await member.Client.DeleteAsync($"/api/attachments/{file.Id}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await board.Client.GetAsync($"/api/attachments/{Guid.NewGuid()}/download")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await board.Client.DeleteAsync($"/api/attachments/{file.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var after = await (await board.Client.GetAsync($"/api/tasks/{task.Id}/attachments"))
            .Content.ReadFromJsonAsync<List<AttachmentDto>>(TestAuthHelper.JsonOptions);
        after.Should().BeEmpty();
        (await board.Client.GetAsync($"/api/attachments/{file.Id}/download")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var activity = await (await board.Client.GetAsync($"/api/tasks/{task.Id}/activity"))
            .Content.ReadFromJsonAsync<List<TaskActivityDto>>(TestAuthHelper.JsonOptions);
        activity!.Select(a => a.Type).Should().Contain(new[]
        {
            TaskActivityType.AttachmentAdded,
            TaskActivityType.AttachmentDeleted
        });
    }

    [Fact]
    public async Task Upload_RejectsDisallowedTypesEmptyFilesAndOversizedBodies()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "files-reject@test.com");
        var task = await BoardTestSetup.CreateTaskAsync(board, "Gate");

        (await UploadAsync(board.Client, task.Id, SampleFiles.Png, "image/png", "run.exe"))
            .StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        (await UploadAsync(board.Client, task.Id, SampleFiles.Html, "image/png", "notes.png"))
            .StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        (await UploadAsync(board.Client, task.Id, SampleFiles.Png, "image/png", "photo.jpg"))
            .StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        (await UploadAsync(board.Client, task.Id, [], "image/png", "empty.png"))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var tooBig = new byte[WorklaneWebApplicationFactory.TestMaxUploadBytes + 1];
        (await UploadAsync(board.Client, task.Id, tooBig, "image/png", "big.png"))
            .StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);

        var jpg = await UploadAsync(board.Client, task.Id, SampleFiles.Jpeg, "text/html", "photo.jpg");
        jpg.StatusCode.Should().Be(HttpStatusCode.Created);
        var stored = (await jpg.Content.ReadFromJsonAsync<AttachmentDto>(TestAuthHelper.JsonOptions))!;
        stored.ContentType.Should().Be("image/jpeg");
    }

    [Fact]
    public async Task Viewer_CannotUpload()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "files-viewer-owner@test.com");
        var task = await BoardTestSetup.CreateTaskAsync(board, "Read only");
        var viewer = await TestAuthHelper.RegisterAsync(_factory.CreateClient(), "files-viewer@test.com");
        await BoardTestSetup.InviteAsync(board.Client, board.User.Workspace.Id, viewer.Tokens.User.Email, "Viewer");

        (await UploadAsync(viewer.Client, task.Id, SampleFiles.Png, "image/png", "notes.png"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task SignalR_BroadcastsAttachmentAddedAndDeleted()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "files-hub@test.com", "Files");
        var task = await BoardTestSetup.CreateTaskAsync(board, "Live file");

        await using var connection = BoardTestSetup.Hub(_factory, board.User.Tokens.AccessToken);
        var added = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deleted = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<JsonElement>("AttachmentAdded", payload => added.TrySetResult(payload));
        connection.On<JsonElement>("AttachmentDeleted", payload => deleted.TrySetResult(payload));
        await connection.StartAsync();
        await connection.InvokeAsync("JoinProject", board.Project.Id);

        var response = await UploadAsync(board.Client, task.Id, SampleFiles.Pdf, "application/pdf", "spec.pdf");
        var file = (await response.Content.ReadFromJsonAsync<AttachmentDto>(TestAuthHelper.JsonOptions))!;

        var addedPayload = await added.Task.WaitAsync(TimeSpan.FromSeconds(10));
        addedPayload.GetProperty("projectId").GetGuid().Should().Be(board.Project.Id);
        addedPayload.GetProperty("id").GetGuid().Should().Be(file.Id);
        addedPayload.GetProperty("taskId").GetGuid().Should().Be(task.Id);
        addedPayload.GetProperty("fileName").GetString().Should().Be("spec.pdf");
        addedPayload.GetProperty("contentType").GetString().Should().Be("application/pdf");
        addedPayload.GetProperty("sizeBytes").GetInt64().Should().Be(SampleFiles.Pdf.Length);
        addedPayload.GetProperty("uploadedBy").GetProperty("displayName").GetString().Should().Be("Files");

        await board.Client.DeleteAsync($"/api/attachments/{file.Id}");
        var deletedPayload = await deleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        deletedPayload.GetProperty("projectId").GetGuid().Should().Be(board.Project.Id);
        deletedPayload.GetProperty("taskId").GetGuid().Should().Be(task.Id);
        deletedPayload.GetProperty("attachmentId").GetGuid().Should().Be(file.Id);

        await connection.StopAsync();
    }

    [Fact]
    public async Task Admin_CanDeleteAnotherMembersFile()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "files-admin-owner@test.com", "Owner");
        var task = await BoardTestSetup.CreateTaskAsync(board, "Shared");
        var member = await TestAuthHelper.RegisterAsync(_factory.CreateClient(), "files-admin-member@test.com", "Member");
        await BoardTestSetup.InviteAsync(board.Client, board.User.Workspace.Id, member.Tokens.User.Email, "Member");

        var uploaded = await UploadAsync(member.Client, task.Id, SampleFiles.Text, "application/octet-stream", "note.txt");
        var file = (await uploaded.Content.ReadFromJsonAsync<AttachmentDto>(TestAuthHelper.JsonOptions))!;

        (await board.Client.DeleteAsync($"/api/attachments/{file.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
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
