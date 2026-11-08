using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Mail;

namespace OfiFlow.Application.Tests.Common;

/// <summary>La cola de correo de mentira: guarda lo que se encola y puede decir «llena» a demanda (spec 018, R15).</summary>
public sealed class FakeEmailQueue : IEmailQueue
{
    public List<EmailMessage> Queued { get; } = [];

    /// <summary>Lo que devuelve <see cref="TryEnqueue"/>. Con <c>false</c> simula una cola llena: el mensaje no se guarda.</summary>
    public bool Accepts { get; set; } = true;

    public bool TryEnqueue(EmailMessage message)
    {
        if (!Accepts)
        {
            return false;
        }

        Queued.Add(message);
        return true;
    }
}

/// <summary>
/// Los tokens de restablecimiento de mentira: devuelve el resultado que se le diga y apunta con qué email se le pidió. Así se comprueba qué hace el caso de uso con cada resultado
/// sin base de datos (la lógica real de crear, anular y limitar se prueba contra SQL Server en Infrastructure).
/// </summary>
public sealed class FakePasswordResetService : IPasswordResetService
{
    public PasswordResetIssue Result { get; set; } = new(PasswordResetIssueOutcome.UnknownAccount);

    public List<string> Requested { get; } = [];

    /// <summary>Lo que devuelve «canjear». Por defecto, un token que sirve para la cuenta indicada.</summary>
    public PasswordResetConsumption ConsumeResult { get; set; } = new(PasswordResetConsumeOutcome.Consumed, Guid.NewGuid());

    /// <summary>Los tokens (en claro) que se intentó canjear, en orden.</summary>
    public List<string> Consumed { get; } = [];

    public Task<PasswordResetConsumption> ConsumeAsync(string token, CancellationToken cancellationToken)
    {
        Consumed.Add(token);
        return Task.FromResult(ConsumeResult);
    }

    /// <summary>Las cuentas cuyos enlaces pendientes se anularon SIN confirmar (spec 019), en orden.</summary>
    public List<Guid> UsersWithPendingInvalidated { get; } = [];

    public Task InvalidatePendingWithoutSavingAsync(Guid userId, CancellationToken cancellationToken)
    {
        UsersWithPendingInvalidated.Add(userId);
        return Task.CompletedTask;
    }

    public Task<PasswordResetIssue> IssueAsync(string email, CancellationToken cancellationToken)
    {
        Requested.Add(email);
        return Task.FromResult(Result);
    }
}
