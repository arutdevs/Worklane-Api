namespace Worklane.Application.Common.Options;

/// <summary>Limits for card links. The cap is configuration, not a compiled constant.</summary>
public class TaskSettings
{
    public const string SectionName = "Tasks";

    /// <summary>Active children one card may have. Archived children do not count.</summary>
    public int MaxChildrenPerParent { get; set; } = 50;
}
