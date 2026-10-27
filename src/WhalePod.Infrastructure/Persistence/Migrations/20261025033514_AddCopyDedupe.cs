using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhalePod.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCopyDedupe : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "copy_dedupe",
                columns: table => new
                {
                    key_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_copy_dedupe", x => x.key_hash);
                    table.CheckConstraint("ck_copy_dedupe_hash_length", "octet_length(key_hash) = 32");
                });

            migrationBuilder.CreateIndex(
                name: "ix_copy_dedupe_expiry",
                table: "copy_dedupe",
                column: "expires_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "copy_dedupe");
        }
    }
}