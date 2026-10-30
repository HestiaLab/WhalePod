using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhalePod.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionVersionAndCategoryKernelPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "session_version",
                table: "user",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<bool>(
                name: "requires_kernel",
                table: "category",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql("UPDATE category SET requires_kernel = true WHERE name = '抄作业';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "session_version",
                table: "user");

            migrationBuilder.DropColumn(
                name: "requires_kernel",
                table: "category");
        }
    }
}