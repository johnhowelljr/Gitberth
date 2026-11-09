using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gitberth.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVercelVariablesSwitch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "VercelVariables",
                table: "Apps",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VercelVariables",
                table: "Apps");
        }
    }
}
