using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jellyfin.Custom.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddDiscoveryRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CustomDiscoveryRequests",
                columns: table => new
                {
                    TmdbId = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomDiscoveryRequests", x => x.TmdbId);
                });

            migrationBuilder.CreateTable(
                name: "CustomDiscoverySettings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomDiscoverySettings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "CustomDiscoveryRequestUsers",
                columns: table => new
                {
                    TmdbId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomDiscoveryRequestUsers", x => new { x.TmdbId, x.UserId });
                    table.ForeignKey(
                        name: "FK_CustomDiscoveryRequestUsers_CustomDiscoveryRequests_TmdbId",
                        column: x => x.TmdbId,
                        principalTable: "CustomDiscoveryRequests",
                        principalColumn: "TmdbId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CustomDiscoveryRequests_State",
                table: "CustomDiscoveryRequests",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_CustomDiscoveryRequestUsers_UserId",
                table: "CustomDiscoveryRequestUsers",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CustomDiscoveryRequestUsers");

            migrationBuilder.DropTable(
                name: "CustomDiscoverySettings");

            migrationBuilder.DropTable(
                name: "CustomDiscoveryRequests");
        }
    }
}
