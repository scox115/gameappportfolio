using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RankedSeasons : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SeasonLosses",
                table: "Players",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateOnly>(
                name: "SeasonStart",
                table: "Players",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SeasonWins",
                table: "Players",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "ClosedSeasons",
                columns: table => new
                {
                    SeasonStart = table.Column<DateOnly>(type: "date", nullable: false),
                    ClosedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RankedHeroes = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClosedSeasons", x => x.SeasonStart);
                });

            migrationBuilder.CreateTable(
                name: "PlayerSeasonRecords",
                columns: table => new
                {
                    SeasonStart = table.Column<DateOnly>(type: "date", nullable: false),
                    PlayerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Rating = table.Column<int>(type: "int", nullable: false),
                    Wins = table.Column<int>(type: "int", nullable: false),
                    Losses = table.Column<int>(type: "int", nullable: false),
                    Rank = table.Column<int>(type: "int", nullable: true),
                    RewardGold = table.Column<int>(type: "int", nullable: false),
                    Settled = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerSeasonRecords", x => new { x.PlayerId, x.SeasonStart });
                    table.ForeignKey(
                        name: "FK_PlayerSeasonRecords_Players_PlayerId",
                        column: x => x.PlayerId,
                        principalTable: "Players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Players_SeasonStart",
                table: "Players",
                column: "SeasonStart");

            migrationBuilder.CreateIndex(
                name: "IX_PlayerSeasonRecords_SeasonStart_Rank",
                table: "PlayerSeasonRecords",
                columns: new[] { "SeasonStart", "Rank" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClosedSeasons");

            migrationBuilder.DropTable(
                name: "PlayerSeasonRecords");

            migrationBuilder.DropIndex(
                name: "IX_Players_SeasonStart",
                table: "Players");

            migrationBuilder.DropColumn(
                name: "SeasonLosses",
                table: "Players");

            migrationBuilder.DropColumn(
                name: "SeasonStart",
                table: "Players");

            migrationBuilder.DropColumn(
                name: "SeasonWins",
                table: "Players");
        }
    }
}
