using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gitberth.Data.Migrations
{
    /// <inheritdoc />
    public partial class PreviewsAndEnvironments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EnvVars_AppId_Key",
                table: "EnvVars");

            migrationBuilder.AddColumn<string>(
                name: "Scope",
                table: "EnvVars",
                type: "TEXT",
                nullable: false,
                defaultValue: "All");

            migrationBuilder.AddColumn<string>(
                name: "AccessAppId",
                table: "Domains",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Protected",
                table: "Domains",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ParentAppId",
                table: "Apps",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PreviewExpiryDays",
                table: "Apps",
                type: "INTEGER",
                nullable: false,
                defaultValue: 7);

            migrationBuilder.AddColumn<bool>(
                name: "PreviewForks",
                table: "Apps",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "PreviewLimit",
                table: "Apps",
                type: "INTEGER",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddColumn<string>(
                name: "PreviewMode",
                table: "Apps",
                type: "TEXT",
                nullable: false,
                defaultValue: "Off");

            migrationBuilder.AddColumn<string>(
                name: "PromotedOverCommit",
                table: "Apps",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ProtectPreviews",
                table: "Apps",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "Previews",
                columns: table => new
                {
                    AppId = table.Column<int>(type: "INTEGER", nullable: false),
                    ParentAppId = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Branch = table.Column<string>(type: "TEXT", nullable: false),
                    PullRequest = table.Column<int>(type: "INTEGER", nullable: true),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    Ref = table.Column<string>(type: "TEXT", nullable: false),
                    HeadSha = table.Column<string>(type: "TEXT", nullable: true),
                    FromFork = table.Column<bool>(type: "INTEGER", nullable: false),
                    CommentId = table.Column<long>(type: "INTEGER", nullable: true),
                    LastActivityAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Previews", x => x.AppId);
                    table.ForeignKey(
                        name: "FK_Previews_Apps_AppId",
                        column: x => x.AppId,
                        principalTable: "Apps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EnvVars_AppId_Key_Scope",
                table: "EnvVars",
                columns: new[] { "AppId", "Key", "Scope" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Apps_ParentAppId",
                table: "Apps",
                column: "ParentAppId");

            migrationBuilder.CreateIndex(
                name: "IX_Previews_ParentAppId",
                table: "Previews",
                column: "ParentAppId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Previews");

            migrationBuilder.DropIndex(
                name: "IX_EnvVars_AppId_Key_Scope",
                table: "EnvVars");

            migrationBuilder.DropIndex(
                name: "IX_Apps_ParentAppId",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "Scope",
                table: "EnvVars");

            migrationBuilder.DropColumn(
                name: "AccessAppId",
                table: "Domains");

            migrationBuilder.DropColumn(
                name: "Protected",
                table: "Domains");

            migrationBuilder.DropColumn(
                name: "ParentAppId",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "PreviewExpiryDays",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "PreviewForks",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "PreviewLimit",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "PreviewMode",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "PromotedOverCommit",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "ProtectPreviews",
                table: "Apps");

            migrationBuilder.CreateIndex(
                name: "IX_EnvVars_AppId_Key",
                table: "EnvVars",
                columns: new[] { "AppId", "Key" },
                unique: true);
        }
    }
}
