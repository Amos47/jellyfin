using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jellyfin.Custom.Database.Migrations
{
    /// <summary>
    /// Adds the season requests of shows and the Sonarr season statuses the discovery app reports. Two new tables;
    /// nothing existing changes.
    /// </summary>
    public partial class AddShowSeasons : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CustomSeasonRequests",
                columns: table => new
                {
                    SeriesTmdbId = table.Column<int>(type: "INTEGER", nullable: false),
                    SeasonNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomSeasonRequests", x => new { x.SeriesTmdbId, x.SeasonNumber });
                });

            migrationBuilder.CreateTable(
                name: "CustomSonarrSeasonStatus",
                columns: table => new
                {
                    SeriesTmdbId = table.Column<int>(type: "INTEGER", nullable: false),
                    SeasonNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Mismatch = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomSonarrSeasonStatus", x => new { x.SeriesTmdbId, x.SeasonNumber });
                });

            migrationBuilder.CreateIndex(
                name: "IX_CustomSeasonRequests_State",
                table: "CustomSeasonRequests",
                column: "State");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CustomSeasonRequests");

            migrationBuilder.DropTable(
                name: "CustomSonarrSeasonStatus");
        }
    }
}
