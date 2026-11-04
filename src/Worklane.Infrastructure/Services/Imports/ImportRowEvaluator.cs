using System.Globalization;
using System.Text.RegularExpressions;
using Worklane.Application.Board;
using Worklane.Application.Imports;
using Worklane.Application.Imports.Dtos;
using Worklane.Domain.Enums;

namespace Worklane.Infrastructure.Services.Imports;

internal sealed class ImportColumn
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required int Order { get; init; }
    public required bool IsDone { get; init; }
    public required bool IsInProgress { get; init; }
}

internal sealed class ExistingExternalCard
{
    public required Guid Id { get; init; }
    public required string ExternalId { get; init; }
    public required bool IsArchived { get; init; }
    public Guid? ParentTaskId { get; init; }
    public required string DisplayKey { get; init; }
    public int ActiveChildCount { get; init; }
}

internal sealed class EvaluatedRow
{
    public required int RowNumber { get; init; }
    public required ImportRowStatus Status { get; init; }
    public string? ErrorCode { get; init; }
    public string? WarningCode { get; set; }
    public int? MaxLength { get; init; }
    public int? MaxChildren { get; set; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public string? ExternalId { get; init; }
    public string? ParentExternalId { get; init; }
    public bool ParentRequested { get; set; }
    public bool HadParent { get; init; }
    public string? ColumnName { get; init; }
    public string? AssigneeEmail { get; init; }
    public string? PriorityText { get; init; }
    public string? DueDateText { get; init; }
    public string? EstimateText { get; init; }
    public Guid? ColumnId { get; init; }
    public string? ResolvedColumnName { get; init; }
    public Guid? AssigneeId { get; init; }
    public TaskPriority Priority { get; init; } = TaskPriority.Medium;
    public DateTimeOffset? DueDate { get; init; }
    public double? EstimateDays { get; init; }
    public Guid? ExistingTaskId { get; init; }
    public string? DisplayKey { get; init; }
    public bool WillCreate { get; init; }
}

internal sealed class EvaluatedImport
{
    public required IReadOnlyList<EvaluatedRow> Rows { get; init; }
    public BatchWarning? ParentAssumption { get; init; }
}

internal sealed class ImportEvaluationInput
{
    public required ImportSource Source { get; init; }
    public required ColumnMapping Mapping { get; init; }
    public required IReadOnlyList<string> Headers { get; init; }
    public required IReadOnlyList<ParsedRow> Rows { get; init; }
    public required IReadOnlyList<ImportColumn> Columns { get; init; }
    public required Guid FallbackColumnId { get; init; }
    public required IReadOnlyDictionary<string, Guid> MembersByNormalizedEmail { get; init; }
    public required Func<string, string> NormalizeEmail { get; init; }
    public required IReadOnlyDictionary<string, ExistingExternalCard> Existing { get; init; }
    public required int MaxChildren { get; init; }
}

/// <summary>Same function for preview and commit. First blocking code wins, then the first warning.</summary>
internal static partial class ImportRowEvaluator
{
    private static readonly Regex JiraKey = JiraKeyPattern();

    private static readonly Dictionary<string, TaskPriority> Priorities = new(StringComparer.OrdinalIgnoreCase)
    {
        ["urgent"] = TaskPriority.Urgent,
        ["highest"] = TaskPriority.Urgent,
        ["critical"] = TaskPriority.Urgent,
        ["blocker"] = TaskPriority.Urgent,
        ["high"] = TaskPriority.High,
        ["major"] = TaskPriority.High,
        ["medium"] = TaskPriority.Medium,
        ["normal"] = TaskPriority.Medium,
        ["low"] = TaskPriority.Low,
        ["lowest"] = TaskPriority.Low,
        ["minor"] = TaskPriority.Low,
        ["trivial"] = TaskPriority.Low
    };

    public static EvaluatedImport Evaluate(ImportEvaluationInput input)
    {
        var headerIndex = Index(input.Headers);
        var drafts = input.Rows.Select(row => Extract(row, input, headerIndex)).ToList();
        var assumption = ApplyParentRules(input, headerIndex, drafts);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var firstPass = new List<Draft>(drafts.Count);
        foreach (var draft in drafts)
        {
            if (draft.SourceArchived)
            {
                draft.Status = ImportRowStatus.SkippedSourceArchived;
                firstPass.Add(draft);
                continue;
            }

            if (string.IsNullOrEmpty(draft.ExternalId))
            {
                Block(draft, ImportErrors.ExternalIdMissing);
                firstPass.Add(draft);
                continue;
            }

            if (!seen.Add(draft.ExternalId))
            {
                Block(draft, ImportErrors.ExternalIdDuplicate);
                firstPass.Add(draft);
                continue;
            }

            if (string.IsNullOrWhiteSpace(draft.Title))
            {
                Block(draft, ImportErrors.TitleMissing);
                firstPass.Add(draft);
                continue;
            }

            if (draft.Title!.Length > ImportErrors.TitleMaxLength)
            {
                draft.Title = draft.Title[..ImportErrors.TitleMaxLength];
                Block(draft, ImportErrors.TitleTooLong, maxLength: ImportErrors.TitleMaxLength);
                firstPass.Add(draft);
                continue;
            }

            if (draft.Description is { Length: > ImportErrors.DescriptionMaxLength })
            {
                draft.Description = draft.Description[..ImportErrors.DescriptionMaxLength];
                Block(draft, ImportErrors.DescriptionTooLong, maxLength: ImportErrors.DescriptionMaxLength);
                firstPass.Add(draft);
                continue;
            }

            if (draft.DueDateInvalid)
            {
                Block(draft, ImportErrors.DateInvalid);
                firstPass.Add(draft);
                continue;
            }

            if (draft.EstimateInvalid)
            {
                Block(draft, ImportErrors.EstimateInvalid);
                firstPass.Add(draft);
                continue;
            }

            if (draft.FormulaUncached)
            {
                Block(draft, ImportErrors.FormulaUncached);
                firstPass.Add(draft);
                continue;
            }

            if (input.Existing.TryGetValue(draft.ExternalId, out var existing))
            {
                draft.ExistingTaskId = existing.Id;
                draft.DisplayKey = existing.DisplayKey;
                draft.ParentExternalId = null;
                draft.ParentRequested = false;
                draft.Status = existing.IsArchived
                    ? ImportRowStatus.SkippedArchived
                    : ImportRowStatus.SkippedExisting;
                firstPass.Add(draft);
                continue;
            }

            draft.Survived = true;
            firstPass.Add(draft);
        }

        var plannedChildren = new Dictionary<string, int>(StringComparer.Ordinal);
        var results = new List<EvaluatedRow>(firstPass.Count);
        foreach (var draft in firstPass)
        {
            if (draft.Survived)
            {
                draft.HadParent = draft.ParentUnresolved || draft.ParentAmbiguous || !string.IsNullOrEmpty(draft.ParentExternalId);
                ApplyParent(draft, firstPass, input, plannedChildren);
            }
            results.Add(Finish(draft, input));
        }

        return new EvaluatedImport { Rows = results, ParentAssumption = assumption };
    }

    private static void ApplyParent(
        Draft draft,
        List<Draft> rows,
        ImportEvaluationInput input,
        Dictionary<string, int> plannedChildren)
    {
        if (draft.ParentAmbiguous)
        {
            Block(draft, ImportErrors.ParentAmbiguous);
            return;
        }

        if (draft.ParentUnresolved || string.IsNullOrEmpty(draft.ParentExternalId))
        {
            if (draft.ParentUnresolved)
                draft.WarningCode = ImportErrors.ParentUnresolved;
            return;
        }

        if (string.Equals(draft.ParentExternalId, draft.ExternalId, StringComparison.Ordinal))
        {
            Block(draft, ParentTaskErrors.ParentSelf);
            return;
        }

        input.Existing.TryGetValue(draft.ParentExternalId, out var existing);
        if (existing is { IsArchived: true })
        {
            draft.ParentUnresolved = true;
            draft.WarningCode = ImportErrors.ParentUnresolved;
            return;
        }

        var existingActive = existing is { IsArchived: false };
        var parentInFile = rows.FirstOrDefault(row =>
            row.ErrorCode != ImportErrors.ExternalIdDuplicate
            && row.Status is not ImportRowStatus.SkippedExisting and not ImportRowStatus.SkippedArchived and not ImportRowStatus.SkippedSourceArchived
            && string.Equals(row.ExternalId, draft.ParentExternalId, StringComparison.Ordinal));
        var parentIsChild = existingActive
            ? existing!.ParentTaskId is not null
            : parentInFile is not null && !string.IsNullOrEmpty(parentInFile.ParentExternalId);
        if (parentIsChild)
        {
            Block(draft, ParentTaskErrors.ParentDepthExceeded);
            return;
        }

        var parentWillExist = existingActive || parentInFile is { Survived: true };
        if (!parentWillExist)
        {
            draft.ParentUnresolved = true;
            draft.WarningCode = ImportErrors.ParentUnresolved;
            return;
        }

        var already = existingActive ? existing!.ActiveChildCount : 0;
        var planned = plannedChildren.GetValueOrDefault(draft.ParentExternalId);
        if (already + planned >= input.MaxChildren)
        {
            Block(draft, ParentTaskErrors.ParentChildLimit, maxChildren: input.MaxChildren);
            return;
        }

        plannedChildren[draft.ParentExternalId] = planned + 1;
        draft.ParentRequested = true;
    }

    private static EvaluatedRow Finish(Draft draft, ImportEvaluationInput input)
    {
        Guid? columnId = null;
        string? columnName = null;
        Guid? assigneeId = null;
        var willCreate = draft.Survived && draft.ErrorCode is null;
        if (willCreate)
        {
            if (!string.IsNullOrWhiteSpace(draft.AssigneeEmail))
            {
                var key = input.NormalizeEmail(draft.AssigneeEmail);
                if (input.MembersByNormalizedEmail.TryGetValue(key, out var member))
                    assigneeId = member;
                else
                    draft.WarningCode ??= ImportErrors.AssigneeSkipped;
            }

            var matched = MatchColumn(input.Columns, draft.ColumnText);
            if (matched is null)
            {
                columnId = input.FallbackColumnId;
                columnName = input.Columns.FirstOrDefault(column => column.Id == input.FallbackColumnId)?.Name;
                if (!string.IsNullOrWhiteSpace(draft.ColumnText))
                    draft.WarningCode ??= ImportErrors.ColumnFallback;
            }
            else
            {
                columnId = matched.Id;
                columnName = matched.Name;
            }

            if (draft.PriorityDefaulted)
                draft.WarningCode ??= ImportErrors.PriorityDefaulted;

            draft.Status = draft.WarningCode is null ? ImportRowStatus.Ready : ImportRowStatus.Warning;
        }
        else if (draft.Status == ImportRowStatus.Pending)
        {
            draft.Status = ImportRowStatus.Invalid;
        }

        return new EvaluatedRow
        {
            RowNumber = draft.RowNumber,
            Status = draft.Status,
            ErrorCode = draft.ErrorCode,
            WarningCode = willCreate ? draft.WarningCode : null,
            MaxLength = draft.MaxLength,
            MaxChildren = draft.MaxChildren,
            Title = draft.Title,
            Description = draft.Description,
            ExternalId = draft.ExternalId,
            ParentExternalId = draft.Status is ImportRowStatus.SkippedExisting or ImportRowStatus.SkippedArchived or ImportRowStatus.SkippedSourceArchived
                ? null
                : draft.ParentExternalId,
            ParentRequested = willCreate && draft.ParentRequested,
            HadParent = draft.HadParent,
            ColumnName = TrimTo(draft.ColumnText, 100),
            AssigneeEmail = TrimTo(draft.AssigneeEmail, 320),
            PriorityText = TrimTo(draft.PriorityText, 50),
            DueDateText = TrimTo(draft.DueDateText, 80),
            EstimateText = TrimTo(draft.EstimateText, 40),
            ColumnId = columnId,
            ResolvedColumnName = columnName,
            AssigneeId = assigneeId,
            Priority = draft.Priority,
            DueDate = draft.DueDate,
            EstimateDays = draft.EstimateDays,
            ExistingTaskId = draft.ExistingTaskId,
            DisplayKey = draft.DisplayKey,
            WillCreate = willCreate
        };
    }

    private static BatchWarning? ApplyParentRules(
        ImportEvaluationInput input,
        Dictionary<string, int> headerIndex,
        List<Draft> drafts)
    {
        var parentHeader = Header(input.Mapping, ImportTargets.ParentExternalId);
        if (parentHeader is null)
            return null;

        if (input.Source == ImportSource.Asana && parentHeader.Equals("Parent task", StringComparison.OrdinalIgnoreCase))
        {
            ResolveAsanaParents(drafts, headerIndex, input.Mapping);
            return null;
        }

        if (!parentHeader.Equals("Parent", StringComparison.OrdinalIgnoreCase))
            return null;

        var values = drafts
            .Select(draft => draft.ParentExternalId)
            .Where(value => !string.IsNullOrEmpty(value))
            .ToList();
        string mode;
        if (values.All(value => JiraKey.IsMatch(value!)))
        {
            mode = "key";
        }
        else if (FindHeader(input.Headers, "Issue id") is { } issueHeader
                 && values.All(value => value!.All(char.IsDigit)))
        {
            mode = "issueId";
            var issueIndex = headerIndex[issueHeader];
            var byIssue = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var draft in drafts)
            {
                var issue = Cell(draft.Source, issueIndex);
                var externalId = draft.ExternalId;
                if (!string.IsNullOrEmpty(issue) && !byIssue.ContainsKey(issue))
                    byIssue[issue] = externalId;
            }

            foreach (var draft in drafts)
            {
                if (string.IsNullOrEmpty(draft.ParentExternalId))
                    continue;
                if (byIssue.TryGetValue(draft.ParentExternalId, out var externalId) && !string.IsNullOrEmpty(externalId))
                    draft.ParentExternalId = externalId;
                else
                    draft.ParentUnresolved = true;
            }
        }
        else
        {
            mode = "unresolved";
            foreach (var draft in drafts)
            {
                if (string.IsNullOrEmpty(draft.ParentExternalId))
                    continue;
                draft.ParentUnresolved = true;
            }
        }

        return new BatchWarning { Code = ImportErrors.ParentColumnAssumed, Mode = mode };
    }

    private static void ResolveAsanaParents(
        List<Draft> drafts,
        Dictionary<string, int> headerIndex,
        ColumnMapping mapping)
    {
        var titleHeader = Header(mapping, ImportTargets.Title);
        if (titleHeader is null || !headerIndex.ContainsKey(titleHeader))
            return;

        var candidates = drafts
            .Where(draft => string.IsNullOrEmpty(draft.ParentExternalId))
            .Select(draft => (Name: draft.Title, draft.ExternalId))
            .Where(candidate => !string.IsNullOrEmpty(candidate.Name))
            .ToList();

        foreach (var draft in drafts)
        {
            if (string.IsNullOrEmpty(draft.ParentExternalId))
                continue;
            var name = draft.ParentExternalId;
            var matches = candidates.Where(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal)).ToList();
            if (matches.Count > 1)
            {
                draft.ParentAmbiguous = true;
            }
            else if (matches.Count == 0 || string.IsNullOrEmpty(matches[0].ExternalId))
            {
                draft.ParentUnresolved = true;
            }
            else
            {
                draft.ParentExternalId = matches[0].ExternalId;
            }
        }
    }

    private static Draft Extract(ParsedRow row, ImportEvaluationInput input, Dictionary<string, int> headerIndex)
    {
        var draft = new Draft { RowNumber = row.RowNumber, Source = row };
        draft.FormulaUncached = row.Cells.Any(cell => cell.FormulaUncached);
        draft.SourceArchived = Archived(input.Headers, headerIndex, row);
        draft.ExternalId = Mapped(input.Mapping, headerIndex, row, ImportTargets.ExternalId);
        draft.Title = Mapped(input.Mapping, headerIndex, row, ImportTargets.Title);
        draft.Description = Mapped(input.Mapping, headerIndex, row, ImportTargets.Description);
        draft.ColumnText = Mapped(input.Mapping, headerIndex, row, ImportTargets.Column);
        draft.AssigneeEmail = Mapped(input.Mapping, headerIndex, row, ImportTargets.AssigneeEmail);
        draft.PriorityText = Mapped(input.Mapping, headerIndex, row, ImportTargets.Priority);
        draft.DueDateText = Mapped(input.Mapping, headerIndex, row, ImportTargets.DueDate);
        draft.EstimateText = Mapped(input.Mapping, headerIndex, row, ImportTargets.EstimateDays);
        draft.ParentExternalId = Mapped(input.Mapping, headerIndex, row, ImportTargets.ParentExternalId);

        if (string.IsNullOrEmpty(draft.PriorityText))
            draft.Priority = TaskPriority.Medium;
        else if (Priorities.TryGetValue(draft.PriorityText, out var priority))
            draft.Priority = priority;
        else
        {
            draft.Priority = TaskPriority.Medium;
            draft.PriorityDefaulted = true;
        }

        if (string.IsNullOrEmpty(draft.DueDateText))
            draft.DueDate = null;
        else if (ImportDates.TryParse(draft.DueDateText, out var due))
            draft.DueDate = due;
        else
            draft.DueDateInvalid = true;

        if (string.IsNullOrEmpty(draft.EstimateText))
            draft.EstimateDays = null;
        else if (double.TryParse(draft.EstimateText, NumberStyles.Float, CultureInfo.InvariantCulture, out var estimate)
                 && estimate > 0 && estimate <= 260)
            draft.EstimateDays = estimate;
        else
            draft.EstimateInvalid = true;

        return draft;
    }

    private static bool Archived(IReadOnlyList<string> headers, Dictionary<string, int> index, ParsedRow row)
    {
        foreach (var name in new[] { "Archived", "Closed" })
        {
            var header = FindHeader(headers, name);
            if (header is null || !index.TryGetValue(header, out var column))
                continue;
            var text = Cell(row, column);
            if (text is not null && text.Equals("true", StringComparison.OrdinalIgnoreCase)
                || text is not null && text.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || text == "1"
                || text is not null && text.Equals("archived", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static ImportColumn? MatchColumn(IReadOnlyList<ImportColumn> columns, string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Trim().Length > 100)
            return null;
        return columns
            .Where(column => string.Equals(column.Name.Trim(), text.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(column => column.Order)
            .FirstOrDefault();
    }

    private static string? Mapped(ColumnMapping mapping, Dictionary<string, int> index, ParsedRow row, string target)
    {
        var header = Header(mapping, target);
        if (header is null || !index.TryGetValue(header, out var column))
            return null;
        return Cell(row, column);
    }

    private static string? Cell(ParsedRow row, int column) =>
        column >= 0 && column < row.Cells.Count ? row.Cells[column].Text : null;

    private static string? Header(ColumnMapping mapping, string target) =>
        mapping.Assignments.FirstOrDefault(assignment => assignment.Target == target)?.Header;

    private static Dictionary<string, int> Index(IReadOnlyList<string> headers)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < headers.Count; i++)
            index.TryAdd(headers[i], i);
        return index;
    }

    private static string? FindHeader(IReadOnlyList<string> headers, string name) =>
        headers.FirstOrDefault(header => header.Trim().Equals(name, StringComparison.OrdinalIgnoreCase));

    private static void Block(Draft draft, string code, int? maxLength = null, int? maxChildren = null)
    {
        draft.ErrorCode = code;
        draft.MaxLength = maxLength;
        draft.MaxChildren = maxChildren;
        draft.Survived = false;
        draft.Status = ImportRowStatus.Invalid;
        draft.ParentRequested = false;
    }

    private static string? TrimTo(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];

    [GeneratedRegex("^[A-Z][A-Z0-9]+-\\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex JiraKeyPattern();

    private sealed class Draft
    {
        public int RowNumber { get; init; }
        public required ParsedRow Source { get; init; }
        public ImportRowStatus Status { get; set; } = ImportRowStatus.Pending;
        public string? ErrorCode { get; set; }
        public string? WarningCode { get; set; }
        public int? MaxLength { get; set; }
        public int? MaxChildren { get; set; }
        public string? Title { get; set; }
        public string? Description { get; set; }
        public string? ExternalId { get; set; }
        public string? ParentExternalId { get; set; }
        public bool ParentRequested { get; set; }
        public bool HadParent { get; set; }
        public bool ParentUnresolved { get; set; }
        public bool ParentAmbiguous { get; set; }
        public string? ColumnText { get; set; }
        public string? AssigneeEmail { get; set; }
        public string? PriorityText { get; set; }
        public string? DueDateText { get; set; }
        public string? EstimateText { get; set; }
        public TaskPriority Priority { get; set; } = TaskPriority.Medium;
        public bool PriorityDefaulted { get; set; }
        public DateTimeOffset? DueDate { get; set; }
        public bool DueDateInvalid { get; set; }
        public double? EstimateDays { get; set; }
        public bool EstimateInvalid { get; set; }
        public bool FormulaUncached { get; set; }
        public bool SourceArchived { get; set; }
        public bool Survived { get; set; }
        public Guid? ExistingTaskId { get; set; }
        public string? DisplayKey { get; set; }
    }
}

internal static class ImportDates
{
    private static readonly string[] OffsetFormats =
    [
        "O",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
        "yyyy-MM-dd'T'HH:mm:ssK",
        "yyyy-MM-dd'T'HH:mm:ss'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"
    ];

    public static bool TryParse(string text, out DateTimeOffset value)
    {
        if (DateTimeOffset.TryParseExact(text, OffsetFormats, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out value))
            return true;

        if (DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            value = new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Utc));
            return true;
        }

        if (DateTime.TryParseExact(text, ["yyyy-MM-dd HH:mm", "yyyy-MM-dd'T'HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var minute))
        {
            value = new DateTimeOffset(DateTime.SpecifyKind(minute, DateTimeKind.Utc));
            return true;
        }

        var english = CultureInfo.GetCultureInfo("en-US");
        if (DateTime.TryParseExact(text, ["d/MMM/yy", "dd/MMM/yy"], english, DateTimeStyles.None, out var jira))
        {
            value = new DateTimeOffset(DateTime.SpecifyKind(jira, DateTimeKind.Utc));
            return true;
        }

        return DateTimeOffset.TryParseExact(
            text,
            "ddd, d MMM yyyy HH:mm:ss zzz",
            english,
            DateTimeStyles.AllowWhiteSpaces,
            out value);
    }
}
