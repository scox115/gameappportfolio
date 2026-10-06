using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddHeroicBoss : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Difficulty",
                table: "PveBattles",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                // Battles fought before the Heroic boss existed were all against the normal boss.
                defaultValue: "Normal");

            migrationBuilder.AddColumn<DateOnly>(
                name: "HeroicWinDay",
                table: "Players",
                type: "date",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Difficulty",
                table: "PveBattles");

            migrationBuilder.DropColumn(
                name: "HeroicWinDay",
                table: "Players");
        }
    }
}
