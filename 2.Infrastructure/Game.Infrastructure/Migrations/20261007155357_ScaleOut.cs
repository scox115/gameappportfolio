using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ScaleOut : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ClaimToken",
                table: "OutboxMessages",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<DateTime>(
                name: "ClaimedUntil",
                table: "OutboxMessages",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "HubMessages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Hub = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Method = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    UserIds = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Arguments = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Origin = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HubMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PvpLobby",
                columns: table => new
                {
                    PlayerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Wager = table.Column<int>(type: "int", nullable: false),
                    Network = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    JoinedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SeenAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PvpLobby", x => x.PlayerId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HubMessages_CreatedAt",
                table: "HubMessages",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_PvpLobby_Wager_JoinedAt",
                table: "PvpLobby",
                columns: new[] { "Wager", "JoinedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HubMessages");

            migrationBuilder.DropTable(
                name: "PvpLobby");

            migrationBuilder.DropColumn(
                name: "ClaimToken",
                table: "OutboxMessages");

            migrationBuilder.DropColumn(
                name: "ClaimedUntil",
                table: "OutboxMessages");
        }
    }
}
