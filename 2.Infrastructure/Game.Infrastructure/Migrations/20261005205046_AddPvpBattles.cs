using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPvpBattles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PvpBattles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlayerOneId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlayerTwoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlayerOneHp = table.Column<int>(type: "int", nullable: false),
                    PlayerTwoHp = table.Column<int>(type: "int", nullable: false),
                    PlayerOneShielded = table.Column<bool>(type: "bit", nullable: false),
                    PlayerTwoShielded = table.Column<bool>(type: "bit", nullable: false),
                    PlayerOneLastCard = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    PlayerTwoLastCard = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    ActivePlayerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TurnDeadline = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Turn = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    WinnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EndReason = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    MatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Version = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PvpBattles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PvpBattles_PlayerOneId_Status",
                table: "PvpBattles",
                columns: new[] { "PlayerOneId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_PvpBattles_PlayerTwoId_Status",
                table: "PvpBattles",
                columns: new[] { "PlayerTwoId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_PvpBattles_Status_TurnDeadline",
                table: "PvpBattles",
                columns: new[] { "Status", "TurnDeadline" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PvpBattles");
        }
    }
}
