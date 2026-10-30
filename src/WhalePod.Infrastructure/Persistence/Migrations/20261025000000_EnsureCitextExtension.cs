using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhalePod.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Runs before InitialCreate (migration IDs are ordered) so fresh databases can use citext columns.
    /// Extension creation may require a privileged role; production runbooks pre-create it when needed.
    /// </summary>
    [DbContext(typeof(WhalePodDbContext))]
    [Migration("20261025000000_EnsureCitextExtension")]
    public partial class EnsureCitextExtension : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS citext;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Existing citext columns depend on the extension; never drop it on downgrade.
        }
    }
}