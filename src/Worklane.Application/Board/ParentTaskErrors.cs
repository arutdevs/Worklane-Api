namespace Worklane.Application.Board;

/// <summary>
/// Stable problem codes for parent/child links. Details are the English sentences
/// in docs/api-error-codes.md. The Web app localizes from the code.
/// </summary>
public static class ParentTaskErrors
{
    public const string ParentSelf = "parent_self";
    public const string ParentSelfDetail = "A card can't be its own subtask.";

    public const string ParentDifferentProject = "parent_different_project";
    public const string ParentDifferentProjectDetail = "You can only link cards in the same project.";

    public const string ParentDepthExceeded = "parent_depth_exceeded";
    public const string ParentDepthExceededDetail = "A subtask can't have its own subtasks.";

    public const string ChildHasChildren = "child_has_children";
    public const string ChildHasChildrenDetail = "This card has subtasks, so it can't become a subtask.";

    public const string ParentChildLimit = "parent_child_limit";
    public const string ParentChildLimitDetail = "This card already has the maximum of {0} subtasks.";

    public const string AlreadyHasParent = "already_has_parent";
    public const string AlreadyHasParentDetail = "This card is already a subtask of {0}. Unlink it first.";

    public const string CardArchived = "card_archived";
    public const string CardArchivedDetail = "Archived cards can't be linked.";

    public const string ParentHasActiveChildren = "parent_has_active_children";
    public const string ParentHasActiveChildrenDetail = "This card has {0} active subtasks.";

    public const string ParentHasChildren = "parent_has_children";
    public const string ParentHasChildrenDetail = "Delete or unlink the subtasks before deleting this card permanently.";

    public const string ParentLinkConflict = "parent_link_conflict";
    public const string ParentLinkConflictDetail = "Someone changed this card at the same time. Reload and try again.";

    public const string NotFoundDetail = "Resource was not found.";

    public const string ChildCount = "childCount";
    public const string MaxChildren = "maxChildren";
    public const string ParentDisplayKey = "parentDisplayKey";

    public const string ManualSource = "manual";
}
