using OfiFlow.Application.Common.Abstractions;

namespace OfiFlow.Application.Tests.Common;

/// <summary>
/// Los registros pendientes de mentira (spec 021): devuelve el resultado que se le diga y apunta con qué se le pidió. Así se comprueba qué hace el caso de uso con cada resultado sin base de datos
/// (la lógica real de crear, anular y limitar se prueba contra SQL Server en Infrastructure).
/// </summary>
public sealed class FakePendingRegistrationService : IPendingRegistrationService
{
    public PendingRegistrationIssue Result { get; set; } = new(PendingRegistrationIssueOutcome.Throttled);

    /// <summary>Lo que se pidió, en orden: email, empresa, persona y versión de las condiciones.</summary>
    public List<(string Email, string CompanyName, string UserName, string TermsVersion)> Requested { get; } = [];

    public PendingRegistrationConsumption ConsumeResult { get; set; } = new(PendingRegistrationConsumeOutcome.Rejected, RejectedReason: "Unknown");

    /// <summary>Los tokens (en claro) que se intentó canjear, en orden.</summary>
    public List<string> Consumed { get; } = [];

    public Task<PendingRegistrationIssue> IssueAsync(string email, string companyName, string userName, string termsVersion, CancellationToken cancellationToken)
    {
        Requested.Add((email, companyName, userName, termsVersion));
        return Task.FromResult(Result);
    }

    public Task<PendingRegistrationConsumption> ConsumeAsync(string token, CancellationToken cancellationToken)
    {
        Consumed.Add(token);
        return Task.FromResult(ConsumeResult);
    }
}

/// <summary>Las condiciones de uso de mentira: la versión vigente que se diga, y nada más (la comparación real se prueba en Infrastructure).</summary>
public sealed class FakeLegalTerms(string? currentVersion = "2026-11") : ILegalTerms
{
    public string? CurrentVersion { get; set; } = currentVersion;

    public bool IsCurrent(string? version) => CurrentVersion is { } current && string.Equals(version, current, StringComparison.Ordinal);
}

/// <summary>El limitador de avisos de mentira: concede o niega y apunta a quién se le pidió cupo.</summary>
public sealed class FakeRegistrationNoticeThrottle : IRegistrationNoticeThrottle
{
    public bool Allows { get; set; } = true;

    public List<string> Asked { get; } = [];

    public bool TryAcquire(string email)
    {
        Asked.Add(email);
        return Allows;
    }
}
