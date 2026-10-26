using OfiFlow.Application.Common.Abstractions;

namespace OfiFlow.Application.Tests.Common;

/// <summary>Los operadores de la plataforma en memoria (spec 011). Lo que de verdad importa del servicio real se prueba contra SQL Server en Infrastructure.Tests.</summary>
public sealed class FakePlatformOperators : IPlatformOperators
{
    private readonly HashSet<Guid> _active = [];

    /// <summary>Lo que responde el siguiente alta; por defecto, que la cuenta se añade.</summary>
    public PlatformOperatorCreation NextCreation { get; set; } = PlatformOperatorCreation.Added;

    public List<(Guid UserId, DateTime At)> Added { get; } = [];

    public List<(Guid UserId, DateTime At)> Disabled { get; } = [];

    public void MakeActive(Guid userId) => _active.Add(userId);

    public Task<bool> IsActiveOperatorAsync(Guid userId, CancellationToken cancellationToken) => Task.FromResult(_active.Contains(userId));

    public Task<PlatformOperatorCreation> AddAsync(Guid userId, DateTime utcNow, CancellationToken cancellationToken)
    {
        if (NextCreation == PlatformOperatorCreation.Added)
        {
            Added.Add((userId, utcNow));
            _active.Add(userId);
        }

        return Task.FromResult(NextCreation);
    }

    public Task<bool> DisableAsync(Guid userId, DateTime utcNow, CancellationToken cancellationToken)
    {
        if (!_active.Remove(userId))
        {
            return Task.FromResult(false);
        }

        Disabled.Add((userId, utcNow));
        return Task.FromResult(true);
    }
}

/// <summary>La auditoría de plataforma en memoria: guarda las filas que se «añadirían» (sin confirmar).</summary>
public sealed class FakePlatformAudit : IPlatformAudit
{
    public List<(Guid OperatorUserId, string Action, Guid? TenantId, DateTime At)> Entries { get; } = [];

    public void Add(Guid operatorUserId, string action, Guid? tenantId, DateTime utcNow) => Entries.Add((operatorUserId, action, tenantId, utcNow));
}
