using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jellyfin.Custom.Database.Migrations
{
    /// <summary>
    /// Adds the media type (Movie or Series) to the TMDB-keyed tables. The type joins their primary keys, which SQLite
    /// can't alter, so each is rebuilt: a new table, every row copied in as <c>Movie</c>, the old one dropped and the new
    /// one renamed. Lists only gain an <c>ItemType</c> column.
    /// </summary>
    /// <remarks>
    /// Written by hand: EF's own rebuild copies the request users into their new table, whose foreign key names the
    /// new (TmdbId, MediaType) key of the requests, before the requests have it, which SQLite refuses ("foreign key
    /// mismatch"). Here the parent is rebuilt first, with foreign keys off for the swap so dropping an old table
    /// doesn't cascade its children away, as EF does for its own rebuilds.
    /// </remarks>
    public partial class AddDiscoveryMediaType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("PRAGMA foreign_keys = 0;", suppressTransaction: true);

            migrationBuilder.Sql("ALTER TABLE \"CustomDiscoveryLists\" ADD \"ItemType\" TEXT NOT NULL DEFAULT 'Movie';");

            Rebuild(
                migrationBuilder,
                "CustomDiscoveryShown",
                """
                    "TmdbId" INTEGER NOT NULL,
                    "MediaType" TEXT NOT NULL DEFAULT 'Movie',
                    "FirstShown" TEXT NOT NULL,
                    "LastShown" TEXT NOT NULL,
                    CONSTRAINT "PK_CustomDiscoveryShown" PRIMARY KEY ("TmdbId", "MediaType")
                """,
                "\"TmdbId\", \"FirstShown\", \"LastShown\"");

            Rebuild(
                migrationBuilder,
                "CustomDiscoveryPickSnapshots",
                """
                    "UserId" TEXT NOT NULL,
                    "Day" TEXT NOT NULL,
                    "MediaType" TEXT NOT NULL DEFAULT 'Movie',
                    "ListIdsJson" TEXT NOT NULL,
                    CONSTRAINT "PK_CustomDiscoveryPickSnapshots" PRIMARY KEY ("UserId", "Day", "MediaType")
                """,
                "\"UserId\", \"Day\", \"ListIdsJson\"");
            migrationBuilder.Sql("CREATE INDEX \"IX_CustomDiscoveryPickSnapshots_Day\" ON \"CustomDiscoveryPickSnapshots\" (\"Day\");");

            Rebuild(
                migrationBuilder,
                "CustomUserListItems",
                """
                    "ListId" TEXT NOT NULL,
                    "TmdbId" INTEGER NOT NULL,
                    "MediaType" TEXT NOT NULL DEFAULT 'Movie',
                    "AddedAt" TEXT NOT NULL,
                    "ImdbId" TEXT NULL,
                    CONSTRAINT "PK_CustomUserListItems" PRIMARY KEY ("ListId", "TmdbId", "MediaType"),
                    CONSTRAINT "FK_CustomUserListItems_CustomUserLists_ListId" FOREIGN KEY ("ListId") REFERENCES "CustomUserLists" ("Id") ON DELETE CASCADE
                """,
                "\"ListId\", \"TmdbId\", \"AddedAt\", \"ImdbId\"");
            migrationBuilder.Sql("CREATE INDEX \"IX_CustomUserListItems_TmdbId\" ON \"CustomUserListItems\" (\"TmdbId\");");

            // The requests before their users, whose foreign key names the requests' new key.
            Rebuild(
                migrationBuilder,
                "CustomDiscoveryRequests",
                """
                    "TmdbId" INTEGER NOT NULL,
                    "MediaType" TEXT NOT NULL DEFAULT 'Movie',
                    "State" TEXT NOT NULL,
                    "UpdatedAt" TEXT NOT NULL,
                    CONSTRAINT "PK_CustomDiscoveryRequests" PRIMARY KEY ("TmdbId", "MediaType")
                """,
                "\"TmdbId\", \"State\", \"UpdatedAt\"");
            migrationBuilder.Sql("CREATE INDEX \"IX_CustomDiscoveryRequests_State\" ON \"CustomDiscoveryRequests\" (\"State\");");

            Rebuild(
                migrationBuilder,
                "CustomDiscoveryRequestUsers",
                """
                    "TmdbId" INTEGER NOT NULL,
                    "MediaType" TEXT NOT NULL DEFAULT 'Movie',
                    "UserId" TEXT NOT NULL,
                    CONSTRAINT "PK_CustomDiscoveryRequestUsers" PRIMARY KEY ("TmdbId", "MediaType", "UserId"),
                    CONSTRAINT "FK_CustomDiscoveryRequestUsers_CustomDiscoveryRequests_TmdbId_MediaType" FOREIGN KEY ("TmdbId", "MediaType") REFERENCES "CustomDiscoveryRequests" ("TmdbId", "MediaType") ON DELETE CASCADE
                """,
                "\"TmdbId\", \"UserId\"");
            migrationBuilder.Sql("CREATE INDEX \"IX_CustomDiscoveryRequestUsers_UserId\" ON \"CustomDiscoveryRequestUsers\" (\"UserId\");");

            migrationBuilder.Sql("PRAGMA foreign_keys = 1;", suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Back to one row per TMDB id: only the movies' rows are kept.
            migrationBuilder.Sql("PRAGMA foreign_keys = 0;", suppressTransaction: true);

            // Show lists go: without the column they would read as movie lists.
            migrationBuilder.Sql("DELETE FROM \"CustomDiscoveryLists\" WHERE \"ItemType\" <> 'Movie';");
            migrationBuilder.Sql("ALTER TABLE \"CustomDiscoveryLists\" DROP COLUMN \"ItemType\";");

            RebuildDown(
                migrationBuilder,
                "CustomDiscoveryShown",
                """
                    "TmdbId" INTEGER NOT NULL,
                    "FirstShown" TEXT NOT NULL,
                    "LastShown" TEXT NOT NULL,
                    CONSTRAINT "PK_CustomDiscoveryShown" PRIMARY KEY ("TmdbId")
                """,
                "\"TmdbId\", \"FirstShown\", \"LastShown\"");

            RebuildDown(
                migrationBuilder,
                "CustomDiscoveryPickSnapshots",
                """
                    "UserId" TEXT NOT NULL,
                    "Day" TEXT NOT NULL,
                    "ListIdsJson" TEXT NOT NULL,
                    CONSTRAINT "PK_CustomDiscoveryPickSnapshots" PRIMARY KEY ("UserId", "Day")
                """,
                "\"UserId\", \"Day\", \"ListIdsJson\"");
            migrationBuilder.Sql("CREATE INDEX \"IX_CustomDiscoveryPickSnapshots_Day\" ON \"CustomDiscoveryPickSnapshots\" (\"Day\");");

            RebuildDown(
                migrationBuilder,
                "CustomUserListItems",
                """
                    "ListId" TEXT NOT NULL,
                    "TmdbId" INTEGER NOT NULL,
                    "ImdbId" TEXT NULL,
                    "AddedAt" TEXT NOT NULL,
                    CONSTRAINT "PK_CustomUserListItems" PRIMARY KEY ("ListId", "TmdbId"),
                    CONSTRAINT "FK_CustomUserListItems_CustomUserLists_ListId" FOREIGN KEY ("ListId") REFERENCES "CustomUserLists" ("Id") ON DELETE CASCADE
                """,
                "\"ListId\", \"TmdbId\", \"ImdbId\", \"AddedAt\"");
            migrationBuilder.Sql("CREATE INDEX \"IX_CustomUserListItems_TmdbId\" ON \"CustomUserListItems\" (\"TmdbId\");");

            RebuildDown(
                migrationBuilder,
                "CustomDiscoveryRequests",
                """
                    "TmdbId" INTEGER NOT NULL,
                    "State" TEXT NOT NULL,
                    "UpdatedAt" TEXT NOT NULL,
                    CONSTRAINT "PK_CustomDiscoveryRequests" PRIMARY KEY ("TmdbId")
                """,
                "\"TmdbId\", \"State\", \"UpdatedAt\"");
            migrationBuilder.Sql("CREATE INDEX \"IX_CustomDiscoveryRequests_State\" ON \"CustomDiscoveryRequests\" (\"State\");");

            RebuildDown(
                migrationBuilder,
                "CustomDiscoveryRequestUsers",
                """
                    "TmdbId" INTEGER NOT NULL,
                    "UserId" TEXT NOT NULL,
                    CONSTRAINT "PK_CustomDiscoveryRequestUsers" PRIMARY KEY ("TmdbId", "UserId"),
                    CONSTRAINT "FK_CustomDiscoveryRequestUsers_CustomDiscoveryRequests_TmdbId" FOREIGN KEY ("TmdbId") REFERENCES "CustomDiscoveryRequests" ("TmdbId") ON DELETE CASCADE
                """,
                "\"TmdbId\", \"UserId\"");
            migrationBuilder.Sql("CREATE INDEX \"IX_CustomDiscoveryRequestUsers_UserId\" ON \"CustomDiscoveryRequestUsers\" (\"UserId\");");

            migrationBuilder.Sql("PRAGMA foreign_keys = 1;", suppressTransaction: true);
        }

        // Rebuilds a table with a MediaType column in its key: every old row is copied in as a movie.
        private static void Rebuild(MigrationBuilder migrationBuilder, string table, string definition, string oldColumns)
        {
            migrationBuilder.Sql($"CREATE TABLE \"ef_temp_{table}\" (\n{definition}\n);");
            migrationBuilder.Sql($"INSERT INTO \"ef_temp_{table}\" ({oldColumns}, \"MediaType\") SELECT {oldColumns}, 'Movie' FROM \"{table}\";");
            migrationBuilder.Sql($"DROP TABLE \"{table}\";");
            migrationBuilder.Sql($"ALTER TABLE \"ef_temp_{table}\" RENAME TO \"{table}\";");
        }

        // Rebuilds a table without its MediaType column, keeping the movies' rows.
        private static void RebuildDown(MigrationBuilder migrationBuilder, string table, string definition, string columns)
        {
            migrationBuilder.Sql($"CREATE TABLE \"ef_temp_{table}\" (\n{definition}\n);");
            migrationBuilder.Sql($"INSERT INTO \"ef_temp_{table}\" ({columns}) SELECT {columns} FROM \"{table}\" WHERE \"MediaType\" = 'Movie';");
            migrationBuilder.Sql($"DROP TABLE \"{table}\";");
            migrationBuilder.Sql($"ALTER TABLE \"ef_temp_{table}\" RENAME TO \"{table}\";");
        }
    }
}
