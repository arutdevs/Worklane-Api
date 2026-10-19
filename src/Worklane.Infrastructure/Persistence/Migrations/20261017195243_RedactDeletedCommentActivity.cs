using Microsoft.EntityFrameworkCore.Migrations;
using Worklane.Infrastructure.Persistence;

#nullable disable

namespace Worklane.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RedactDeletedCommentActivity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(DeletedCommentActivityRedaction.Sql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No-op. Redaction is irreversible: the comment text that was removed from
            // activity cannot be restored, so there is nothing for Down to put back.
        }
    }
}
