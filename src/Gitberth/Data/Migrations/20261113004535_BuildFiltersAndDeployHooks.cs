using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gitberth.Data.Migrations
{
    /// <inheritdoc />
    public partial class BuildFiltersAndDeployHooks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HookName",
                table: "Deployments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "NoCache",
                table: "Deployments",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SkipReason",
                table: "Deployments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IgnoredPaths",
                table: "Apps",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WatchPaths",
                table: "Apps",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DeployHooks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AppId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", nullable: false),
                    ProtectedToken = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeployHooks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeployHooks_Apps_AppId",
                        column: x => x.AppId,
                        principalTable: "Apps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeployHooks_AppId",
                table: "DeployHooks",
                column: "AppId");

            migrationBuilder.CreateIndex(
                name: "IX_DeployHooks_TokenHash",
                table: "DeployHooks",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeployHooks");

            migrationBuilder.DropColumn(
                name: "HookName",
                table: "Deployments");

            migrationBuilder.DropColumn(
                name: "NoCache",
                table: "Deployments");

            migrationBuilder.DropColumn(
                name: "SkipReason",
                table: "Deployments");

            migrationBuilder.DropColumn(
                name: "IgnoredPaths",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "WatchPaths",
                table: "Apps");
        }
    }
}
