using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OfiFlow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddJobScheduledDateIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Jobs_TenantId_ScheduledDate",
                table: "Jobs",
                columns: new[] { "TenantId", "ScheduledDate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Jobs_TenantId_ScheduledDate",
                table: "Jobs");
        }
    }
}
