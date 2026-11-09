using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OfiFlow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddQuotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Quotes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JobId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Number = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ValidUntil = table.Column<DateOnly>(type: "date", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    NetCents = table.Column<long>(type: "bigint", nullable: false),
                    VatCents = table.Column<long>(type: "bigint", nullable: false),
                    TotalCents = table.Column<long>(type: "bigint", nullable: false),
                    SentAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DecidedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Revision = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Quotes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "QuoteItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuoteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(9,3)", precision: 9, scale: 3, nullable: false),
                    UnitPriceCents = table.Column<long>(type: "bigint", nullable: false),
                    VatPercent = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuoteItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QuoteItems_Quotes_QuoteId",
                        column: x => x.QuoteId,
                        principalTable: "Quotes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_QuoteItems_QuoteId_Position",
                table: "QuoteItems",
                columns: new[] { "QuoteId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_TenantId_CustomerId",
                table: "Quotes",
                columns: new[] { "TenantId", "CustomerId" });

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_TenantId_Number",
                table: "Quotes",
                columns: new[] { "TenantId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_TenantId_Status_CreatedAt",
                table: "Quotes",
                columns: new[] { "TenantId", "Status", "CreatedAt" },
                descending: new[] { false, false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "QuoteItems");

            migrationBuilder.DropTable(
                name: "Quotes");
        }
    }
}
