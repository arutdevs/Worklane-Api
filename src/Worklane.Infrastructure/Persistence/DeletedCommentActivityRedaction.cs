namespace Worklane.Infrastructure.Persistence;

/// <summary>
/// Removes comment text from activity once the comment row is gone. Used by the data
/// migration for deletes that happened before redaction-on-delete, and by tests.
/// </summary>
public static class DeletedCommentActivityRedaction
{
    public const string Sql = """
        UPDATE "TaskActivities" AS a
        SET "Details" = a."Details" - 'body' - 'oldValue' - 'newValue'
        WHERE a."Type" IN ('CommentAdded', 'CommentEdited')
          AND a."Details" ? 'commentId'
          AND (a."Details"->>'commentId') ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
          AND NOT EXISTS (
              SELECT 1
              FROM "TaskComments" AS c
              WHERE c."Id" = (a."Details"->>'commentId')::uuid
          );
        """;
}
