using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPvpRating : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PvpLosses",
                table: "Players",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Rating",
                table: "Players",
                type: "int",
                nullable: false,
                defaultValue: 1000);

            // Count the duels players already lost, so their win-loss record starts out right.
            // Boss fights have the fixed AI boss id as the second player and are left out.
            // Existing players start at the base rating; old duels aren't replayed.
            migrationBuilder.Sql("""
                UPDATE p SET PvpLosses = (
                    SELECT COUNT(*) FROM Matches m
                    WHERE (m.PlayerOneId = p.Id OR m.PlayerTwoId = p.Id)
                      AND m.WinnerPlayerId IS NOT NULL
                      AND m.WinnerPlayerId <> p.Id
                      AND m.PlayerTwoId <> '00000000-0000-0000-0000-00000000b055')
                FROM Players p;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Players_Rating",
                table: "Players",
                column: "Rating");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Players_Rating",
                table: "Players");

            migrationBuilder.DropColumn(
                name: "PvpLosses",
                table: "Players");

            migrationBuilder.DropColumn(
                name: "Rating",
                table: "Players");
        }
    }
}
