using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddGoldShop : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DragonClawLevel",
                table: "PveBattles",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "FireballLevel",
                table: "PveBattles",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "HolyShieldLevel",
                table: "PveBattles",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "PlayerMaxHp",
                table: "PveBattles",
                type: "int",
                nullable: false,
                defaultValue: 100);

            migrationBuilder.AddColumn<int>(
                name: "Elixirs",
                table: "Players",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "Version",
                table: "Players",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "PlayerCardUpgrades",
                columns: table => new
                {
                    Card = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    PlayerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Level = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerCardUpgrades", x => new { x.PlayerId, x.Card });
                    table.ForeignKey(
                        name: "FK_PlayerCardUpgrades_Players_PlayerId",
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
                name: "PlayerCardUpgrades");

            migrationBuilder.DropColumn(
                name: "DragonClawLevel",
                table: "PveBattles");

            migrationBuilder.DropColumn(
                name: "FireballLevel",
                table: "PveBattles");

            migrationBuilder.DropColumn(
                name: "HolyShieldLevel",
                table: "PveBattles");

            migrationBuilder.DropColumn(
                name: "PlayerMaxHp",
                table: "PveBattles");

            migrationBuilder.DropColumn(
                name: "Elixirs",
                table: "Players");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "Players");
        }
    }
}
