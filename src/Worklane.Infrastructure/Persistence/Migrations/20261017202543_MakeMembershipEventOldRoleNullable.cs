using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Worklane.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MakeMembershipEventOldRoleNullable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "OldRole",
                table: "WorkspaceMembershipEvents",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // MemberAdded rows store a null old role. A rollback has to fill those
            // before the column can be required again.
            migrationBuilder.Sql(
                """
                UPDATE "WorkspaceMembershipEvents"
                SET "OldRole" = COALESCE("NewRole", 'Viewer')
                WHERE "OldRole" IS NULL
                """);

            migrationBuilder.AlterColumn<string>(
                name: "OldRole",
                table: "WorkspaceMembershipEvents",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16,
                oldNullable: true);
        }
    }
}
