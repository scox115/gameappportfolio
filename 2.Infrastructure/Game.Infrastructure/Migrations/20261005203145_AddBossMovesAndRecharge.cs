using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBossMovesAndRecharge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BossNextMove",
                table: "PveBattles",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                // Battles already in progress keep their announced attack as a plain Slash.
                defaultValue: "Slash");

            migrationBuilder.AddColumn<string>(
                name: "LastCardPlayed",
                table: "PveBattles",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BossNextMove",
                table: "PveBattles");

            migrationBuilder.DropColumn(
                name: "LastCardPlayed",
                table: "PveBattles");
        }
    }
}
