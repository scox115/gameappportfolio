using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMatchHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DailyArenaStats",
                columns: table => new
                {
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    BossFights = table.Column<int>(type: "int", nullable: false),
                    BossWins = table.Column<int>(type: "int", nullable: false),
                    HeroicFights = table.Column<int>(type: "int", nullable: false),
                    HeroicWins = table.Column<int>(type: "int", nullable: false),
                    Duels = table.Column<int>(type: "int", nullable: false),
                    DuelKnockouts = table.Column<int>(type: "int", nullable: false),
                    DuelTimeouts = table.Column<int>(type: "int", nullable: false),
                    DuelForfeits = table.Column<int>(type: "int", nullable: false),
                    GoldPaid = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailyArenaStats", x => x.Day);
                });

            migrationBuilder.CreateTable(
                name: "MatchHistory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlayerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Won = table.Column<bool>(type: "bit", nullable: false),
                    Class = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    OpponentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OpponentName = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    OpponentClass = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    Difficulty = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    EndReason = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    Turns = table.Column<int>(type: "int", nullable: false),
                    GoldEarned = table.Column<int>(type: "int", nullable: false),
                    ExperienceEarned = table.Column<int>(type: "int", nullable: false),
                    RatingChange = table.Column<int>(type: "int", nullable: true),
                    WagerResult = table.Column<int>(type: "int", nullable: false),
                    PlayedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MatchHistory", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MatchHistory_MatchId_PlayerId",
                table: "MatchHistory",
                columns: new[] { "MatchId", "PlayerId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MatchHistory_PlayerId_PlayedAt",
                table: "MatchHistory",
                columns: new[] { "PlayerId", "PlayedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DailyArenaStats");

            migrationBuilder.DropTable(
                name: "MatchHistory");
        }
    }
}
