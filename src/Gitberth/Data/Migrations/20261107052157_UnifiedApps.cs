using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gitberth.Data.Migrations
{
    /// <inheritdoc />
    public partial class UnifiedApps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Fields",
                table: "ContainerChanges",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImageAfter",
                table: "ContainerChanges",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImageBefore",
                table: "ContainerChanges",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "ContainerChanges",
                type: "TEXT",
                nullable: false,
                defaultValue: "Ports");

            migrationBuilder.AddColumn<string>(
                name: "NewRestartPolicy",
                table: "ContainerChanges",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Container",
                table: "Apps",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContainerId",
                table: "Apps",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImageRef",
                table: "Apps",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Pinned",
                table: "Apps",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PreviousImage",
                table: "Apps",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RegistryId",
                table: "Apps",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RuntimeExtras",
                table: "Apps",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SecretKeys",
                table: "Apps",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "Apps",
                type: "TEXT",
                nullable: false,
                defaultValue: "Git");

            migrationBuilder.AddColumn<string>(
                name: "SpecProtected",
                table: "Apps",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UpdatePolicy",
                table: "Apps",
                type: "TEXT",
                nullable: false,
                defaultValue: "Off");

            migrationBuilder.CreateTable(
                name: "ActivityEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ContainerName = table.Column<string>(type: "TEXT", nullable: false),
                    AppId = table.Column<int>(type: "INTEGER", nullable: true),
                    At = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Tone = table.Column<string>(type: "TEXT", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivityEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ActivityEntries_Apps_AppId",
                        column: x => x.AppId,
                        principalTable: "Apps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ImageChecks",
                columns: table => new
                {
                    Reference = table.Column<string>(type: "TEXT", nullable: false),
                    RemoteDigest = table.Column<string>(type: "TEXT", nullable: true),
                    CheckedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    RetryAfter = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImageChecks", x => x.Reference);
                });

            migrationBuilder.CreateTable(
                name: "Registries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Host = table.Column<string>(type: "TEXT", nullable: false),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    ProtectedSecret = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    VerifiedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Registries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Apps_Container",
                table: "Apps",
                column: "Container",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ActivityEntries_AppId",
                table: "ActivityEntries",
                column: "AppId");

            migrationBuilder.CreateIndex(
                name: "IX_ActivityEntries_ContainerName_At",
                table: "ActivityEntries",
                columns: new[] { "ContainerName", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_Registries_Host",
                table: "Registries",
                column: "Host",
                unique: true);

            // The apps in today's menu stay in the menu: it lists the pinned ones now.
            migrationBuilder.Sql("UPDATE \"Apps\" SET \"Pinned\" = 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActivityEntries");

            migrationBuilder.DropTable(
                name: "ImageChecks");

            migrationBuilder.DropTable(
                name: "Registries");

            migrationBuilder.DropIndex(
                name: "IX_Apps_Container",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "Fields",
                table: "ContainerChanges");

            migrationBuilder.DropColumn(
                name: "ImageAfter",
                table: "ContainerChanges");

            migrationBuilder.DropColumn(
                name: "ImageBefore",
                table: "ContainerChanges");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "ContainerChanges");

            migrationBuilder.DropColumn(
                name: "NewRestartPolicy",
                table: "ContainerChanges");

            migrationBuilder.DropColumn(
                name: "Container",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "ContainerId",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "ImageRef",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "Pinned",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "PreviousImage",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "RegistryId",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "RuntimeExtras",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "SecretKeys",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "SpecProtected",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "UpdatePolicy",
                table: "Apps");
        }
    }
}
