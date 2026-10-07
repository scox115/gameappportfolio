using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Game.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddModeration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PreviousNormalizedUserName",
                table: "AspNetUsers",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PlayerReports",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ReporterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Note = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Outcome = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerReports", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_PreviousNormalizedUserName",
                table: "AspNetUsers",
                column: "PreviousNormalizedUserName");

            migrationBuilder.CreateIndex(
                name: "IX_PlayerReports_ReporterId_TargetId_Reason",
                table: "PlayerReports",
                columns: new[] { "ReporterId", "TargetId", "Reason" });

            migrationBuilder.CreateIndex(
                name: "IX_PlayerReports_ResolvedAt",
                table: "PlayerReports",
                column: "ResolvedAt");

            migrationBuilder.CreateIndex(
                name: "IX_PlayerReports_TargetId_Reason_ResolvedAt",
                table: "PlayerReports",
                columns: new[] { "TargetId", "Reason", "ResolvedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlayerReports");

            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_PreviousNormalizedUserName",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "PreviousNormalizedUserName",
                table: "AspNetUsers");
        }
    }
}
