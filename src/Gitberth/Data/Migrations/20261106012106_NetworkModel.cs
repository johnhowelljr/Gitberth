using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gitberth.Data.Migrations
{
    /// <inheritdoc />
    public partial class NetworkModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Access",
                table: "Ports",
                type: "TEXT",
                nullable: false,
                defaultValue: "Lan");

            migrationBuilder.AddColumn<bool>(
                name: "IsWeb",
                table: "Ports",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Who reaches each port comes from the address it was published on: every address is the LAN, the loopback the
            // host only, any other address Custom, which alone keeps its address.
            migrationBuilder.Sql("""
                UPDATE "Ports" SET "Access" = CASE
                    WHEN "HostIp" IS NULL OR TRIM("HostIp", ' []') IN ('', '0.0.0.0', '::') THEN 'Lan'
                    WHEN TRIM("HostIp", ' []') IN ('127.0.0.1', '::1') THEN 'HostOnly'
                    ELSE 'Custom' END;
                """);
            migrationBuilder.Sql("""UPDATE "Ports" SET "HostIp" = CASE WHEN "Access" = 'Custom' THEN TRIM("HostIp") END;""");
            // The web port is each app's first TCP port: the one Open, domains, the health URL and scheduled jobs used so far.
            migrationBuilder.Sql("""
                UPDATE "Ports" SET "IsWeb" = 1
                WHERE "Id" IN (SELECT MIN("Id") FROM "Ports" WHERE LOWER("Protocol") = 'tcp' GROUP BY "AppId");
                """);

            // HostPort becomes nullable: an Internal port has none. SQLite can't change a column, so the table is rebuilt,
            // here by hand: EF Core's own rebuild turns foreign keys off around it, which commits the migration in four steps,
            // and a start stopped between two would leave a database no later start could migrate. Nothing refers to Ports,
            // so it's rebuilt with foreign keys on, in the migration's one transaction, as EF Core would have it.
            migrationBuilder.Sql("""
                CREATE TABLE "ef_temp_Ports" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_Ports" PRIMARY KEY AUTOINCREMENT,
                    "Access" TEXT NOT NULL,
                    "AppId" INTEGER NOT NULL,
                    "ContainerPort" INTEGER NOT NULL,
                    "HostIp" TEXT NULL,
                    "HostPort" INTEGER NULL,
                    "IsWeb" INTEGER NOT NULL,
                    "Protocol" TEXT NOT NULL,
                    CONSTRAINT "FK_Ports_Apps_AppId" FOREIGN KEY ("AppId") REFERENCES "Apps" ("Id") ON DELETE CASCADE
                );
                """);
            migrationBuilder.Sql("""
                INSERT INTO "ef_temp_Ports" ("Id", "Access", "AppId", "ContainerPort", "HostIp", "HostPort", "IsWeb", "Protocol")
                SELECT "Id", "Access", "AppId", "ContainerPort", "HostIp", "HostPort", "IsWeb", "Protocol"
                FROM "Ports";
                """);
            migrationBuilder.Sql("""DROP TABLE "Ports";""");
            migrationBuilder.Sql("""ALTER TABLE "ef_temp_Ports" RENAME TO "Ports";""");
            migrationBuilder.Sql("""CREATE INDEX "IX_Ports_AppId" ON "Ports" ("AppId");""");

            migrationBuilder.CreateTable(
                name: "AppNetworks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AppId = table.Column<int>(type: "INTEGER", nullable: false),
                    Network = table.Column<string>(type: "TEXT", nullable: false),
                    Alias = table.Column<string>(type: "TEXT", nullable: true),
                    IPv4Address = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppNetworks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppNetworks_Apps_AppId",
                        column: x => x.AppId,
                        principalTable: "Apps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ContainerChanges",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ContainerName = table.Column<string>(type: "TEXT", nullable: false),
                    Summary = table.Column<string>(type: "TEXT", nullable: false),
                    OldId = table.Column<string>(type: "TEXT", nullable: false),
                    NewId = table.Column<string>(type: "TEXT", nullable: true),
                    OldName = table.Column<string>(type: "TEXT", nullable: false),
                    TempName = table.Column<string>(type: "TEXT", nullable: false),
                    WasRunning = table.Column<bool>(type: "INTEGER", nullable: false),
                    OldRestartPolicy = table.Column<string>(type: "TEXT", nullable: false),
                    Step = table.Column<string>(type: "TEXT", nullable: false),
                    StopAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Outcome = table.Column<string>(type: "TEXT", nullable: false),
                    OfflineMs = table.Column<int>(type: "INTEGER", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    Notice = table.Column<string>(type: "TEXT", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContainerChanges", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ContainerPortNotes",
                columns: table => new
                {
                    ContainerName = table.Column<string>(type: "TEXT", nullable: false),
                    Port = table.Column<int>(type: "INTEGER", nullable: false),
                    Protocol = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContainerPortNotes", x => new { x.ContainerName, x.Port, x.Protocol });
                });

            migrationBuilder.CreateTable(
                name: "KeptNetworks",
                columns: table => new
                {
                    ContainerName = table.Column<string>(type: "TEXT", nullable: false),
                    Network = table.Column<string>(type: "TEXT", nullable: false),
                    Alias = table.Column<string>(type: "TEXT", nullable: true),
                    IPv4Address = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KeptNetworks", x => new { x.ContainerName, x.Network });
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppNetworks_AppId_Network",
                table: "AppNetworks",
                columns: new[] { "AppId", "Network" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ContainerChanges_ContainerName",
                table: "ContainerChanges",
                column: "ContainerName");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Before Access, a port was always published, and the host only was its address: an Internal port can't be kept.
            migrationBuilder.Sql("""DELETE FROM "Ports" WHERE "HostPort" IS NULL;""");
            migrationBuilder.Sql("""UPDATE "Ports" SET "HostIp" = '127.0.0.1' WHERE "Access" = 'HostOnly';""");

            migrationBuilder.DropTable(
                name: "AppNetworks");

            migrationBuilder.DropTable(
                name: "ContainerChanges");

            migrationBuilder.DropTable(
                name: "ContainerPortNotes");

            migrationBuilder.DropTable(
                name: "KeptNetworks");

            migrationBuilder.DropColumn(
                name: "Access",
                table: "Ports");

            migrationBuilder.DropColumn(
                name: "IsWeb",
                table: "Ports");

            migrationBuilder.AlterColumn<int>(
                name: "HostPort",
                table: "Ports",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldNullable: true);
        }
    }
}
