using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace API.Migrations.ExtraMetadata
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MangaExtraMetadata",
                columns: table => new
                {
                    MangaId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Data = table.Column<string>(type: "jsonb", nullable: false),
                    FetchedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MangaExtraMetadata", x => x.MangaId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MangaExtraMetadata");
        }
    }
}
