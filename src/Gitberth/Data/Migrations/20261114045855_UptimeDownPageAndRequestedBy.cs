using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gitberth.Data.Migrations
{
    /// <inheritdoc />
    public partial class UptimeDownPageAndRequestedBy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RequestedBy",
                table: "Deployments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "DownPage",
                table: "Apps",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "DownPageMessage",
                table: "Apps",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "UptimeCheck",
                table: "Apps",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "UptimePath",
                table: "Apps",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RequestedBy",
                table: "Deployments");

            migrationBuilder.DropColumn(
                name: "DownPage",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "DownPageMessage",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "UptimeCheck",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "UptimePath",
                table: "Apps");
        }
    }
}
