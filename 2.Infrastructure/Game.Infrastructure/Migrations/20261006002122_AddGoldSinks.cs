using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddGoldSinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Wager",
                table: "PvpBattles",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "EquippedCardSkin",
                table: "Players",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EquippedFrame",
                table: "Players",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WinStreak",
                table: "Players",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "PlayerBounties",
                columns: table => new
                {
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    Bounty = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    PlayerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Progress = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerBounties", x => new { x.PlayerId, x.Day, x.Bounty });
                    table.ForeignKey(
                        name: "FK_PlayerBounties_Players_PlayerId",
                        column: x => x.PlayerId,
                        principalTable: "Players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PlayerCosmetics",
                columns: table => new
                {
                    Cosmetic = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    PlayerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerCosmetics", x => new { x.PlayerId, x.Cosmetic });
                    table.ForeignKey(
                        name: "FK_PlayerCosmetics_Players_PlayerId",
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
                name: "PlayerBounties");

            migrationBuilder.DropTable(
                name: "PlayerCosmetics");

            migrationBuilder.DropColumn(
                name: "Wager",
                table: "PvpBattles");

            migrationBuilder.DropColumn(
                name: "EquippedCardSkin",
                table: "Players");

            migrationBuilder.DropColumn(
                name: "EquippedFrame",
                table: "Players");

            migrationBuilder.DropColumn(
                name: "WinStreak",
                table: "Players");
        }
    }
}
