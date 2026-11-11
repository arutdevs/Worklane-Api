using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Worklane.Application.Workspaces.Dtos;
using Worklane.Domain.Enums;
using Worklane.Infrastructure.Persistence;
using Worklane.IntegrationTests.Infrastructure;

namespace Worklane.IntegrationTests.Imports;

public class ImportExportTests : IClassFixture<WorklaneWebApplicationFactory>
{
    private readonly WorklaneWebApplicationFactory _factory;

    public ImportExportTests(WorklaneWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Outsider_IsNotFound_WithTheSameBodyAsAMissingProject()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, $"import-out-{Guid.NewGuid():N}@example.com");
        var outsider = await TestAuthHelper.RegisterAsync(_factory.CreateClient(), $"import-stranger-{Guid.NewGuid():N}@example.com");
        var missingProject = Guid.NewGuid();

        var missingUpload = await ImportHttp.UploadAsync(board.Client, missingProject, "a.csv", "Title,External Id\nA,1\n");
        var outsiderUpload = await ImportHttp.UploadAsync(outsider.Client, board.Project.Id, "a.csv", "Title,External Id\nA,1\n");
        var missingImport = Guid.NewGuid();
        var outsiderImport = Guid.NewGuid();
        var missingGet = await board.Client.GetAsync($"/api/projects/{missingProject}/imports/{missingImport}");
        var outsiderGet = await outsider.Client.GetAsync($"/api/projects/{board.Project.Id}/imports/{outsiderImport}");

        missingUpload.StatusCode.Should().Be(HttpStatusCode.NotFound);
        await SameProblemAsync(missingUpload, outsiderUpload, missingProject, board.Project.Id);
        await SameProblemAsync(missingGet, outsiderGet, missingProject, board.Project.Id, missingImport, outsiderImport);
    }

    [Fact]
    public async Task LowRole_IsForbidden_AndViewerCanExport()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, $"import-role-{Guid.NewGuid():N}@example.com");
        var viewer = await InviteAsync(board, WorkspaceRole.Viewer);
        var member = await InviteAsync(board, WorkspaceRole.Member);
        var uploaded = await ImportHttp.UploadAsync(board.Client, board.Project.Id, "a.csv", ImportHttp.SimpleCsv("A", "A-1"));
        var batchId = (await ImportHttp.ReadAsync(uploaded)).GetProperty("id").GetGuid();

        var viewerUpload = await ImportHttp.UploadAsync(viewer.Client, board.Project.Id, "a.csv", ImportHttp.SimpleCsv("B", "B-1"));
        var memberUndo = await member.Client.PostAsJsonAsync($"/api/projects/{board.Project.Id}/imports/{batchId}/undo", new { });
        var viewerExport = await viewer.Client.GetAsync($"/api/projects/{board.Project.Id}/tasks/export?format=csv");

        (await ProblemCodeAsync(viewerUpload)).Should().Be("workspace_access_denied");
        viewerUpload.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        memberUndo.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ProblemCodeAsync(memberUndo)).Should().Be("workspace_access_denied");
        viewerExport.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task BatchFromAnotherProject_IsNotFound()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, $"import-other-project-{Guid.NewGuid():N}@example.com");
        var uploaded = await ImportHttp.UploadAsync(board.Client, board.Project.Id, "a.csv", ImportHttp.SimpleCsv("A", "A-1"));
        uploaded.StatusCode.Should().Be(HttpStatusCode.Created);
        var batchId = (await ImportHttp.ReadAsync(uploaded)).GetProperty("id").GetGuid();
        var other = await board.Client.PostAsJsonAsync($"/api/workspaces/{board.User.Workspace.Id}/projects", new { name = "Other", key = "OT" });
        other.EnsureSuccessStatusCode();
        var otherId = (await other.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions)).GetProperty("id").GetGuid();

        var preview = await board.Client.PostAsync($"/api/projects/{otherId}/imports/{batchId}/preview", null);
        preview.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ProblemCodeAsync(preview)).Should().Be("not_found");
    }

    [Fact]
    public async Task Upload_AcceptsCsvAndXlsx_AndRejectsBadFiles()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, $"import-upload-{Guid.NewGuid():N}@example.com");
        var csv = await ImportHttp.UploadAsync(board.Client, board.Project.Id, "issues.csv", ImportHttp.SimpleCsv("Fix", "WL-1"), contentType: "image/png");
        csv.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await ImportHttp.ReadAsync(csv);
        created.GetProperty("source").GetString().Should().Be("generic");
        created.GetProperty("suggestedMapping").GetProperty("assignments").EnumerateArray()
            .Select(a => a.GetProperty("target").GetString())
            .Should().Contain(new[] { "title", "externalId" });

        var xlsx = await ImportHttp.UploadBytesAsync(
            board.Client,
            board.Project.Id,
            "book.xlsx",
            ImportHttp.SimpleXlsx(new[] { "Title", "External Id" }, new[] { new[] { "From sheet", "X-1" } }),
            "generic");
        xlsx.StatusCode.Should().Be(HttpStatusCode.Created, await xlsx.Content.ReadAsStringAsync());

        var xls = await ImportHttp.UploadBytesAsync(board.Client, board.Project.Id, "old.xls", Encoding.UTF8.GetBytes("Title,External Id\nA,1\n"), "generic");
        xls.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        (await ImportHttp.ReadAsync(xls)).GetProperty("extension").GetString().Should().Be(".xls");

        var renamed = await ImportHttp.UploadBytesAsync(board.Client, board.Project.Id, "notes.csv", ImportHttp.ZipWithControlByte(), "generic");
        renamed.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);

        var empty = await ImportHttp.UploadBytesAsync(board.Client, board.Project.Id, "empty.csv", Array.Empty<byte>(), "generic");
        empty.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ProblemCodeAsync(empty)).Should().Be("file_empty");

        var semicolon = await ImportHttp.UploadAsync(board.Client, board.Project.Id, "semi.csv", "Title;External Id\nA;1\n");
        semicolon.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ProblemCodeAsync(semicolon)).Should().Be("import_delimiter_unsupported");

        var tooMany = new StringBuilder("Title,External Id\n");
        for (var i = 0; i < 2001; i++)
            tooMany.Append("Row ").Append(i).Append(",ID-").Append(i).Append('\n');
        var rows = await ImportHttp.UploadAsync(board.Client, board.Project.Id, "big.csv", tooMany.ToString());
        rows.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var rowProblem = await ImportHttp.ReadAsync(rows);
        rowProblem.GetProperty("code").GetString().Should().Be("import_row_limit");
        rowProblem.GetProperty("maxRows").GetInt32().Should().Be(2000);
    }

    [Fact]
    public async Task EleventhOpenBatch_AndProjectWithoutColumns_Conflict()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, $"import-cap-{Guid.NewGuid():N}@example.com");
        for (var i = 0; i < 10; i++)
        {
            var ok = await ImportHttp.UploadAsync(board.Client, board.Project.Id, $"f{i}.csv", ImportHttp.SimpleCsv("A" + i, "ID-" + i));
            ok.StatusCode.Should().Be(HttpStatusCode.Created, await ok.Content.ReadAsStringAsync());
        }

        var eleventh = await ImportHttp.UploadAsync(board.Client, board.Project.Id, "f10.csv", ImportHttp.SimpleCsv("A", "ID-X"));
        eleventh.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var limit = await ImportHttp.ReadAsync(eleventh);
        limit.GetProperty("code").GetString().Should().Be("import_batch_limit");
        limit.GetProperty("limit").GetInt32().Should().Be(10);

        foreach (var column in board.Columns)
        {
            var deleted = await board.Client.DeleteAsync($"/api/columns/{column.Id}");
            deleted.StatusCode.Should().Be(HttpStatusCode.NoContent, await deleted.Content.ReadAsStringAsync());
        }

        var bare = await BoardTestSetup.CreateAsync(_factory, $"import-nocol-{Guid.NewGuid():N}@example.com");
        foreach (var column in bare.Columns)
            (await bare.Client.DeleteAsync($"/api/columns/{column.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var noColumns = await ImportHttp.UploadAsync(bare.Client, bare.Project.Id, "a.csv", ImportHttp.SimpleCsv("A", "1"));
        noColumns.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await ImportHttp.ReadAsync(noColumns);
        body.GetProperty("code").GetString().Should().Be("conflict");
        body.GetProperty("detail").GetString().Should().Be("Project has no columns. Create a column first.");
    }

    [Fact]
    public async Task Mapping_RequiresTitleAndExternalId_AndAColumnInThisProject()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, $"import-map-{Guid.NewGuid():N}@example.com");
        var uploaded = await ImportHttp.UploadAsync(board.Client, board.Project.Id, "a.csv", ImportHttp.SimpleCsv("A", "1"));
        var batch = await ImportHttp.ReadAsync(uploaded);
        var batchId = batch.GetProperty("id").GetGuid();
        var previewEarly = await board.Client.PostAsync($"/api/projects/{board.Project.Id}/imports/{batchId}/preview", null);
        previewEarly.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ProblemCodeAsync(previewEarly)).Should().Be("import_mapping_required");

        var missingTitle = await board.Client.PutAsJsonAsync(
            $"/api/projects/{board.Project.Id}/imports/{batchId}/mapping",
            new
            {
                fallbackColumnId = board.Todo.Id,
                assignments = new[] { new { header = "External Id", target = "externalId" }, new { header = "Title", target = "ignore" } }
            });
        missingTitle.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ProblemCodeAsync(missingTitle)).Should().Be("validation_failed");

        var other = await board.Client.PostAsJsonAsync($"/api/workspaces/{board.User.Workspace.Id}/projects", new { name = "Two", key = "TW" });
        other.EnsureSuccessStatusCode();
        var otherProject = (await other.Content.ReadFromJsonAsync<JsonElement>(TestAuthHelper.JsonOptions)).GetProperty("id").GetGuid();
        var otherColumns = await board.Client.GetFromJsonAsync<List<JsonElement>>($"/api/projects/{otherProject}/columns", TestAuthHelper.JsonOptions);
        var foreignColumn = otherColumns!.First().GetProperty("id").GetGuid();
        var headers = batch.GetProperty("headers").EnumerateArray().Select(header => header.GetString()).ToList();
        var foreign = await board.Client.PutAsJsonAsync(
            $"/api/projects/{board.Project.Id}/imports/{batchId}/mapping",
            new
            {
                fallbackColumnId = foreignColumn,
                assignments = headers.Select(header => new
                {
                    header,
                    target = header == "Title" ? "title" : header == "External Id" ? "externalId" : "ignore"
                })
            });
        foreign.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ProblemCodeAsync(foreign)).Should().Be("not_found");
    }

    [Fact]
    public async Task Preview_WritesNoCards_AndCommitHonoursSkipInvalid()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, $"import-preview-{Guid.NewGuid():N}@example.com");
        var before = await TaskCountAsync(board.Project.Id);
        var csv = """
            Title,External Id,Description,Column,Assignee Email,Priority,Due Date,Estimate Days,Parent External Id
            Good,G-1,,To Do,,,2026-01-15,,
            Missing id,,no id,,,,,,
            """;
        var batchId = await ImportHttp.PrepareAsync(board, csv, "generic");
        var preview = await board.Client.PostAsync($"/api/projects/{board.Project.Id}/imports/{batchId}/preview", null);
        preview.StatusCode.Should().Be(HttpStatusCode.OK, await preview.Content.ReadAsStringAsync());
        (await TaskCountAsync(board.Project.Id)).Should().Be(before);
        var rows = (await ImportHttp.ReadAsync(preview)).GetProperty("rows").EnumerateArray().ToList();
        rows.Should().HaveCount(2);
        rows[1].GetProperty("error").GetProperty("code").GetString().Should().Be("import_external_id_missing");

        var blocked = await board.Client.PostAsJsonAsync(
            $"/api/projects/{board.Project.Id}/imports/{batchId}/commit",
            new { skipInvalidRows = false });
        blocked.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var blockedBody = await ImportHttp.ReadAsync(blocked);
        blockedBody.GetProperty("code").GetString().Should().Be("import_has_errors");
        blockedBody.GetProperty("invalidCount").GetInt32().Should().Be(1);
        (await TaskCountAsync(board.Project.Id)).Should().Be(before);

        var skipped = await board.Client.PostAsJsonAsync(
            $"/api/projects/{board.Project.Id}/imports/{batchId}/commit",
            new { skipInvalidRows = true });
        skipped.StatusCode.Should().Be(HttpStatusCode.OK, await skipped.Content.ReadAsStringAsync());
        var committed = await ImportHttp.ReadAsync(skipped);
        committed.GetProperty("createdCount").GetInt32().Should().Be(1);
        committed.GetProperty("skippedInvalidCount").GetInt32().Should().Be(1);
        (await TaskCountAsync(board.Project.Id)).Should().Be(before + 1);

        var again = await board.Client.PostAsJsonAsync(
            $"/api/projects/{board.Project.Id}/imports/{batchId}/commit",
            new { skipInvalidRows = true });
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ProblemCodeAsync(again)).Should().Be("import_already_committed");
        (await TaskCountAsync(board.Project.Id)).Should().Be(before + 1);
    }

    [Fact]
    public async Task Commit_LinksParents_RejectsAThirdLevel_AndLeavesUnresolvedUnlinked()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, $"import-parent-{Guid.NewGuid():N}@example.com");
        var csv = """
            Title,External Id,Description,Column,Assignee Email,Priority,Due Date,Estimate Days,Parent External Id
            Epic,E-1,,To Do,,,,,
            Story,E-2,,To Do,,,,,E-1
            Sub,E-3,,To Do,,,,,E-2
            Orphan,E-4,,To Do,,,,,MISSING
            """;
        var batchId = await ImportHttp.PrepareAsync(board, csv, "generic");
        var preview = await ImportHttp.ReadAsync(await board.Client.PostAsync($"/api/projects/{board.Project.Id}/imports/{batchId}/preview", null));
        var sub = preview.GetProperty("rows").EnumerateArray().Single(row => row.GetProperty("externalId").GetString() == "E-3");
        sub.GetProperty("error").GetProperty("code").GetString().Should().Be("parent_depth_exceeded");
        var orphan = preview.GetProperty("rows").EnumerateArray().Single(row => row.GetProperty("externalId").GetString() == "E-4");
        orphan.GetProperty("warning").GetProperty("code").GetString().Should().Be("import_parent_unresolved");

        var blocked = await board.Client.PostAsJsonAsync($"/api/projects/{board.Project.Id}/imports/{batchId}/commit", new { skipInvalidRows = false });
        blocked.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await TaskCountAsync(board.Project.Id)).Should().Be(0);

        var committed = await ImportHttp.ReadAsync(await PostCommit(board, batchId, skip: true));
        committed.GetProperty("createdCount").GetInt32().Should().Be(3);
        committed.GetProperty("linkedCount").GetInt32().Should().Be(1);
        committed.GetProperty("unlinkedCount").GetInt32().Should().Be(1);
        var story = await FindTaskAsync(board, "Story");
        var epic = await FindTaskAsync(board, "Epic");
        story.GetProperty("parentTaskId").GetGuid().Should().Be(epic.GetProperty("id").GetGuid());
        epic.GetProperty("childCount").GetInt32().Should().Be(1);
        (await FindTaskAsync(board, "Orphan")).GetProperty("parentTaskId").ValueKind.Should().Be(JsonValueKind.Null);

        var activity = await board.Client.GetFromJsonAsync<List<JsonElement>>(
            $"/api/tasks/{story.GetProperty("id").GetGuid()}/activity",
            TestAuthHelper.JsonOptions);
        activity!.Select(row => row.GetProperty("type").GetString()).Should().Contain("ParentSet");
        activity.Should().OnlyContain(row => row.GetProperty("details").GetProperty("source").GetString() == "import");
    }

    [Fact]
    public async Task Reimport_SkipsExistingAndArchived_AndSourceIsPartOfTheKey()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, $"import-idem-{Guid.NewGuid():N}@example.com");
        var first = await ImportHttp.CommitAsync(board, ImportHttp.SimpleCsv("Original", "SAME"), "generic");
        first.GetProperty("createdCount").GetInt32().Should().Be(1);
        var task = await FindTaskAsync(board, "Original");
        var taskId = task.GetProperty("id").GetGuid();

        var second = await ImportHttp.CommitAsync(board, ImportHttp.SimpleCsv("Changed", "SAME"), "generic");
        second.GetProperty("skippedExistingCount").GetInt32().Should().Be(1);
        second.GetProperty("createdCount").GetInt32().Should().Be(0);
        (await FindTaskAsync(board, "Original")).GetProperty("title").GetString().Should().Be("Original");

        (await board.Client.DeleteAsync($"/api/tasks/{taskId}")).EnsureSuccessStatusCode();
        var third = await ImportHttp.CommitAsync(board, ImportHttp.SimpleCsv("Again", "SAME"), "generic");
        third.GetProperty("skippedArchivedCount").GetInt32().Should().Be(1);
        third.GetProperty("createdCount").GetInt32().Should().Be(0);

        var jira = """
            Summary,Issue key
            Jira card,SAME
            """;
        var otherSource = await ImportHttp.CommitAsync(board, jira, "jira");
        otherSource.GetProperty("createdCount").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task UnknownAndNonMemberAssignees_AreTheSameWarning()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, $"import-assign-{Guid.NewGuid():N}@example.com");
        var outsider = await TestAuthHelper.RegisterAsync(_factory.CreateClient(), $"import-not-member-{Guid.NewGuid():N}@example.com");
        var memberEmail = board.User.Tokens.User.Email;
        var assigned = await PreviewRowAsync(board, ImportHttp.SimpleCsv("Mine", "M-1", memberEmail));
        assigned.GetProperty("assigneeId").GetGuid().Should().Be(board.User.Tokens.User.Id);
        assigned.GetProperty("warning").ValueKind.Should().Be(JsonValueKind.Null);

        var unknown = await PreviewRowAsync(board, ImportHttp.SimpleCsv("Mine", "U-1", "nobody-import@example.com"));
        var nonMember = await PreviewRowAsync(board, ImportHttp.SimpleCsv("Mine", "N-1", outsider.Tokens.User.Email));
        unknown.GetProperty("warning").GetProperty("code").GetString().Should().Be("import_assignee_skipped");
        nonMember.GetProperty("warning").GetProperty("code").GetString().Should().Be("import_assignee_skipped");
        unknown.GetProperty("assigneeId").ValueKind.Should().Be(JsonValueKind.Null);
        nonMember.GetProperty("assigneeId").ValueKind.Should().Be(JsonValueKind.Null);
        unknown.GetProperty("title").GetString().Should().Be(nonMember.GetProperty("title").GetString());
        unknown.GetRawText().Should().NotContain("nobody-import@example.com");
        nonMember.GetRawText().Should().NotContain(outsider.Tokens.User.Email);
    }

    [Fact]
    public async Task LowPriority_UtcDate_DoneColumn_AndVendorStartAreNotCopied()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, $"import-fields-{Guid.NewGuid():N}@example.com");
        var csv = """
            Title,External Id,Description,Column,Assignee Email,Priority,Due Date,Estimate Days,Parent External Id,Start Date
            Done card,D-1,,Done,,Low,2026-01-15,1.5,,1999-01-01
            """;
        await ImportHttp.CommitAsync(board, csv, "generic");
        var task = await FindTaskAsync(board, "Done card");
        task.GetProperty("priority").GetString().Should().Be("Low");
        task.GetProperty("dueDate").GetDateTimeOffset().Should().Be(new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero));
        task.GetProperty("completedAt").GetDateTimeOffset().Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2));
        task.GetProperty("startedAt").ValueKind.Should().Be(JsonValueKind.Null);
        task.GetProperty("estimateDays").GetDouble().Should().Be(1.5);
    }

    [Fact]
    public async Task Commit_DeletesTheFile_AndDoesNotTouchAttachmentQuota()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, $"import-file-{Guid.NewGuid():N}@example.com");
        var before = await AttachmentBytesAsync();
        var result = await ImportHttp.CommitAsync(board, ImportHttp.SimpleCsv("Stored", "S-1"), "generic");
        var batchId = result.GetProperty("batchId").GetGuid();
        (await AttachmentBytesAsync()).Should().Be(before);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
        var batch = await db.ImportBatches.IgnoreQueryFilters().SingleAsync(b => b.Id == batchId);
        batch.Status.Should().Be(ImportBatchStatus.Committed);
        batch.StorageKey.Should().BeNull();
        batch.SampleJson.Should().BeNull();
        var row = await db.ImportBatchRows.IgnoreQueryFilters().SingleAsync(r => r.ImportBatchId == batchId);
        row.Title.Should().BeNull();
        row.AssigneeEmail.Should().BeNull();
        (await db.TaskAttachments.IgnoreQueryFilters().CountAsync(a => a.Task.ProjectId == board.Project.Id)).Should().Be(0);
    }

    [Fact]
    public async Task Undo_ArchivesOnlyPristineCards()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, $"import-undo-{Guid.NewGuid():N}@example.com");
        var csv = """
            Title,External Id,Description,Column,Assignee Email,Priority,Due Date,Estimate Days,Parent External Id
            Parent,P-1,,To Do,,,,,
            Child,C-1,,To Do,,,,,P-1
            Lone,L-1,,To Do,,,,,
            """;
        var committed = await ImportHttp.CommitAsync(board, csv, "generic");
        var batchId = committed.GetProperty("batchId").GetGuid();
        var child = await FindTaskAsync(board, "Child");
        var comment = await board.Client.PostAsJsonAsync($"/api/tasks/{child.GetProperty("id").GetGuid()}/comments", new { body = "edited" });
        comment.EnsureSuccessStatusCode();

        var undo = await board.Client.PostAsync($"/api/projects/{board.Project.Id}/imports/{batchId}/undo", null);
        undo.StatusCode.Should().Be(HttpStatusCode.OK, await undo.Content.ReadAsStringAsync());
        var body = await ImportHttp.ReadAsync(undo);
        body.GetProperty("status").GetString().Should().Be("undone");
        body.GetProperty("archivedCount").GetInt32().Should().Be(1);
        var kept = body.GetProperty("kept").EnumerateArray().ToList();
        kept.Should().Contain(row => row.GetProperty("reason").GetString() == "edited_after_import");
        kept.Should().Contain(row => row.GetProperty("reason").GetString() == "has_active_children");
        (await FindTaskAsync(board, "Child")).GetProperty("title").GetString().Should().Be("Child");
        (await FindTaskAsync(board, "Parent")).GetProperty("title").GetString().Should().Be("Parent");

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
            var lone = await db.TaskItems.IgnoreQueryFilters()
                .SingleAsync(task => task.ProjectId == board.Project.Id && task.Title == "Lone");
            lone.IsArchived.Should().BeTrue();
            lone.ArchivedAt.Should().NotBeNull();
            var keptCard = await db.TaskItems.IgnoreQueryFilters()
                .SingleAsync(task => task.ProjectId == board.Project.Id && task.Title == "Parent");
            keptCard.IsArchived.Should().BeFalse();
            keptCard.ArchivedAt.Should().BeNull();
        }

        var second = await board.Client.PostAsync($"/api/projects/{board.Project.Id}/imports/{batchId}/undo", null);
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ProblemCodeAsync(second)).Should().Be("import_already_undone");
    }

    [Fact]
    public async Task Export_PrefixesFormulas_OmitsArchivedCards_AndLogsOnce()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, $"import-export-{Guid.NewGuid():N}@example.com");
        var created = await board.Client.PostAsJsonAsync($"/api/projects/{board.Project.Id}/tasks", new
        {
            title = "=1+1",
            priority = "Medium",
            columnId = board.Todo.Id,
            assigneeId = board.User.Tokens.User.Id
        });
        created.EnsureSuccessStatusCode();
        var archived = await BoardTestSetup.CreateTaskAsync(board, "Hidden");
        (await board.Client.DeleteAsync($"/api/tasks/{archived.Id}")).EnsureSuccessStatusCode();

        var csv = await board.Client.GetAsync($"/api/projects/{board.Project.Id}/tasks/export?format=csv");
        csv.StatusCode.Should().Be(HttpStatusCode.OK);
        csv.Content.Headers.ContentType!.ToString().Should().Be("text/csv; charset=utf-8");
        csv.Content.Headers.ContentDisposition!.FileName.Should().Be($"{board.Project.Key}-tasks.csv");
        csv.Headers.GetValues("X-Content-Type-Options").Should().Contain("nosniff");
        var bytes = await csv.Content.ReadAsByteArrayAsync();
        bytes[0].Should().Be(0xEF);
        bytes[1].Should().Be(0xBB);
        bytes[2].Should().Be(0xBF);
        var text = Encoding.UTF8.GetString(bytes);
        text.Should().StartWith("\uFEFFExternal Id,Title,Description,Column,Assignee Email,Priority,Due Date,Estimate Days,Parent External Id,Parent Display Key,Display Key,Assignee Name,Started At,Completed At,Created At,Updated At\r\n");
        text.Should().Contain("'=1+1");
        text.Should().Contain(board.User.Tokens.User.Email);
        text.Should().NotContain("Hidden");

        var json = await board.Client.GetAsync($"/api/projects/{board.Project.Id}/tasks/export?format=json");
        var document = await ImportHttp.ReadAsync(json);
        document.GetProperty("tasks")[0].GetProperty("title").GetString().Should().Be("=1+1");
        document.GetProperty("tasks")[0].GetProperty("assigneeEmail").GetString().Should().Be(board.User.Tokens.User.Email);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
        var logs = await db.ProjectExportLogs.IgnoreQueryFilters().Where(l => l.ProjectId == board.Project.Id).ToListAsync();
        logs.Should().HaveCount(2);
    }

    [Fact]
    public async Task SameExternalId_InAnotherWorkspace_DoesNotCollideOrExport()
    {
        var left = await BoardTestSetup.CreateAsync(_factory, $"import-left-{Guid.NewGuid():N}@example.com");
        var right = await BoardTestSetup.CreateAsync(_factory, $"import-right-{Guid.NewGuid():N}@example.com");
        await ImportHttp.CommitAsync(left, ImportHttp.SimpleCsv("Left card", "SHARED"), "generic");
        await ImportHttp.CommitAsync(right, ImportHttp.SimpleCsv("Right card", "SHARED"), "generic");

        var leftExport = await (await left.Client.GetAsync($"/api/projects/{left.Project.Id}/tasks/export?format=json")).Content.ReadAsStringAsync();
        var rightExport = await (await right.Client.GetAsync($"/api/projects/{right.Project.Id}/tasks/export?format=json")).Content.ReadAsStringAsync();
        leftExport.Should().Contain("Left card").And.NotContain("Right card");
        rightExport.Should().Contain("Right card").And.NotContain("Left card");

        var stolen = await right.Client.GetAsync($"/api/projects/{left.Project.Id}/tasks/export?format=json");
        stolen.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ParallelCommitAndUndo_DoNotDuplicateCards()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, $"import-race-{Guid.NewGuid():N}@example.com");
        var batchId = await ImportHttp.PrepareAsync(board, ImportHttp.SimpleCsv("Race", "RACE-1"), "generic");
        var commit = board.Client.PostAsJsonAsync(
            $"/api/projects/{board.Project.Id}/imports/{batchId}/commit",
            new { skipInvalidRows = false });
        var undo = board.Client.PostAsync($"/api/projects/{board.Project.Id}/imports/{batchId}/undo", null);
        var results = await Task.WhenAll(commit, undo);
        results.Should().OnlyContain(response => (int)response.StatusCode < 500);
        results.Count(response => response.StatusCode == HttpStatusCode.OK).Should().BeGreaterThan(0);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
        var cards = await db.TaskItems.IgnoreQueryFilters().Where(t => t.ProjectId == board.Project.Id).ToListAsync();
        cards.Should().HaveCountLessThanOrEqualTo(1);
        var batch = await db.ImportBatches.IgnoreQueryFilters().SingleAsync(b => b.Id == batchId);
        if (batch.Status == ImportBatchStatus.Committed)
            cards.Should().ContainSingle(card => !card.IsArchived);
        if (batch.Status == ImportBatchStatus.Undone)
            cards.Should().ContainSingle(card => card.IsArchived);
    }

    [Fact]
    public async Task FiftyFirstChild_IsParentChildLimit()
    {
        var board = await BoardTestSetup.CreateAsync(_factory, $"import-cap-children-{Guid.NewGuid():N}@example.com");
        var csv = new StringBuilder("Title,External Id,Parent External Id\nParent,P-1,\n");
        for (var i = 1; i <= 51; i++)
            csv.Append("Child ").Append(i).Append(",C-").Append(i).Append(",P-1\n");
        var batchId = await ImportHttp.PrepareAsync(board, csv.ToString(), "generic");
        var preview = await ImportHttp.ReadAsync(await board.Client.PostAsync($"/api/projects/{board.Project.Id}/imports/{batchId}/preview", null));
        var last = preview.GetProperty("rows").EnumerateArray().Single(row => row.GetProperty("externalId").GetString() == "C-51");
        last.GetProperty("error").GetProperty("code").GetString().Should().Be("parent_child_limit");
        last.GetProperty("error").GetProperty("maxChildren").GetInt32().Should().Be(50);
        (await TaskCountAsync(board.Project.Id)).Should().Be(0);
    }

    private async Task<RegisteredUser> InviteAsync(BoardContext owner, WorkspaceRole role)
    {
        var email = $"{role.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}@example.com";
        var user = await TestAuthHelper.RegisterAsync(_factory.CreateClient(), email, role.ToString());
        var invite = await owner.Client.PostAsJsonAsync(
            $"/api/workspaces/{owner.User.Workspace.Id}/invitations",
            new { email, role = role.ToString() });
        invite.StatusCode.Should().Be(HttpStatusCode.OK, await invite.Content.ReadAsStringAsync());
        return user;
    }

    private async Task<int> TaskCountAsync(Guid projectId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
        return await db.TaskItems.IgnoreQueryFilters().CountAsync(t => t.ProjectId == projectId);
    }

    private async Task<long> AttachmentBytesAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorklaneDbContext>();
        return await db.TaskAttachments.IgnoreQueryFilters().SumAsync(a => (long?)a.SizeBytes) ?? 0;
    }

    private static async Task<JsonElement> FindTaskAsync(BoardContext board, string title)
    {
        var columns = await board.Client.GetFromJsonAsync<List<JsonElement>>($"/api/projects/{board.Project.Id}/columns", TestAuthHelper.JsonOptions);
        return columns!.SelectMany(column => column.GetProperty("tasks").EnumerateArray())
            .Single(task => task.GetProperty("title").GetString() == title);
    }

    private static async Task<JsonElement> PreviewRowAsync(BoardContext board, string csv)
    {
        var batchId = await ImportHttp.PrepareAsync(board, csv, "generic");
        var preview = await board.Client.PostAsync($"/api/projects/{board.Project.Id}/imports/{batchId}/preview", null);
        preview.EnsureSuccessStatusCode();
        return (await ImportHttp.ReadAsync(preview)).GetProperty("rows")[0];
    }

    private static async Task<HttpResponseMessage> PostCommit(BoardContext board, Guid batchId, bool skip) =>
        await board.Client.PostAsJsonAsync($"/api/projects/{board.Project.Id}/imports/{batchId}/commit", new { skipInvalidRows = skip });

    private static async Task<string> ProblemCodeAsync(HttpResponseMessage response) =>
        (await ImportHttp.ReadAsync(response)).GetProperty("code").GetString()!;

    private static async Task SameProblemAsync(HttpResponseMessage left, HttpResponseMessage right, params Guid[] ids)
    {
        var a = await left.Content.ReadAsStringAsync();
        var b = await right.Content.ReadAsStringAsync();
        var normalizedLeft = a;
        var normalizedRight = b;
        for (var i = 0; i < ids.Length; i += 2)
        {
            normalizedLeft = normalizedLeft.Replace(ids[i].ToString(), "{id}", StringComparison.OrdinalIgnoreCase);
            normalizedRight = normalizedRight.Replace(ids[i + 1].ToString(), "{id}", StringComparison.OrdinalIgnoreCase);
        }

        normalizedRight.Should().Be(normalizedLeft);
        normalizedLeft.Should().Contain("\"code\":\"not_found\"");
    }
}
