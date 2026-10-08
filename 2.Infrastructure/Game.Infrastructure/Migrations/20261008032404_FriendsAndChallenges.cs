using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FriendsAndChallenges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DuelChallenges",
                columns: table => new
                {
                    ChallengerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChallengedId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChallengerNetwork = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    SentAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DuelChallenges", x => x.ChallengerId);
                });

            migrationBuilder.CreateTable(
                name: "Friendships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequesterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AddresseeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PairKey = table.Column<string>(type: "nvarchar(65)", maxLength: 65, nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AcceptedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Friendships", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DuelChallenges_ChallengedId",
                table: "DuelChallenges",
                column: "ChallengedId");

            migrationBuilder.CreateIndex(
                name: "IX_DuelChallenges_Id",
                table: "DuelChallenges",
                column: "Id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Friendships_AddresseeId",
                table: "Friendships",
                column: "AddresseeId");

            migrationBuilder.CreateIndex(
                name: "IX_Friendships_PairKey",
                table: "Friendships",
                column: "PairKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Friendships_RequesterId",
                table: "Friendships",
                column: "RequesterId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DuelChallenges");

            migrationBuilder.DropTable(
                name: "Friendships");
        }
    }
}
