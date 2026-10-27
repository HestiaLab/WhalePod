using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhalePod.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddServiceAccountDailyTokenBudget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "daily_token_budget",
                table: "service_account",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_service_account_daily_token_budget",
                table: "service_account",
                sql: "daily_token_budget IS NULL OR daily_token_budget BETWEEN 1 AND 1000000000");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_service_account_daily_token_budget",
                table: "service_account");

            migrationBuilder.DropColumn(
                name: "daily_token_budget",
                table: "service_account");
        }
    }
}