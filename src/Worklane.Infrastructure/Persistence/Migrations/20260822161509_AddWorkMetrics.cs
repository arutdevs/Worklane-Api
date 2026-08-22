using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Worklane.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkMetrics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CompletedAt",
                table: "TaskItems",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "EstimateDays",
                table: "TaskItems",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "StartedAt",
                table: "TaskItems",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsInProgress",
                table: "BoardColumns",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "TaskActivities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    FromColumnId = table.Column<Guid>(type: "uuid", nullable: true),
                    ToColumnId = table.Column<Guid>(type: "uuid", nullable: false),
                    MovedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    MovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskActivities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskActivities_TaskItems_TaskId",
                        column: x => x.TaskId,
                        principalTable: "TaskItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TaskActivities_TaskId_MovedAt",
                table: "TaskActivities",
                columns: new[] { "TaskId", "MovedAt" });

            // Boards created before this migration have no way to say which column starts
            // the clock. Every project seeded so far got the same three default columns,
            // so adopt the one named "In Progress"; anything renamed has to be set by hand
            // once the column editor exists.
            migrationBuilder.Sql("""
                UPDATE "BoardColumns"
                SET "IsInProgress" = TRUE
                WHERE "Name" = 'In Progress';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TaskActivities");

            migrationBuilder.DropColumn(
                name: "CompletedAt",
                table: "TaskItems");

            migrationBuilder.DropColumn(
                name: "EstimateDays",
                table: "TaskItems");

            migrationBuilder.DropColumn(
                name: "StartedAt",
                table: "TaskItems");

            migrationBuilder.DropColumn(
                name: "IsInProgress",
                table: "BoardColumns");
        }
    }
}
