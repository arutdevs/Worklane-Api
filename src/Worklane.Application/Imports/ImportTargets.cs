namespace Worklane.Application.Imports;

/// <summary>Closed set of column-mapping targets. There is no labels target.</summary>
public static class ImportTargets
{
    public const string Title = "title";
    public const string ExternalId = "externalId";
    public const string Description = "description";
    public const string Column = "column";
    public const string AssigneeEmail = "assigneeEmail";
    public const string Priority = "priority";
    public const string DueDate = "dueDate";
    public const string EstimateDays = "estimateDays";
    public const string ParentExternalId = "parentExternalId";
    public const string Ignore = "ignore";

    public static readonly string[] All =
    [
        Title, ExternalId, Description, Column, AssigneeEmail, Priority, DueDate, EstimateDays, ParentExternalId, Ignore
    ];

    /// <summary>Targets that may be assigned to at most one header. <see cref="Ignore"/> is not in this list.</summary>
    public static readonly string[] Single =
    [
        Title, ExternalId, Description, Column, AssigneeEmail, Priority, DueDate, EstimateDays, ParentExternalId
    ];

    public static bool IsKnown(string? target) =>
        target is not null && All.Contains(target, StringComparer.Ordinal);
}
