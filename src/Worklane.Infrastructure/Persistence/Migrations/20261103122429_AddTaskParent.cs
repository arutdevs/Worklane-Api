using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Worklane.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskParent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ParentTaskId",
                table: "TaskItems",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_TaskItems_ProjectId_Id",
                table: "TaskItems",
                columns: new[] { "ProjectId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskItems_ProjectId_ParentTaskId",
                table: "TaskItems",
                columns: new[] { "ProjectId", "ParentTaskId" },
                filter: "\"ParentTaskId\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_TaskItems_TaskItems_ProjectId_ParentTaskId",
                table: "TaskItems",
                columns: new[] { "ProjectId", "ParentTaskId" },
                principalTable: "TaskItems",
                principalColumns: new[] { "ProjectId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddCheckConstraint(
                name: "CK_TaskItems_NotSelfParent",
                table: "TaskItems",
                sql: "\"ParentTaskId\" IS NULL OR \"ParentTaskId\" <> \"Id\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_TaskItems_NotSelfParent",
                table: "TaskItems");

            migrationBuilder.DropForeignKey(
                name: "FK_TaskItems_TaskItems_ProjectId_ParentTaskId",
                table: "TaskItems");

            migrationBuilder.DropIndex(
                name: "IX_TaskItems_ProjectId_ParentTaskId",
                table: "TaskItems");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_TaskItems_ProjectId_Id",
                table: "TaskItems");

            migrationBuilder.DropColumn(
                name: "ParentTaskId",
                table: "TaskItems");
        }
    }
}
