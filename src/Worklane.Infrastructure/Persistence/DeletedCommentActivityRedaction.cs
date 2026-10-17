namespace Worklane.Infrastructure.Persistence;

/// <summary>
/// Removes comment text from every CommentAdded and CommentEdited activity row.
/// Idempotent: subtracting a jsonb key that is already absent leaves the row unchanged,
/// so the statement is safe to run again. Down cannot restore the text.
/// </summary>
public static class DeletedCommentActivityRedaction
{
    public const string Sql = """
        UPDATE "TaskActivities" AS a
        SET "Details" = a."Details" - 'body' - 'oldValue' - 'newValue'
        WHERE a."Type" IN ('CommentAdded', 'CommentEdited')
          AND a."Details" ? 'commentId'
          AND (a."Details"->>'commentId') ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$';
        """;
}
