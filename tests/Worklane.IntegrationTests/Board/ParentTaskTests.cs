using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Worklane.Application.Board;
using Worklane.Application.Board.Dtos;
using Worklane.Application.Common.Options;
using Worklane.Domain.Enums;
using Worklane.Infrastructure.Persistence;
using Worklane.IntegrationTests.Infrastructure;

namespace Worklane.IntegrationTests.Board;

public class ParentTaskTests : IClassFixture<WorklaneWebApplicationFactory>
{
    private readonly WorklaneWebApplicationFactory _factory;

    public ParentTaskTests(WorklaneWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Link_CreateListClearAndRelink()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "parent-happy@test.com", "Ada");
        var parent = await BoardTestSetup.CreateTaskAsync(board, "Parent");
        var created = await PostChild(board, "Child", parent.Id);

        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var child = (await created.Content.ReadFromJsonAsync<TaskItemDto>(TestAuthHelper.JsonOptions))!;
        child.ParentTaskId.Should().Be(parent.Id);
        child.ParentDisplayKey.Should().Be(parent.DisplayKey);
        child.ChildCount.Should().Be(0);
        child.DoneChildCount.Should().Be(0);

        var parentAfter = await GetTask(board, parent.Id);
        parentAfter.ParentTaskId.Should().BeNull();
        parentAfter.ParentDisplayKey.Should().BeNull();
        parentAfter.ChildCount.Should().Be(1);
        parentAfter.DoneChildCount.Should().Be(0);

        var listed = await GetTasks(board);
        listed.Single(task => task.Id == parent.Id).ChildCount.Should().Be(1);
        listed.Single(task => task.Id == child.Id).ParentDisplayKey.Should().Be(parent.DisplayKey);

        var columns = await GetColumns(board);
        columns.SelectMany(column => column.Tasks).Single(task => task.Id == parent.Id).ChildCount.Should().Be(1);

        var second = await BoardTestSetup.CreateTaskAsync(board, "Second");
        var third = await BoardTestSetup.CreateTaskAsync(board, "Third");
        (await PutParent(board, second.Id, parent.Id)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await PutParent(board, third.Id, parent.Id)).StatusCode.Should().Be(HttpStatusCode.OK);
        await SetCreatedAt(second.Id, new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero));
        await SetCreatedAt(third.Id, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await SetCreatedAt(child.Id, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var children = await GetChildren(board, parent.Id);
        children.Select(task => task.Id).Should().Equal(child.Id, third.Id, second.Id);

        var repeat = await PutParent(board, child.Id, parent.Id);
        repeat.StatusCode.Should().Be(HttpStatusCode.OK);
        (await repeat.Content.ReadFromJsonAsync<TaskItemDto>(TestAuthHelper.JsonOptions))!.ParentTaskId.Should().Be(parent.Id);
        (await Activity(board, child.Id)).Count(row => row.Type == TaskActivityType.ParentSet).Should().Be(1);
        (await Activity(board, parent.Id)).Count(row => row.Type == TaskActivityType.ChildAdded).Should().Be(3);

        var parentSet = (await Activity(board, child.Id)).Single(row => row.Type == TaskActivityType.ParentSet);
        parentSet.Details!.Value.GetProperty("otherTaskId").GetGuid().Should().Be(parent.Id);
        parentSet.Details.Value.GetProperty("otherDisplayKey").GetString().Should().Be(parent.DisplayKey);
        parentSet.Details.Value.GetProperty("source").GetString().Should().Be(ParentTaskErrors.ManualSource);
        var childAdded = (await Activity(board, parent.Id)).Single(row =>
            row.Type == TaskActivityType.ChildAdded &&
            row.Details!.Value.GetProperty("otherTaskId").GetGuid() == child.Id);
        childAdded.Details!.Value.GetProperty("otherDisplayKey").GetString().Should().Be(child.DisplayKey);
        childAdded.Details.Value.GetProperty("source").GetString().Should().Be(ParentTaskErrors.ManualSource);

        var other = await BoardTestSetup.CreateTaskAsync(board, "Other parent");
        var blocked = await PutParent(board, child.Id, other.Id);
        await AssertProblem(blocked, HttpStatusCode.Conflict, ParentTaskErrors.AlreadyHasParent);
        (await Body(blocked)).GetProperty(ParentTaskErrors.ParentDisplayKey).GetString().Should().Be(parent.DisplayKey);

        var cleared = await DeleteParent(board, child.Id);
        cleared.StatusCode.Should().Be(HttpStatusCode.OK);
        (await cleared.Content.ReadFromJsonAsync<TaskItemDto>(TestAuthHelper.JsonOptions))!.ParentTaskId.Should().BeNull();
        (await GetTask(board, parent.Id)).ChildCount.Should().Be(2);
        (await Activity(board, child.Id)).Should().Contain(row => row.Type == TaskActivityType.ParentCleared);
        (await Activity(board, parent.Id)).Should().Contain(row =>
            row.Type == TaskActivityType.ChildRemoved &&
            row.Details!.Value.GetProperty("source").GetString() == ParentTaskErrors.ManualSource);

        var clearedAgain = await DeleteParent(board, child.Id);
        clearedAgain.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Activity(board, child.Id)).Count(row => row.Type == TaskActivityType.ParentCleared).Should().Be(1);

        var relinked = await PutParent(board, child.Id, other.Id);
        relinked.StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetTask(board, child.Id)).ParentDisplayKey.Should().Be(other.DisplayKey);
        (await GetTask(board, other.Id)).ChildCount.Should().Be(1);
        (await GetTask(board, parent.Id)).ChildCount.Should().Be(2);
    }

    [Fact]
    public async Task Rules_RejectSelfDepthCycleCrossProjectAndArchive()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "parent-rules@test.com");
        var root = await BoardTestSetup.CreateTaskAsync(board, "Root");
        var child = await ReadTask(await PostChild(board, "Child", root.Id));
        var sibling = await BoardTestSetup.CreateTaskAsync(board, "Sibling");
        var grandchildCandidate = await BoardTestSetup.CreateTaskAsync(board, "Grand");

        var self = await PutParent(board, root.Id, root.Id);
        await AssertProblem(self, HttpStatusCode.BadRequest, ParentTaskErrors.ParentSelf);

        var thirdOnCreate = await PostChild(board, "Too deep", child.Id);
        await AssertProblem(thirdOnCreate, HttpStatusCode.Conflict, ParentTaskErrors.ParentDepthExceeded);
        var thirdOnLink = await PutParent(board, grandchildCandidate.Id, child.Id);
        await AssertProblem(thirdOnLink, HttpStatusCode.Conflict, ParentTaskErrors.ParentDepthExceeded);

        var cycle = await PutParent(board, root.Id, child.Id);
        await AssertProblem(cycle, HttpStatusCode.Conflict, ParentTaskErrors.ParentDepthExceeded);

        (await PutParent(board, sibling.Id, root.Id)).EnsureSuccessStatusCode();
        var spare = await BoardTestSetup.CreateTaskAsync(board, "Spare");
        var hasChildren = await PutParent(board, root.Id, spare.Id);
        await AssertProblem(hasChildren, HttpStatusCode.Conflict, ParentTaskErrors.ChildHasChildren);
        (await Body(hasChildren)).GetProperty(ParentTaskErrors.ChildCount).GetInt32().Should().Be(2);

        var otherProject = await CreateProject(board, "Other", "OT");
        var foreign = await CreateTask(board, otherProject, "Foreign");
        var differentProject = await PutParent(board, sibling.Id, foreign.Id);
        await AssertProblem(differentProject, HttpStatusCode.Conflict, ParentTaskErrors.ParentDifferentProject);

        var outsider = await TestAuthHelper.RegisterAsync(_factory.CreateClient(), "parent-outsider@test.com");
        var outsideTask = await CreateTask(outsider, outsider.Workspace.Id, "Hidden", "ZX");
        var crossWorkspace = await PutParent(board, sibling.Id, outsideTask.Id);
        var randomParent = await PutParent(board, sibling.Id, Guid.NewGuid());
        crossWorkspace.StatusCode.Should().Be(HttpStatusCode.NotFound);
        randomParent.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await crossWorkspace.Content.ReadAsStringAsync()).Should().Be(await randomParent.Content.ReadAsStringAsync());

        var crossCreate = await PostChild(board, "Nope", outsideTask.Id);
        var randomCreate = await PostChild(board, "Nope", Guid.NewGuid());
        crossCreate.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await crossCreate.Content.ReadAsStringAsync()).Should().Be(await randomCreate.Content.ReadAsStringAsync());

        (await outsider.Client.DeleteAsync($"/api/tasks/{outsideTask.Id}")).EnsureSuccessStatusCode();
        var archivedElsewhere = await PutParent(board, sibling.Id, outsideTask.Id);
        var randomAfterArchive = await PutParent(board, sibling.Id, Guid.NewGuid());
        archivedElsewhere.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await archivedElsewhere.Content.ReadAsStringAsync()).Should().Be(await randomAfterArchive.Content.ReadAsStringAsync());

        var archivedParent = await BoardTestSetup.CreateTaskAsync(board, "Archived parent");
        (await board.Client.DeleteAsync($"/api/tasks/{archivedParent.Id}")).EnsureSuccessStatusCode();
        var archivedLink = await PutParent(board, grandchildCandidate.Id, archivedParent.Id);
        await AssertProblem(archivedLink, HttpStatusCode.Conflict, ParentTaskErrors.CardArchived);

        var archivedChild = await BoardTestSetup.CreateTaskAsync(board, "Archived child");
        (await board.Client.DeleteAsync($"/api/tasks/{archivedChild.Id}")).EnsureSuccessStatusCode();
        var linkArchivedChild = await PutParent(board, archivedChild.Id, root.Id);
        await AssertProblem(linkArchivedChild, HttpStatusCode.Conflict, ParentTaskErrors.CardArchived);
    }

    [Fact]
    public async Task ArchivedChildren_StillBlockAThirdLevel()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "parent-archived-child@test.com");
        var parent = await BoardTestSetup.CreateTaskAsync(board, "Parent");
        var child = await ReadTask(await PostChild(board, "Child", parent.Id));
        (await board.Client.DeleteAsync($"/api/tasks/{child.Id}")).EnsureSuccessStatusCode();

        var spare = await BoardTestSetup.CreateTaskAsync(board, "Spare");
        var blocked = await PutParent(board, parent.Id, spare.Id);
        await AssertProblem(blocked, HttpStatusCode.Conflict, ParentTaskErrors.ChildHasChildren);
        (await Body(blocked)).GetProperty(ParentTaskErrors.ChildCount).GetInt32().Should().Be(1);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
        var stored = await db.TaskItems.IgnoreQueryFilters().SingleAsync(task => task.Id == child.Id);
        stored.IsArchived.Should().BeTrue();
        stored.ParentTaskId.Should().Be(parent.Id);
    }

    [Fact]
    public async Task Counts_FollowDoneColumnAndArchivingAChild()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "parent-counts@test.com");
        var parent = await BoardTestSetup.CreateTaskAsync(board, "Parent");
        var children = new List<TaskItemDto>();
        for (var i = 0; i < 5; i++)
            children.Add(await ReadTask(await PostChild(board, $"Child {i}", parent.Id)));

        (await board.Client.PostAsJsonAsync($"/api/tasks/{children[0].Id}/move", new { toColumnId = board.Done.Id, toIndex = 0 }))
            .EnsureSuccessStatusCode();
        (await board.Client.PostAsJsonAsync($"/api/tasks/{children[1].Id}/move", new { toColumnId = board.Done.Id, toIndex = 1 }))
            .EnsureSuccessStatusCode();

        var before = await GetTask(board, parent.Id);
        before.ChildCount.Should().Be(5);
        before.DoneChildCount.Should().Be(2);
        (await GetColumns(board)).SelectMany(column => column.Tasks).Single(task => task.Id == parent.Id)
            .DoneChildCount.Should().Be(2);

        (await board.Client.DeleteAsync($"/api/tasks/{children[0].Id}")).EnsureSuccessStatusCode();
        var after = await GetTask(board, parent.Id);
        after.ChildCount.Should().Be(4);
        after.DoneChildCount.Should().Be(1);
        (await GetChildren(board, parent.Id)).Select(task => task.Id).Should().NotContain(children[0].Id);
    }

    [Fact]
    public async Task Archive_RequiresTheFlagThenArchivesActiveChildren()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "parent-archive@test.com");
        var parent = await BoardTestSetup.CreateTaskAsync(board, "Parent");
        var first = await ReadTask(await PostChild(board, "One", parent.Id));
        var second = await ReadTask(await PostChild(board, "Two", parent.Id));
        (await board.Client.DeleteAsync($"/api/tasks/{second.Id}")).EnsureSuccessStatusCode();

        var blocked = await board.Client.DeleteAsync($"/api/tasks/{parent.Id}");
        await AssertProblem(blocked, HttpStatusCode.Conflict, ParentTaskErrors.ParentHasActiveChildren);
        (await Body(blocked)).GetProperty(ParentTaskErrors.ChildCount).GetInt32().Should().Be(1);
        (await GetTask(board, parent.Id)).IsArchived.Should().BeFalse();

        var archived = await board.Client.DeleteAsync($"/api/tasks/{parent.Id}?includeChildren=true");
        archived.StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await board.Client.GetAsync($"/api/tasks/{parent.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await board.Client.GetAsync($"/api/tasks/{first.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await GetColumns(board)).SelectMany(column => column.Tasks).Should().BeEmpty();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
        var rows = await db.TaskItems.IgnoreQueryFilters()
            .Where(task => task.Id == parent.Id || task.Id == first.Id || task.Id == second.Id)
            .ToListAsync();
        rows.Should().OnlyContain(task => task.IsArchived);
        rows.Single(task => task.Id == first.Id).ParentTaskId.Should().Be(parent.Id);
        rows.Single(task => task.Id == second.Id).ParentTaskId.Should().Be(parent.Id);
    }

    [Fact]
    public async Task Authorization_ViewerCannotMutate_OutsiderIsNotFound()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "parent-auth-owner@test.com");
        var parent = await BoardTestSetup.CreateTaskAsync(board, "Parent");
        var child = await ReadTask(await PostChild(board, "Child", parent.Id));
        var viewer = await TestAuthHelper.RegisterAsync(_factory.CreateClient(), "parent-auth-viewer@test.com");
        var outsider = await TestAuthHelper.RegisterAsync(_factory.CreateClient(), "parent-auth-out@test.com");
        await BoardTestSetup.InviteAsync(board.Client, board.User.Workspace.Id, viewer.Tokens.User.Email, "Viewer");

        await AssertProblem(await PostChild(viewer, board.Project.Id, "Nope", parent.Id), HttpStatusCode.Forbidden, "workspace_access_denied");
        await AssertProblem(await PutParent(viewer, child.Id, parent.Id), HttpStatusCode.Forbidden, "workspace_access_denied");
        await AssertProblem(await DeleteParent(viewer, child.Id), HttpStatusCode.Forbidden, "workspace_access_denied");
        await AssertProblem(await viewer.Client.DeleteAsync($"/api/tasks/{parent.Id}?includeChildren=true"), HttpStatusCode.Forbidden, "workspace_access_denied");

        var visible = await GetChildren(viewer, parent.Id);
        visible.Select(task => task.Id).Should().Equal(child.Id);

        var missing = Guid.NewGuid();
        foreach (var (method, url, real) in new (HttpMethod Method, Func<Guid, string> Url, Guid Real)[]
        {
            (HttpMethod.Put, id => $"/api/tasks/{id}/parent", child.Id),
            (HttpMethod.Delete, id => $"/api/tasks/{id}/parent", child.Id),
            (HttpMethod.Get, id => $"/api/tasks/{id}/children", parent.Id),
            (HttpMethod.Delete, id => $"/api/tasks/{id}?includeChildren=true", parent.Id)
        })
        {
            var hidden = await Send(outsider.Client, method, url(real), method == HttpMethod.Put ? new { parentTaskId = parent.Id } : null);
            var absent = await Send(board.Client, method, url(missing), method == HttpMethod.Put ? new { parentTaskId = parent.Id } : null);
            hidden.StatusCode.Should().Be(HttpStatusCode.NotFound);
            absent.StatusCode.Should().Be(HttpStatusCode.NotFound);
            Normalize(await hidden.Content.ReadAsStringAsync(), real).Should().Be(Normalize(await absent.Content.ReadAsStringAsync(), missing));
        }
    }

    [Fact]
    public async Task Patch_IgnoresParentTaskId()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "parent-patch@test.com");
        var parent = await BoardTestSetup.CreateTaskAsync(board, "Parent");
        var task = await BoardTestSetup.CreateTaskAsync(board, "Loose");

        var patched = await board.Client.PatchAsJsonAsync($"/api/tasks/{task.Id}", new
        {
            title = "Renamed",
            parentTaskId = parent.Id
        });
        patched.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await patched.Content.ReadFromJsonAsync<TaskItemDto>(TestAuthHelper.JsonOptions))!;
        body.Title.Should().Be("Renamed");
        body.ParentTaskId.Should().BeNull();
        (await Activity(board, task.Id)).Should().NotContain(row => row.Type == TaskActivityType.ParentSet);
    }

    [Fact]
    public async Task SetParent_RejectsAMissingOrInvalidId()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "parent-bind@test.com");
        var task = await BoardTestSetup.CreateTaskAsync(board, "Loose");

        var missing = await board.Client.PutAsJsonAsync($"/api/tasks/{task.Id}/parent", new { });
        await AssertProblem(missing, HttpStatusCode.BadRequest, "validation_failed");

        var nulled = await board.Client.PutAsync(
            $"/api/tasks/{task.Id}/parent",
            new StringContent("""{"parentTaskId":null}""", Encoding.UTF8, "application/json"));
        await AssertProblem(nulled, HttpStatusCode.BadRequest, "invalid_request_body");

        var garbage = await board.Client.PutAsync(
            $"/api/tasks/{task.Id}/parent",
            new StringContent("""{"parentTaskId":"not-a-guid"}""", Encoding.UTF8, "application/json"));
        await AssertProblem(garbage, HttpStatusCode.BadRequest, "invalid_request_body");
    }

    [Fact]
    public async Task Cap_SerializesTwoConcurrentLinks()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "parent-cap@test.com");
        var max = _factory.Services.GetRequiredService<IOptions<TaskSettings>>().Value.MaxChildrenPerParent;
        max.Should().Be(50);
        var parent = await BoardTestSetup.CreateTaskAsync(board, "Parent");
        for (var i = 0; i < max - 1; i++)
            (await PostChild(board, $"Child {i}", parent.Id)).EnsureSuccessStatusCode();

        var first = await BoardTestSetup.CreateTaskAsync(board, "Racer 1");
        var second = await BoardTestSetup.CreateTaskAsync(board, "Racer 2");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = Task.WhenAll(PutWhenOpen(board.Client, first.Id, parent.Id, gate), PutWhenOpen(board.Client, second.Id, parent.Id, gate));
        gate.SetResult();
        var results = await pending;

        results.Count(result => result.StatusCode == HttpStatusCode.OK).Should().Be(1);
        var loser = results.Single(result => result.StatusCode != HttpStatusCode.OK);
        loser.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var loserCode = (await Body(loser)).GetProperty("code").GetString();
        loserCode.Should().BeOneOf(ParentTaskErrors.ParentChildLimit, ParentTaskErrors.ParentLinkConflict);

        var extra = await BoardTestSetup.CreateTaskAsync(board, "One more");
        var over = await PutParent(board, extra.Id, parent.Id);
        await AssertProblem(over, HttpStatusCode.Conflict, ParentTaskErrors.ParentChildLimit);
        (await Body(over)).GetProperty(ParentTaskErrors.MaxChildren).GetInt32().Should().Be(max);
        (await GetTask(board, parent.Id)).ChildCount.Should().Be(max);
    }

    [Fact]
    public async Task ConcurrentLinks_CannotBuildAThirdLevel()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "parent-race-depth@test.com");
        var top = await BoardTestSetup.CreateTaskAsync(board, "Top");
        var middle = await BoardTestSetup.CreateTaskAsync(board, "Middle");
        var bottom = await BoardTestSetup.CreateTaskAsync(board, "Bottom");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = Task.WhenAll(
            PutWhenOpen(board.Client, middle.Id, top.Id, gate),
            PutWhenOpen(board.Client, bottom.Id, middle.Id, gate));
        gate.SetResult();
        var results = await pending;
        results.Should().Contain(result => result.StatusCode == HttpStatusCode.OK);

        var rows = new[]
        {
            await GetTask(board, top.Id),
            await GetTask(board, middle.Id),
            await GetTask(board, bottom.Id)
        };
        var byId = rows.ToDictionary(task => task.Id);
        foreach (var task in rows.Where(task => task.ParentTaskId is Guid))
        {
            var parent = byId[task.ParentTaskId!.Value];
            parent.ParentTaskId.Should().BeNull();
            parent.ChildCount.Should().BeGreaterThan(0);
        }

        rows.Count(task => task.ParentTaskId is not null).Should().Be(1);
    }

    [Fact]
    public async Task SignalR_UpdatesTheParentProjectOnly()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "parent-hub@test.com");
        var otherProject = await CreateProject(board, "Sibling", "SB");
        var parent = await BoardTestSetup.CreateTaskAsync(board, "Parent");
        var child = await BoardTestSetup.CreateTaskAsync(board, "Child");

        await using var home = BoardTestSetup.Hub(_factory, board.User.Tokens.AccessToken);
        await using var away = BoardTestSetup.Hub(_factory, board.User.Tokens.AccessToken);
        var parentUpdated = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var parentUpdates = 0;
        var stray = 0;
        home.On<JsonElement>("TaskUpdated", payload =>
        {
            if (payload.GetProperty("id").GetGuid() == parent.Id)
            {
                Interlocked.Increment(ref parentUpdates);
                parentUpdated.TrySetResult(payload.Clone());
            }
        });
        away.On<JsonElement>("TaskUpdated", _ => Interlocked.Increment(ref stray));
        away.On<JsonElement>("TaskCreated", _ => Interlocked.Increment(ref stray));
        await home.StartAsync();
        await away.StartAsync();
        await home.InvokeAsync("JoinProject", board.Project.Id);
        await away.InvokeAsync("JoinProject", otherProject);

        (await PutParent(board, child.Id, parent.Id)).EnsureSuccessStatusCode();
        var update = await parentUpdated.Task.WaitAsync(TimeSpan.FromSeconds(10));
        update.GetProperty("childCount").GetInt32().Should().Be(1);
        update.GetProperty("projectId").GetGuid().Should().Be(board.Project.Id);
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        stray.Should().Be(0);

        (await PutParent(board, child.Id, parent.Id)).EnsureSuccessStatusCode();
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        parentUpdates.Should().Be(1);
    }

    [Fact]
    public async Task Database_RejectsSelfParentAndCrossProject_AndBlocksParentDelete()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "parent-db@test.com");
        var parent = await BoardTestSetup.CreateTaskAsync(board, "Parent");
        var child = await ReadTask(await PostChild(board, "Child", parent.Id));
        var otherProject = await CreateProject(board, "Other", "DB");
        var foreign = await CreateTask(board, otherProject, "Foreign");

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
            var row = await db.TaskItems.IgnoreQueryFilters().SingleAsync(task => task.Id == foreign.Id);
            row.ParentTaskId = row.Id;
            var error = await db.Invoking(context => context.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>();
            ParentLinkConflicts.Translate(error.Which)!.Code.Should().Be(ParentTaskErrors.ParentSelf);
        }

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
            var row = await db.TaskItems.IgnoreQueryFilters().SingleAsync(task => task.Id == foreign.Id);
            row.ParentTaskId = parent.Id;
            var error = await db.Invoking(context => context.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>();
            ParentLinkConflicts.Translate(error.Which)!.Code.Should().Be(ParentTaskErrors.ParentHasChildren);
        }

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
            var row = await db.TaskItems.IgnoreQueryFilters().SingleAsync(task => task.Id == parent.Id);
            db.TaskItems.Remove(row);
            var error = await db.Invoking(context => context.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>();
            ParentLinkConflicts.Translate(error.Which)!.Code.Should().Be(ParentTaskErrors.ParentHasChildren);
        }

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
            var linked = await db.TaskItems.IgnoreQueryFilters().SingleAsync(task => task.Id == child.Id);
            linked.ParentTaskId = null;
            var doomed = await db.TaskItems.IgnoreQueryFilters().SingleAsync(task => task.Id == parent.Id);
            db.TaskItems.Remove(doomed);
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task Migration_DownDropsTheLinkAndUpIsClean()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, "parent-migrate@test.com");
        var parent = await BoardTestSetup.CreateTaskAsync(board, "Parent");
        var child = await ReadTask(await PostChild(board, "Child", parent.Id));

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
        var migrator = db.GetService<IMigrator>() ?? throw new InvalidOperationException("IMigrator was not registered.");
        var up = migrator.GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
        up.Should().Contain("\"ParentTaskId\"");
        up.Should().Contain("AK_TaskItems_ProjectId_Id");
        up.Should().Contain("IX_TaskItems_ProjectId_ParentTaskId");
        up.Should().Contain("FK_TaskItems_TaskItems_ProjectId_ParentTaskId");
        up.Should().Contain("CK_TaskItems_NotSelfParent");
        var down = migrator.GenerateScript("20261103122429_AddTaskParent", "20261025155535_MakeEmailIndexUnique");
        down.Should().Contain("DROP COLUMN \"ParentTaskId\"");

        try
        {
            await migrator.MigrateAsync("20261025155535_MakeEmailIndexUnique");
            var columns = await db.Database.SqlQuery<string>(
                $"""SELECT column_name AS "Value" FROM information_schema.columns WHERE table_name = 'TaskItems' AND column_name = 'ParentTaskId'""")
                .ToListAsync();
            columns.Should().BeEmpty();
        }
        finally
        {
            await migrator.MigrateAsync();
        }

        var restored = await db.TaskItems.IgnoreQueryFilters().SingleAsync(task => task.Id == child.Id);
        restored.ParentTaskId.Should().BeNull();
    }

    private static async Task<HttpResponseMessage> PutWhenOpen(HttpClient client, Guid taskId, Guid parentId, TaskCompletionSource gate)
    {
        await gate.Task;
        return await client.PutAsJsonAsync($"/api/tasks/{taskId}/parent", new { parentTaskId = parentId });
    }

    private async Task SetCreatedAt(Guid taskId, DateTimeOffset createdAt)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
        var task = await db.TaskItems.IgnoreQueryFilters().SingleAsync(row => row.Id == taskId);
        task.CreatedAt = createdAt;
        await db.SaveChangesAsync();
    }

    private static async Task<Guid> CreateProject(BoardContext board, string name, string key)
    {
        var response = await board.Client.PostAsJsonAsync($"/api/workspaces/{board.User.Workspace.Id}/projects", new { name, key });
        response.EnsureSuccessStatusCode();
        var project = (await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions));
        return project.GetProperty("id").GetGuid();
    }

    private static async Task<TaskItemDto> CreateTask(BoardContext board, Guid projectId, string title)
    {
        var response = await board.Client.PostAsJsonAsync($"/api/projects/{projectId}/tasks", new { title, priority = "Medium" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TaskItemDto>(TestAuthHelper.JsonOptions))!;
    }

    private static async Task<TaskItemDto> CreateTask(RegisteredUser user, Guid workspaceId, string title, string key)
    {
        var projectResponse = await user.Client.PostAsJsonAsync($"/api/workspaces/{workspaceId}/projects", new { name = "Hidden", key });
        projectResponse.EnsureSuccessStatusCode();
        var projectId = (await projectResponse.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions)).GetProperty("id").GetGuid();
        var response = await user.Client.PostAsJsonAsync($"/api/projects/{projectId}/tasks", new { title, priority = "Medium" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TaskItemDto>(TestAuthHelper.JsonOptions))!;
    }

    private static Task<HttpResponseMessage> PostChild(BoardContext board, string title, Guid parentId) =>
        PostChild(board.Client, board.Project.Id, title, parentId);

    private static Task<HttpResponseMessage> PostChild(RegisteredUser user, Guid projectId, string title, Guid parentId) =>
        PostChild(user.Client, projectId, title, parentId);

    private static Task<HttpResponseMessage> PostChild(HttpClient client, Guid projectId, string title, Guid parentId) =>
        client.PostAsJsonAsync($"/api/projects/{projectId}/tasks", new { title, priority = "Medium", parentTaskId = parentId });

    private static Task<HttpResponseMessage> PutParent(BoardContext board, Guid taskId, Guid parentId) =>
        board.Client.PutAsJsonAsync($"/api/tasks/{taskId}/parent", new { parentTaskId = parentId });

    private static Task<HttpResponseMessage> PutParent(RegisteredUser user, Guid taskId, Guid parentId) =>
        user.Client.PutAsJsonAsync($"/api/tasks/{taskId}/parent", new { parentTaskId = parentId });

    private static Task<HttpResponseMessage> DeleteParent(BoardContext board, Guid taskId) =>
        board.Client.DeleteAsync($"/api/tasks/{taskId}/parent");

    private static Task<HttpResponseMessage> DeleteParent(RegisteredUser user, Guid taskId) =>
        user.Client.DeleteAsync($"/api/tasks/{taskId}/parent");

    private static async Task<TaskItemDto> ReadTask(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TaskItemDto>(TestAuthHelper.JsonOptions))!;
    }

    private static async Task<TaskItemDto> GetTask(BoardContext board, Guid taskId) =>
        (await board.Client.GetFromJsonAsync<TaskItemDto>($"/api/tasks/{taskId}", TestAuthHelper.JsonOptions))!;

    private static async Task<List<TaskItemDto>> GetTasks(BoardContext board) =>
        (await board.Client.GetFromJsonAsync<List<TaskItemDto>>($"/api/projects/{board.Project.Id}/tasks", TestAuthHelper.JsonOptions))!;

    private static async Task<List<BoardColumnDto>> GetColumns(BoardContext board) =>
        (await board.Client.GetFromJsonAsync<List<BoardColumnDto>>($"/api/projects/{board.Project.Id}/columns", TestAuthHelper.JsonOptions))!;

    private static async Task<List<TaskItemDto>> GetChildren(BoardContext board, Guid taskId) =>
        (await board.Client.GetFromJsonAsync<List<TaskItemDto>>($"/api/tasks/{taskId}/children", TestAuthHelper.JsonOptions))!;

    private static async Task<List<TaskItemDto>> GetChildren(RegisteredUser user, Guid taskId) =>
        (await user.Client.GetFromJsonAsync<List<TaskItemDto>>($"/api/tasks/{taskId}/children", TestAuthHelper.JsonOptions))!;

    private static async Task<List<TaskActivityDto>> Activity(BoardContext board, Guid taskId) =>
        (await board.Client.GetFromJsonAsync<List<TaskActivityDto>>($"/api/tasks/{taskId}/activity", TestAuthHelper.JsonOptions))!;

    private static async Task<JsonElement> Body(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions));

    private static async Task AssertProblem(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(status, body);
        JsonDocument.Parse(body).RootElement.GetProperty("code").GetString().Should().Be(code);
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string url, object? body)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return await client.SendAsync(request);
    }

    private static string Normalize(string body, Guid id) =>
        body.Replace(id.ToString(), "{id}", StringComparison.OrdinalIgnoreCase);
}
