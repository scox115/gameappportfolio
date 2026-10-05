using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPvpShopItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PlayerOneDragonClawLevel",
                table: "PvpBattles",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "PlayerOneFireballLevel",
                table: "PvpBattles",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "PlayerOneHolyShieldLevel",
                table: "PvpBattles",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "PlayerOneMaxHp",
                table: "PvpBattles",
                type: "int",
                nullable: false,
                defaultValue: 100);

            migrationBuilder.AddColumn<int>(
                name: "PlayerTwoDragonClawLevel",
                table: "PvpBattles",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "PlayerTwoFireballLevel",
                table: "PvpBattles",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "PlayerTwoHolyShieldLevel",
                table: "PvpBattles",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "PlayerTwoMaxHp",
                table: "PvpBattles",
                type: "int",
                nullable: false,
                defaultValue: 100);

            migrationBuilder.AddColumn<int>(
                name: "DuelElixirs",
                table: "Players",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "EquippedTitle",
                table: "Players",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PvpWins",
                table: "Players",
                type: "int",
                nullable: false,
                defaultValue: 0);

            // Count the duels players already won, so titles unlock for existing PvP players too.
            // Boss fights have the fixed AI boss id as the second player and are left out.
            migrationBuilder.Sql("""
                UPDATE p SET PvpWins = (
                    SELECT COUNT(*) FROM Matches m
                    WHERE m.WinnerPlayerId = p.Id
                      AND m.PlayerTwoId <> '00000000-0000-0000-0000-00000000b055')
                FROM Players p;
                """);

            migrationBuilder.CreateTable(
                name: "PlayerTitles",
                columns: table => new
                {
                    Title = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    PlayerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerTitles", x => new { x.PlayerId, x.Title });
                    table.ForeignKey(
                        name: "FK_PlayerTitles_Players_PlayerId",
                        column: x => x.PlayerId,
                        principalTable: "Players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlayerTitles");

            migrationBuilder.DropColumn(
                name: "PlayerOneDragonClawLevel",
                table: "PvpBattles");

            migrationBuilder.DropColumn(
                name: "PlayerOneFireballLevel",
                table: "PvpBattles");

            migrationBuilder.DropColumn(
                name: "PlayerOneHolyShieldLevel",
                table: "PvpBattles");

            migrationBuilder.DropColumn(
                name: "PlayerOneMaxHp",
                table: "PvpBattles");

            migrationBuilder.DropColumn(
                name: "PlayerTwoDragonClawLevel",
                table: "PvpBattles");

            migrationBuilder.DropColumn(
                name: "PlayerTwoFireballLevel",
                table: "PvpBattles");

            migrationBuilder.DropColumn(
                name: "PlayerTwoHolyShieldLevel",
                table: "PvpBattles");

            migrationBuilder.DropColumn(
                name: "PlayerTwoMaxHp",
                table: "PvpBattles");

            migrationBuilder.DropColumn(
                name: "DuelElixirs",
                table: "Players");

            migrationBuilder.DropColumn(
                name: "EquippedTitle",
                table: "Players");

            migrationBuilder.DropColumn(
                name: "PvpWins",
                table: "Players");
        }
    }
}
