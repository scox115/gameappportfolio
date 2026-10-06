using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddClassRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlayerClassRecords",
                columns: table => new
                {
                    Class = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    PlayerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Wins = table.Column<int>(type: "int", nullable: false),
                    Losses = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerClassRecords", x => new { x.PlayerId, x.Class });
                    table.ForeignKey(
                        name: "FK_PlayerClassRecords_Players_PlayerId",
                        column: x => x.PlayerId,
                        principalTable: "Players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Duels before this weren't split by class, so credit each hero's existing record to
            // the class they play now.
            migrationBuilder.Sql(
                """
                INSERT INTO PlayerClassRecords (PlayerId, Class, Wins, Losses)
                SELECT Id, Class, PvpWins, PvpLosses
                FROM Players
                WHERE PvpWins > 0 OR PvpLosses > 0
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlayerClassRecords");
        }
    }
}
