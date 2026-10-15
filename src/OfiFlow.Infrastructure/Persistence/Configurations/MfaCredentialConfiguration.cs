using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OfiFlow.Infrastructure.Identity;

namespace OfiFlow.Infrastructure.Persistence.Configurations;

public sealed class MfaCredentialConfiguration : IEntityTypeConfiguration<MfaCredential>
{
    public void Configure(EntityTypeBuilder<MfaCredential> builder)
    {
        // La clave es la propia cuenta: como mucho un secreto por cuenta, garantizado por la base de datos.
        builder.HasKey(c => c.UserId);

        builder.Property(c => c.UserId).ValueGeneratedNever();
        builder.Property(c => c.SecretCipher).HasMaxLength(MfaCredential.SecretCipherMaxLength).IsRequired();

        // Control de concurrencia optimista: dos peticiones simultáneas con el mismo código TOTP leen el mismo
        // último paso; la segunda en guardar encuentra que ya cambió y falla, en vez de aceptar el código dos
        // veces (spec 010 R4). Un "if" en el código no lo garantiza: comprobar y escribir serían dos pasos.
        builder.Property<byte[]>(InvitationConfiguration.RowVersion).IsRowVersion();
    }
}
