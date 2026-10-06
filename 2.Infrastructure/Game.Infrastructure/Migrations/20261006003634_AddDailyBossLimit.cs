using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDailyBossLimit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "BossWinsDay",
                table: "Players",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BossWinsToday",
                table: "Players",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BossWinsDay",
                table: "Players");

            migrationBuilder.DropColumn(
                name: "BossWinsToday",
                table: "Players");
        }
    }
}
