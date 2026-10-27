namespace OfiFlow.Application.Platform;

/// <summary>
/// Lo que un operador ve de una empresa (spec 011 R11, D5): <b>exactamente</b> estos cinco datos y nada más. Ni emails ni nombres de
/// personas, ni clientes, ni trabajos, ni recuentos de ellos. El nombre de la empresa es un dato de negocio; el número de miembros,
/// un recuento. Un test fija que este registro no gana ninguna propiedad sin que alguien lo decida a propósito.
/// </summary>
/// <param name="SuspendedAt">Cuándo se suspendió, en UTC; nulo si la empresa está activa.</param>
public sealed record PlatformTenantDto(Guid Id, string Name, DateTime CreatedAt, int MemberCount, DateTime? SuspendedAt);
