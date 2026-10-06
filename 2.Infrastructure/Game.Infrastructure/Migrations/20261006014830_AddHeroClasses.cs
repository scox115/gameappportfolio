using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddHeroClasses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Everyone who played before classes existed was a Sorcerer.
            migrationBuilder.AddColumn<string>(
                name: "PlayerOneClass",
                table: "PvpBattles",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Sorcerer");

            migrationBuilder.AddColumn<string>(
                name: "PlayerTwoClass",
                table: "PvpBattles",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Sorcerer");

            migrationBuilder.AddColumn<string>(
                name: "Class",
                table: "PveBattles",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Sorcerer");

            migrationBuilder.AddColumn<string>(
                name: "Class",
                table: "Players",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Sorcerer");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PlayerOneClass",
                table: "PvpBattles");

            migrationBuilder.DropColumn(
                name: "PlayerTwoClass",
                table: "PvpBattles");

            migrationBuilder.DropColumn(
                name: "Class",
                table: "PveBattles");

            migrationBuilder.DropColumn(
                name: "Class",
                table: "Players");
        }
    }
}
