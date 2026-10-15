namespace OfiFlow.Application.Tenancy;

/// <summary>
/// Lo que recibe quien crea una invitación. <paramref name="Token"/> es la <b>única</b> vez que existe el
/// valor en claro: no se guarda ni se puede volver a consultar. Hasta que haya envío por email, quien invita
/// se lo entrega a la persona por su canal (ADR-013, D1).
/// </summary>
public sealed record InvitationCreatedDto(Guid Id, string Token, DateTime ExpiresAt);

/// <summary>Una invitación pendiente. Nunca incluye el token ni su hash.</summary>
public sealed record InvitationDto(Guid Id, string Email, string Role, DateTime ExpiresAt, DateTime CreatedAt);
