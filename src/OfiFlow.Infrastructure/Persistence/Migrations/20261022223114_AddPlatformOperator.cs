using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OfiFlow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlatformOperator : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // EDITADA A MANO (spec 011, bloque 3): EF generó defaultValue: "", que no es un valor válido del enumerado y haría fallar
            // la lectura de cualquier desafío vivo al migrar. Todas las filas existentes son desafíos de empresa: la plataforma
            // no existía. El modelo no lleva valor por defecto a propósito: quien crea un desafío tiene que decir para quién es.
            migrationBuilder.AddColumn<string>(
                name: "Audience",
                table: "MfaChallenges",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Tenant");

            migrationBuilder.CreateTable(
                name: "PlatformAuditEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OperatorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OccurredAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformAuditEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlatformOperators",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DisabledAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformOperators", x => x.UserId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlatformAuditEntries_OperatorUserId_OccurredAt",
                table: "PlatformAuditEntries",
                columns: new[] { "OperatorUserId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PlatformAuditEntries_TenantId",
                table: "PlatformAuditEntries",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlatformAuditEntries");

            migrationBuilder.DropTable(
                name: "PlatformOperators");

            migrationBuilder.DropColumn(
                name: "Audience",
                table: "MfaChallenges");
        }
    }
}
