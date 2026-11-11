using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Worklane.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskArchivedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ArchivedAt",
                table: "TaskItems",
                type: "timestamp with time zone",
                nullable: true);

            // Historical archived cards had no timestamp. Copy UpdatedAt onto those rows only.
            migrationBuilder.Sql(
                """
                UPDATE "TaskItems"
                SET "ArchivedAt" = "UpdatedAt"
                WHERE "IsArchived" = TRUE
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ArchivedAt",
                table: "TaskItems");
        }
    }
}
