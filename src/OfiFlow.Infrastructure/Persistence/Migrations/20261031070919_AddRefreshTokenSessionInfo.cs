using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OfiFlow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRefreshTokenSessionInfo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Spec 016. El mismo patrón de la spec 015, para que ninguna fila quede con un valor falso: EF crea las columnas de fecha con 0001-01-01 por
            // defecto (todas las sesiones de hoy habrían «empezado» en el año 1). Por eso:
            // 1) las dos fechas nacen ANULABLES y sin valor por defecto;
            // 2) las filas existentes se rellenan: CreatedAt = ExpiresAt menos 7 días (la duración del refresh token por defecto, JwtOptions.RefreshTokenDays) y
            //    SessionStartedAt = CreatedAt (de una sesión anterior solo se sabe cuándo se creó su fila; el navegador y la IP quedan en nulo y se
            //    muestran como «dispositivo desconocido»);
            // 3) solo entonces las dos fechas se vuelven NOT NULL, sin valor por defecto.
            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "RefreshTokens",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SessionStartedAt",
                table: "RefreshTokens",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IpAddress",
                table: "RefreshTokens",
                type: "nvarchar(45)",
                maxLength: 45,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UserAgent",
                table: "RefreshTokens",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.Sql("UPDATE [RefreshTokens] SET [CreatedAt] = DATEADD(DAY, -7, [ExpiresAt]), [SessionStartedAt] = DATEADD(DAY, -7, [ExpiresAt])");

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedAt",
                table: "RefreshTokens",
                type: "datetime2",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "datetime2",
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "SessionStartedAt",
                table: "RefreshTokens",
                type: "datetime2",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "datetime2",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "RefreshTokens");

            migrationBuilder.DropColumn(
                name: "IpAddress",
                table: "RefreshTokens");

            migrationBuilder.DropColumn(
                name: "SessionStartedAt",
                table: "RefreshTokens");

            migrationBuilder.DropColumn(
                name: "UserAgent",
                table: "RefreshTokens");
        }
    }
}
