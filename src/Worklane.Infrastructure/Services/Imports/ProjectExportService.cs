using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Worklane.Application.Common.Exceptions;
using Worklane.Application.Common.Interfaces;
using Worklane.Application.Imports;
using Worklane.Application.Imports.Dtos;
using Worklane.Application.Imports.Interfaces;
using Worklane.Domain.Entities;
using Worklane.Domain.Enums;
using Worklane.Infrastructure.Identity;
using Worklane.Infrastructure.Persistence;

namespace Worklane.Infrastructure.Services.Imports;

public class ProjectExportService : IProjectExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly WorklaneDbContext _db;

    public ProjectExportService(WorklaneDbContext db)
    {
        _db = db;
    }

    public async Task<ProjectExportFile> ExportAsync(
        Guid projectId,
        Guid userId,
        ExportFormat format,
        CancellationToken cancellationToken)
    {
        var project = await _db.Projects.SingleOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project is null)
            throw new NotFoundException(ImportErrors.NotFoundDetail);

        var rows = await _db.TaskItems
            .Where(t => t.ProjectId == projectId)
            .Join(
                _db.BoardColumns,
                task => task.ColumnId,
                column => column.Id,
                (task, column) => new { Task = task, ColumnName = column.Name, ColumnOrder = column.Order })
            .OrderBy(row => row.ColumnOrder)
            .ThenBy(row => row.Task.Order)
            .ThenBy(row => row.Task.Number)
            .ToListAsync(cancellationToken);

        var workspaceId = project.WorkspaceId;
        var parentIds = rows.Where(row => row.Task.ParentTaskId.HasValue).Select(row => row.Task.ParentTaskId!.Value).Distinct().ToList();
        // A parent may be archived. The task filter would hide it, and the key must still be this workspace's card.
        var parents = parentIds.Count == 0
            ? []
            : await _db.TaskItems
                .IgnoreQueryFilters()
                .Where(t => parentIds.Contains(t.Id) && t.Project.WorkspaceId == workspaceId)
                .Select(t => new { t.Id, t.ExternalId, t.Number, t.Project.Key })
                .ToListAsync(cancellationToken);
        var parentById = parents.ToDictionary(parent => parent.Id);

        var userIds = rows.Where(row => row.Task.AssigneeId.HasValue).Select(row => row.Task.AssigneeId!.Value).Distinct().ToList();
        var users = userIds.Count == 0
            ? new Dictionary<Guid, AppUser>()
            : await _db.Users.Where(user => userIds.Contains(user.Id)).ToDictionaryAsync(user => user.Id, cancellationToken);

        var exportedAt = DateTimeOffset.UtcNow;
        var tasks = rows.Select(row =>
        {
            users.TryGetValue(row.Task.AssigneeId ?? Guid.Empty, out var assignee);
            parentById.TryGetValue(row.Task.ParentTaskId ?? Guid.Empty, out var parent);
            return new ExportTask
            {
                ExternalId = row.Task.ExternalId,
                Title = row.Task.Title,
                Description = row.Task.Description,
                Column = row.ColumnName,
                AssigneeEmail = assignee?.Email,
                AssigneeName = assignee?.DisplayName,
                Priority = row.Task.Priority,
                DueDate = row.Task.DueDate,
                EstimateDays = row.Task.EstimateDays,
                ParentExternalId = parent?.ExternalId,
                ParentDisplayKey = parent is null ? null : parent.Key + "-" + parent.Number,
                DisplayKey = project.Key + "-" + row.Task.Number,
                StartedAt = row.Task.StartedAt,
                CompletedAt = row.Task.CompletedAt,
                CreatedAt = row.Task.CreatedAt,
                UpdatedAt = row.Task.UpdatedAt
            };
        }).ToList();

        _db.ProjectExportLogs.Add(new ProjectExportLog
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            ActorUserId = userId,
            Format = format,
            RowCount = tasks.Count,
            CreatedAt = exportedAt
        });
        await _db.SaveChangesAsync(cancellationToken);

        if (format == ExportFormat.Csv)
        {
            return new ProjectExportFile
            {
                Content = Csv(tasks),
                ContentType = "text/csv; charset=utf-8",
                FileName = project.Key + "-tasks.csv"
            };
        }

        var document = new ExportDocument
        {
            ProjectId = project.Id,
            ProjectKey = project.Key,
            ExportedAt = exportedAt,
            Tasks = tasks
        };
        return new ProjectExportFile
        {
            Content = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions),
            ContentType = "application/json; charset=utf-8",
            FileName = project.Key + "-tasks.json"
        };
    }

    private static byte[] Csv(IReadOnlyList<ExportTask> tasks)
    {
        var builder = new StringBuilder();
        builder.Append("External Id,Title,Description,Column,Assignee Email,Priority,Due Date,Estimate Days,Parent External Id,Parent Display Key,Display Key,Assignee Name,Started At,Completed At,Created At,Updated At");
        builder.Append("\r\n");
        foreach (var task in tasks)
        {
            builder.Append(Cell(task.ExternalId)).Append(',');
            builder.Append(Cell(task.Title)).Append(',');
            builder.Append(Cell(task.Description)).Append(',');
            builder.Append(Cell(task.Column)).Append(',');
            builder.Append(Cell(task.AssigneeEmail)).Append(',');
            builder.Append(Cell(task.Priority.ToString())).Append(',');
            builder.Append(Cell(Format(task.DueDate))).Append(',');
            builder.Append(Cell(task.EstimateDays?.ToString("0.################", CultureInfo.InvariantCulture))).Append(',');
            builder.Append(Cell(task.ParentExternalId)).Append(',');
            builder.Append(Cell(task.ParentDisplayKey)).Append(',');
            builder.Append(Cell(task.DisplayKey)).Append(',');
            builder.Append(Cell(task.AssigneeName)).Append(',');
            builder.Append(Cell(Format(task.StartedAt))).Append(',');
            builder.Append(Cell(Format(task.CompletedAt))).Append(',');
            builder.Append(Cell(Format(task.CreatedAt))).Append(',');
            builder.Append(Cell(Format(task.UpdatedAt)));
            builder.Append("\r\n");
        }

        var preamble = Encoding.UTF8.GetPreamble();
        var body = Encoding.UTF8.GetBytes(builder.ToString());
        var bytes = new byte[preamble.Length + body.Length];
        preamble.CopyTo(bytes, 0);
        body.CopyTo(bytes, preamble.Length);
        return bytes;
    }

    /// <summary>A leading formula character is quoted so a spreadsheet does not evaluate it. JSON export does not do this.</summary>
    private static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            value = "'" + value;
        if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
            return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        return value;
    }

    private static string? Format(DateTimeOffset? value) =>
        value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private sealed class ExportDocument
    {
        public Guid ProjectId { get; set; }
        public string ProjectKey { get; set; } = "";
        public DateTimeOffset ExportedAt { get; set; }
        public List<ExportTask> Tasks { get; set; } = [];
    }

    private sealed class ExportTask
    {
        public string? ExternalId { get; set; }
        public string Title { get; set; } = "";
        public string? Description { get; set; }
        public string Column { get; set; } = "";
        public string? AssigneeEmail { get; set; }
        public string? AssigneeName { get; set; }
        public TaskPriority Priority { get; set; }
        public DateTimeOffset? DueDate { get; set; }
        public double? EstimateDays { get; set; }
        public string? ParentExternalId { get; set; }
        public string? ParentDisplayKey { get; set; }
        public string DisplayKey { get; set; } = "";
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }
}
