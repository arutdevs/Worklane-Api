using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Worklane.Application.Board.Dtos;
using Worklane.IntegrationTests.Infrastructure;

namespace Worklane.IntegrationTests.Board;

public class E2EBackendFixTests : IClassFixture<WorklaneWebApplicationFactory>
{
    private readonly WorklaneWebApplicationFactory _factory;

    public E2EBackendFixTests(WorklaneWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData(" \n ")]
    public async Task CreateAndUpdateTask_RejectWhitespaceTitle(string title)
    {
        var board = await BoardTestSetup.CreateAsync(_factory);
        var created = await board.Client.PostAsJsonAsync($"/api/projects/{board.Project.Id}/tasks", new
        {
            title,
            priority = "Medium",
            columnId = board.Todo.Id
        });
        await AssertValidation(created, "Title", "not_empty");

        var task = await BoardTestSetup.CreateTaskAsync(board, "Keep me");
        using var body = new StringContent($"{{\"title\":{JsonSerializer.Serialize(title)}}}", Encoding.UTF8, "application/json");
        var updated = await board.Client.PatchAsync($"/api/tasks/{task.Id}", body);
        await AssertValidation(updated, "Title", "not_empty");

        var reloaded = await board.Client.GetAsync($"/api/tasks/{task.Id}");
        reloaded.EnsureSuccessStatusCode();
        var dto = await reloaded.Content.ReadFromJsonAsync<TaskItemDto>(TestAuthHelper.JsonOptions);
        dto!.Title.Should().Be("Keep me");
    }

    private static async Task AssertValidation(HttpResponseMessage response, string field, string fieldCode)
    {
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, text);
        using var doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("code").GetString().Should().Be("validation_failed");
        doc.RootElement.GetProperty("fieldCodes").GetProperty(field).EnumerateArray().Select(item => item.GetString())
            .Should().Contain(fieldCode);
    }
}
