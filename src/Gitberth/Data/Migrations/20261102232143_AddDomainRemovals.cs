using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gitberth.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDomainRemovals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "KeepPaths",
                table: "Domains",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "DomainRemovals",
                columns: table => new
                {
                    Hostname = table.Column<string>(type: "TEXT", nullable: false),
                    Service = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DomainRemovals", x => x.Hostname);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DomainRemovals");

            migrationBuilder.DropColumn(
                name: "KeepPaths",
                table: "Domains");
        }
    }
}
