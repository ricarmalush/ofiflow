using OfiFlow.Application.Common.Abstractions;

namespace OfiFlow.Application.Tests.Common;

/// <summary>
/// Un segundo factor de mentira para los tests de los handlers: guarda el estado en memoria y acepta como válido el
/// código que el test decida. Lo que de verdad importa del servicio real (cifrado, anotación atómica del paso, consumo
/// del código de recuperación, bloqueo) se prueba contra SQL Server en Infrastructure.Tests; aquí solo se comprueba
/// que los handlers hacen las cosas en el orden correcto y con los datos correctos.
/// </summary>
public sealed class FakeMfaService : IMfaService
{
    private readonly Dictionary<Guid, MfaStatus> _status = [];

    /// <summary>El código TOTP que este doble da por bueno.</summary>
    public string ValidCode { get; set; } = "123456";

    /// <summary>Códigos de recuperación (tal cual los escribe la persona) que este doble da por buenos.</summary>
    public HashSet<string> ValidRecoveryCodes { get; } = [];

    public string AccountEmail { get; set; } = "ana@example.com";

    public List<Guid> SetupStarted { get; } = [];

    public byte[]? LastSecret { get; private set; }

    public IReadOnlyCollection<string>? ConfirmedRecoveryHashes { get; private set; }

    public List<(Guid UserId, IReadOnlyCollection<string> Hashes)> ReplacedRecoveryCodes { get; } = [];

    public List<Guid> RemovedAll { get; } = [];

    public int SecondFactorChecks { get; private set; }

    public void Set(Guid userId, MfaStatus status) => _status[userId] = status;

    public Task<MfaStatus> GetStatusAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult(_status.GetValueOrDefault(userId, new MfaStatus(false, false, 0)));

    public Task<string> BeginSetupAsync(Guid userId, byte[] secret, CancellationToken cancellationToken)
    {
        SetupStarted.Add(userId);
        LastSecret = secret;
        _status[userId] = new MfaStatus(IsEnabled: false, SetupPending: true, RecoveryCodesRemaining: 0);

        return Task.FromResult(AccountEmail);
    }

    public Task<SetupOutcome> ConfirmSetupAsync(
        Guid userId, string code, IReadOnlyCollection<string> recoveryCodeHashes, CancellationToken cancellationToken)
    {
        if (!_status.GetValueOrDefault(userId, new MfaStatus(false, false, 0)).SetupPending)
        {
            return Task.FromResult(SetupOutcome.NoPendingSetup);
        }

        if (code != ValidCode)
        {
            return Task.FromResult(SetupOutcome.InvalidCode);
        }

        ConfirmedRecoveryHashes = recoveryCodeHashes;
        _status[userId] = new MfaStatus(IsEnabled: true, SetupPending: false, recoveryCodeHashes.Count);

        return Task.FromResult(SetupOutcome.Confirmed);
    }

    public Task<SecondFactorResult> VerifySecondFactorAsync(Guid userId, string code, CancellationToken cancellationToken)
    {
        SecondFactorChecks++;

        if (code == ValidCode)
        {
            return Task.FromResult(new SecondFactorResult(Valid: true, UsedRecoveryCode: false, RecoveryCodesRemaining: 10));
        }

        return Task.FromResult(ValidRecoveryCodes.Contains(code)
            ? new SecondFactorResult(Valid: true, UsedRecoveryCode: true, RecoveryCodesRemaining: 9)
            : SecondFactorResult.Rejected);
    }

    public Task ReplaceRecoveryCodesAsync(Guid userId, IReadOnlyCollection<string> recoveryCodeHashes, CancellationToken cancellationToken)
    {
        ReplacedRecoveryCodes.Add((userId, recoveryCodeHashes));
        return Task.CompletedTask;
    }

    public Task RemoveAllAsync(Guid userId, CancellationToken cancellationToken)
    {
        RemovedAll.Add(userId);
        _status.Remove(userId);

        return Task.CompletedTask;
    }
}
