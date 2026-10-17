using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Worklane.Application.Board.Dtos;
using Worklane.Application.Projects.Dtos;

namespace Worklane.IntegrationTests.Infrastructure;

internal sealed record BoardContext(
    HttpClient Client,
    RegisteredUser User,
    ProjectDto Project,
    IReadOnlyList<BoardColumnDto> Columns)
{
    public BoardColumnDto Todo => Columns.Single(c => c.Name == "To Do");
    public BoardColumnDto InProgress => Columns.Single(c => c.Name == "In Progress");
    public BoardColumnDto Done => Columns.Single(c => c.Name == "Done");
}

internal static class BoardTestSetup
{
    public static async Task<BoardContext> CreateAsync(WorklaneWebApplicationFactory factory, string? email = null, string? displayName = null)
    {
        var client = factory.CreateClient();
        var user = await TestAuthHelper.RegisterAsync(client, email, displayName);

        var projectResponse = await client.PostAsJsonAsync($"/api/workspaces/{user.Workspace.Id}/projects",
            new { name = "Board", key = "BD" });
        projectResponse.EnsureSuccessStatusCode();
        var project = await projectResponse.Content.ReadFromJsonAsync<ProjectDto>(TestAuthHelper.JsonOptions)
            ?? throw new InvalidOperationException("Project was not created.");

        var columnsResponse = await client.GetAsync($"/api/projects/{project.Id}/columns");
        columnsResponse.EnsureSuccessStatusCode();
        var columns = await columnsResponse.Content.ReadFromJsonAsync<List<BoardColumnDto>>(TestAuthHelper.JsonOptions)
            ?? throw new InvalidOperationException("Columns were not returned.");

        return new BoardContext(client, user, project, columns);
    }

    public static async Task<TaskItemDto> CreateTaskAsync(BoardContext board, string title, Guid? columnId = null, string priority = "Medium")
    {
        var response = await board.Client.PostAsJsonAsync($"/api/projects/{board.Project.Id}/tasks", new
        {
            title,
            priority,
            columnId = columnId ?? board.Todo.Id
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TaskItemDto>(TestAuthHelper.JsonOptions))!;
    }

    public static async Task InviteAsync(HttpClient owner, Guid workspaceId, string email, string role)
    {
        var response = await owner.PostAsJsonAsync($"/api/workspaces/{workspaceId}/invitations", new { email, role });
        response.EnsureSuccessStatusCode();
    }

    public static HubConnection Hub(WorklaneWebApplicationFactory factory, string accessToken)
    {
        return new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "/hubs/notifications"), options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(accessToken);
            })
            .Build();
    }
}
