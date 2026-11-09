using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jellyfin.Custom.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddUserLists : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CustomMarkers",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    DateApplied = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomMarkers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CustomUserLists",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                    DateCreated = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomUserLists", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CustomUserListItems",
                columns: table => new
                {
                    ListId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TmdbId = table.Column<int>(type: "INTEGER", nullable: false),
                    ImdbId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    AddedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomUserListItems", x => new { x.ListId, x.TmdbId });
                    table.ForeignKey(
                        name: "FK_CustomUserListItems_CustomUserLists_ListId",
                        column: x => x.ListId,
                        principalTable: "CustomUserLists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CustomUserListItems_TmdbId",
                table: "CustomUserListItems",
                column: "TmdbId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomUserLists_UserId",
                table: "CustomUserLists",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomUserLists_UserId_Default",
                table: "CustomUserLists",
                column: "UserId",
                unique: true,
                filter: "\"IsDefault\" = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CustomMarkers");

            migrationBuilder.DropTable(
                name: "CustomUserListItems");

            migrationBuilder.DropTable(
                name: "CustomUserLists");
        }
    }
}
