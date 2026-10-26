using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OfiFlow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantRowVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "Tenants",
                type: "rowversion",
                rowVersion: true,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "Tenants");
        }
    }
}
