using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jellyfin.Custom.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddDiscoveryPickSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CustomDiscoveryPickSnapshots",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Day = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    ListIdsJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomDiscoveryPickSnapshots", x => new { x.UserId, x.Day });
                });

            migrationBuilder.CreateIndex(
                name: "IX_CustomDiscoveryPickSnapshots_Day",
                table: "CustomDiscoveryPickSnapshots",
                column: "Day");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CustomDiscoveryPickSnapshots");
        }
    }
}
