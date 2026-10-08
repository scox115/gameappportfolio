using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class LiveLobby : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OnlinePresence",
                columns: table => new
                {
                    ConnectionId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    PlayerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SeenAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OnlinePresence", x => x.ConnectionId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Matches_CreatedAt",
                table: "Matches",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_OnlinePresence_SeenAt_PlayerId",
                table: "OnlinePresence",
                columns: new[] { "SeenAt", "PlayerId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OnlinePresence");

            migrationBuilder.DropIndex(
                name: "IX_Matches_CreatedAt",
                table: "Matches");
        }
    }
}
