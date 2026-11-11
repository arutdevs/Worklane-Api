using System.Text.RegularExpressions;
using Worklane.Application.Imports;
using Worklane.Application.Imports.Dtos;

namespace Worklane.Infrastructure.Services.Imports;

/// <summary>
/// Header suggestions for the four presets. Match is trim, case-insensitive, ordinal.
/// The stored header keeps the file's spelling. A renamed duplicate header is left
/// out of the suggestion so the user has to assign it.
/// </summary>
internal static partial class ImportPresets
{
    public static ColumnMapping Suggest(string preset, IReadOnlyList<string> headers)
    {
        var used = new HashSet<int>();
        var assignments = new List<ColumnAssignment>();

        void Take(string target, bool all, params string[] candidates)
        {
            foreach (var candidate in candidates)
            {
                var matched = false;
                for (var i = 0; i < headers.Count; i++)
                {
                    if (used.Contains(i))
                        continue;
                    if (!headers[i].Trim().Equals(candidate, StringComparison.OrdinalIgnoreCase))
                        continue;
                    assignments.Add(new ColumnAssignment { Header = headers[i], Target = target });
                    used.Add(i);
                    matched = true;
                    break;
                }

                if (matched && !all)
                    return;
            }
        }

        switch (preset)
        {
            case "jira":
                SuggestJira(headers, Take);
                break;
            case "asana":
                SuggestAsana(Take);
                break;
            case "trello":
                SuggestTrello(Take);
                break;
            default:
                SuggestGeneric(Take);
                break;
        }

        for (var i = 0; i < headers.Count; i++)
        {
            if (used.Contains(i) || IsRenamedDuplicate(headers[i], headers))
                continue;
            if (preset == "jira" && CommentDigits().IsMatch(headers[i].Trim()))
            {
                assignments.Add(new ColumnAssignment { Header = headers[i], Target = ImportTargets.Ignore });
                used.Add(i);
                continue;
            }

            assignments.Add(new ColumnAssignment { Header = headers[i], Target = ImportTargets.Ignore });
            used.Add(i);
        }

        return new ColumnMapping { Assignments = assignments };
    }

    public static IReadOnlyList<BatchWarning> FieldIgnored(ColumnMapping mapping)
    {
        var headers = mapping.Assignments
            .Where(assignment => assignment.Target == ImportTargets.Ignore)
            .Select(assignment => assignment.Header)
            .ToList();
        if (headers.Count == 0)
            return [];
        return [new BatchWarning { Code = ImportErrors.FieldIgnored, Headers = headers }];
    }

    private delegate void Take(string target, bool all, params string[] candidates);

    private static void SuggestGeneric(Take take)
    {
        take(ImportTargets.Title, false, "Title");
        take(ImportTargets.ExternalId, false, "External Id");
        take(ImportTargets.Description, false, "Description");
        take(ImportTargets.Column, false, "Column");
        take(ImportTargets.AssigneeEmail, false, "Assignee Email");
        take(ImportTargets.Priority, false, "Priority");
        take(ImportTargets.DueDate, false, "Due Date");
        take(ImportTargets.EstimateDays, false, "Estimate Days");
        take(ImportTargets.ParentExternalId, false, "Parent External Id");
        take(ImportTargets.Ignore, true, "Display Key", "Assignee Name", "Parent Display Key", "Started At", "Completed At", "Created At", "Updated At");
    }

    private static void SuggestJira(IReadOnlyList<string> headers, Take take)
    {
        take(ImportTargets.ExternalId, false, "Issue key", "Key");
        take(ImportTargets.Title, false, "Summary");
        take(ImportTargets.Description, false, "Description");
        take(ImportTargets.Column, false, "Status");
        take(ImportTargets.Priority, false, "Priority");
        take(ImportTargets.DueDate, false, "Due date", "Due");
        take(ImportTargets.AssigneeEmail, false, "Assignee email", "Assignee Email");
        var hasParentKey = headers.Any(header => header.Trim().Equals("Parent key", StringComparison.OrdinalIgnoreCase));
        if (hasParentKey)
        {
            take(ImportTargets.ParentExternalId, false, "Parent key", "Parent Key");
            take(ImportTargets.Ignore, false, "Parent");
        }
        else
        {
            take(ImportTargets.ParentExternalId, false, "Parent");
        }

        take(ImportTargets.Ignore, true, "Issue Type", "Issue type", "Labels", "Reporter", "Creator", "Created", "Updated", "Resolved", "Comment");
    }

    private static void SuggestAsana(Take take)
    {
        take(ImportTargets.ExternalId, false, "Task ID");
        take(ImportTargets.Title, false, "Name");
        take(ImportTargets.Description, false, "Notes");
        take(ImportTargets.Column, false, "Section/Column", "Section");
        take(ImportTargets.AssigneeEmail, false, "Assignee Email");
        take(ImportTargets.DueDate, false, "Due Date");
        take(ImportTargets.Priority, false, "Priority");
        take(ImportTargets.ParentExternalId, false, "Parent task", "Parent Task");
        take(ImportTargets.Ignore, true,
            "Assignee", "Tags", "Start Date", "Created At", "Creation Date", "Completed At", "Completion Date",
            "Last Modified", "Last Modified Date", "Projects", "Project Name", "Blocked By (Dependencies)", "Blocking (Dependencies)");
    }

    private static void SuggestTrello(Take take)
    {
        take(ImportTargets.ExternalId, false, "Card ID");
        take(ImportTargets.Title, false, "Card Name");
        take(ImportTargets.Description, false, "Card Description");
        take(ImportTargets.Column, false, "List Name");
        take(ImportTargets.DueDate, false, "Due Date");
        take(ImportTargets.Ignore, true, "Labels", "Members", "Card URL", "Attachment Count", "Checklists", "Date Last Activity");
    }

    private static bool IsRenamedDuplicate(string header, IReadOnlyList<string> headers)
    {
        var match = Renamed().Match(header);
        if (!match.Success)
            return false;
        var stem = match.Groups[1].Value;
        return headers.Any(candidate => candidate.Equals(stem, StringComparison.Ordinal));
    }

    [GeneratedRegex(@"^(.+) \(\d+\)$", RegexOptions.CultureInvariant)]
    private static partial Regex Renamed();

    [GeneratedRegex("^comment\\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CommentDigits();
}
