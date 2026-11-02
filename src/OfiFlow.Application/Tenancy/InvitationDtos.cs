namespace OfiFlow.Application.Tenancy;

/// <summary>
/// Lo que recibe quien crea una invitación (spec 009 y 017). Hay <b>dos</b> formas, según haya o no correo configurado:
/// <list type="bullet">
/// <item><b>Con correo</b> (<paramref name="EmailSent"/> es verdadero): el enlace ya se envió a la persona invitada y <paramref name="Token"/> es <c>null</c>. Quien invita no conoce el token, así que
/// no puede aceptar la invitación en nombre de otro (cierra el riesgo aceptado de ADR-013).</item>
/// <item><b>Sin correo</b>: <paramref name="Token"/> es la <b>única</b> vez que existe el valor en claro (no se guarda ni se puede consultar) y quien invita se lo entrega a la persona por su canal.</item>
/// </list>
/// </summary>
public sealed record InvitationCreatedDto(Guid Id, string? Token, DateTime ExpiresAt, bool EmailSent);

/// <summary>Una invitación pendiente. Nunca incluye el token ni su hash.</summary>
public sealed record InvitationDto(Guid Id, string Email, string Role, DateTime ExpiresAt, DateTime CreatedAt);
