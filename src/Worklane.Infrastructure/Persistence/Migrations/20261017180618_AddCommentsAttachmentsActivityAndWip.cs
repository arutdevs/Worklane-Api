using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Worklane.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCommentsAttachmentsActivityAndWip : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "MovedByUserId",
                table: "TaskActivities",
                newName: "ActorUserId");

            migrationBuilder.RenameColumn(
                name: "MovedAt",
                table: "TaskActivities",
                newName: "CreatedAt");

            migrationBuilder.RenameIndex(
                name: "IX_TaskActivities_TaskId_MovedAt",
                table: "TaskActivities",
                newName: "IX_TaskActivities_TaskId_CreatedAt");

            migrationBuilder.AlterColumn<Guid>(
                name: "ToColumnId",
                table: "TaskActivities",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "Details",
                table: "TaskActivities",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Type",
                table: "TaskActivities",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            // Existing rows are column placements. A null source column is the create
            // event; anything else is a move. Names are snapshotted from the columns
            // that still exist. The empty-string default exists only so the NOT NULL
            // add succeeds, and is dropped so EF always writes the real event type
            // (TaskActivityType.Created is the CLR default — a leftover default would
            // hide it, the same trap as TaskPriority.Low).
            migrationBuilder.Sql("""
                UPDATE "TaskActivities" AS a
                SET
                    "Type" = CASE WHEN a."FromColumnId" IS NULL THEN 'Created' ELSE 'Moved' END,
                    "Details" = CASE
                        WHEN a."FromColumnId" IS NULL THEN jsonb_build_object(
                            'columnId', a."ToColumnId",
                            'columnName', (SELECT c."Name" FROM "BoardColumns" AS c WHERE c."Id" = a."ToColumnId"))
                        ELSE jsonb_build_object(
                            'fromColumnId', a."FromColumnId",
                            'fromColumnName', (SELECT c."Name" FROM "BoardColumns" AS c WHERE c."Id" = a."FromColumnId"),
                            'toColumnId', a."ToColumnId",
                            'toColumnName', (SELECT c."Name" FROM "BoardColumns" AS c WHERE c."Id" = a."ToColumnId"))
                    END;
                """);

            migrationBuilder.Sql("""
                ALTER TABLE "TaskActivities" ALTER COLUMN "Type" DROP DEFAULT;
                """);

            migrationBuilder.AddColumn<int>(
                name: "WipLimit",
                table: "BoardColumns",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TaskAttachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    UploadedById = table.Column<Guid>(type: "uuid", nullable: false),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(127)", maxLength: 127, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    StorageKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskAttachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskAttachments_TaskItems_TaskId",
                        column: x => x.TaskId,
                        principalTable: "TaskItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TaskComments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Body = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskComments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskComments_TaskItems_TaskId",
                        column: x => x.TaskId,
                        principalTable: "TaskItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TaskAttachments_StorageKey",
                table: "TaskAttachments",
                column: "StorageKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskAttachments_TaskId_CreatedAt",
                table: "TaskAttachments",
                columns: new[] { "TaskId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskComments_TaskId_CreatedAt",
                table: "TaskComments",
                columns: new[] { "TaskId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TaskAttachments");

            migrationBuilder.DropTable(
                name: "TaskComments");

            migrationBuilder.DropColumn(
                name: "Details",
                table: "TaskActivities");

            migrationBuilder.DropColumn(
                name: "Type",
                table: "TaskActivities");

            migrationBuilder.DropColumn(
                name: "WipLimit",
                table: "BoardColumns");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                table: "TaskActivities",
                newName: "MovedAt");

            migrationBuilder.RenameColumn(
                name: "ActorUserId",
                table: "TaskActivities",
                newName: "MovedByUserId");

            migrationBuilder.RenameIndex(
                name: "IX_TaskActivities_TaskId_CreatedAt",
                table: "TaskActivities",
                newName: "IX_TaskActivities_TaskId_MovedAt");

            // Created rows, and rows for comments or files, have no ToColumnId. Delete
            // them before that column becomes required, so PostgreSQL does not store Guid.Empty.
            migrationBuilder.Sql("""
                DELETE FROM "TaskActivities" WHERE "ToColumnId" IS NULL;
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "ToColumnId",
                table: "TaskActivities",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
