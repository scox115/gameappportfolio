using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DuelReplays : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DuelMoves",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BattleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Turn = table.Column<int>(type: "int", nullable: false),
                    PlayerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Card = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CardFailed = table.Column<bool>(type: "bit", nullable: false),
                    DamageDealt = table.Column<int>(type: "int", nullable: false),
                    HealthRestored = table.Column<int>(type: "int", nullable: false),
                    AttackBlocked = table.Column<bool>(type: "bit", nullable: false),
                    PlayerOneHp = table.Column<int>(type: "int", nullable: false),
                    PlayerTwoHp = table.Column<int>(type: "int", nullable: false),
                    PlayerOneShielded = table.Column<bool>(type: "bit", nullable: false),
                    PlayerTwoShielded = table.Column<bool>(type: "bit", nullable: false),
                    PlayedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DuelMoves", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DuelMoves_BattleId_Turn",
                table: "DuelMoves",
                columns: new[] { "BattleId", "Turn" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DuelMoves");
        }
    }
}
