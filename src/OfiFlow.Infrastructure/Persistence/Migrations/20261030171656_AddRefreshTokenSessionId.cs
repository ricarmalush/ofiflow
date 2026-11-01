using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OfiFlow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRefreshTokenSessionId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Spec 015 D5. Tres pasos, en este orden, para que ninguna fila quede con un valor falso:
            // 1) la columna nace ANULABLE y sin valor por defecto (con un Guid vacío por defecto, todas las filas de hoy serían UNA sola
            //    sesión y cerrar una cerraría todas);
            // 2) cada fila existente pasa a ser su propia sesión (SessionId = Id): las revocadas ya no cuentan y la activa de cada
            //    dispositivo sigue pudiendo renovar, y al renovar el token nuevo hereda ese SessionId;
            // 3) solo entonces la columna se vuelve NOT NULL, sin valor por defecto: si algún día el código olvidara informarla, fallaría
            //    en vez de crear sesiones compartidas.
            migrationBuilder.AddColumn<Guid>(
                name: "SessionId",
                table: "RefreshTokens",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.Sql("UPDATE [RefreshTokens] SET [SessionId] = [Id]");

            migrationBuilder.AlterColumn<Guid>(
                name: "SessionId",
                table: "RefreshTokens",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_SessionId",
                table: "RefreshTokens",
                column: "SessionId")
                .Annotation("SqlServer:Include", new[] { "UserId", "RevokedAt", "ExpiresAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RefreshTokens_SessionId",
                table: "RefreshTokens");

            migrationBuilder.DropColumn(
                name: "SessionId",
                table: "RefreshTokens");
        }
    }
}
