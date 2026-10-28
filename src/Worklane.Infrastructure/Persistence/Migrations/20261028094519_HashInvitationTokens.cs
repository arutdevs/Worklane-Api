using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Worklane.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HashInvitationTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pgcrypto;");

            migrationBuilder.AddColumn<string>(
                name: "TokenHash",
                table: "Invitations",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            // Hash the plaintext that was already issued. sha256 hex from pgcrypto is
            // lowercase; the application stores Convert.ToHexString, which is uppercase.
            migrationBuilder.Sql("""
                UPDATE "Invitations"
                SET "TokenHash" = upper(encode(digest(convert_to("Token", 'UTF8'), 'sha256'), 'hex'))
                WHERE "TokenHash" IS NULL;
                """);

            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "Invitations" WHERE "TokenHash" IS NULL) THEN
                        RAISE EXCEPTION 'invitation token hash backfill left a null TokenHash';
                    END IF;
                END $$;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "TokenHash",
                table: "Invitations",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64,
                oldNullable: true);

            migrationBuilder.DropIndex(
                name: "IX_Invitations_Token",
                table: "Invitations");

            migrationBuilder.DropColumn(
                name: "Token",
                table: "Invitations");

            migrationBuilder.CreateIndex(
                name: "IX_Invitations_TokenHash",
                table: "Invitations",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The hash is one-way, so a rollback cannot restore the original token.
            // The invitation id keeps the unique index valid; previously sent links stop working.
            migrationBuilder.DropIndex(
                name: "IX_Invitations_TokenHash",
                table: "Invitations");

            migrationBuilder.AddColumn<string>(
                name: "Token",
                table: "Invitations",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE "Invitations" SET "Token" = "Id"::text WHERE "Token" IS NULL;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "Token",
                table: "Invitations",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128,
                oldNullable: true);

            migrationBuilder.DropColumn(
                name: "TokenHash",
                table: "Invitations");

            migrationBuilder.CreateIndex(
                name: "IX_Invitations_Token",
                table: "Invitations",
                column: "Token",
                unique: true);
        }
    }
}
