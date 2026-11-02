using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Mail;

namespace OfiFlow.Application.Tests.Common;

/// <summary>
/// El puerto de correo de mentira: guarda lo que se «envía» para poder inspeccionarlo, y puede fallar a demanda con la categoría que se quiera
/// (spec 017, D11). El transporte de verdad se prueba en Infrastructure contra un servidor SMTP.
/// </summary>
public sealed class FakeEmailSender(bool isEnabled = false) : IEmailSender
{
    public bool IsEnabled { get; set; } = isEnabled;

    public List<EmailMessage> Sent { get; } = [];

    /// <summary>Si tiene valor, el siguiente envío falla con esta categoría (y no se guarda nada).</summary>
    public EmailFailureCategory? FailWith { get; set; }

    /// <summary>Algo que ocurre justo antes de entregar el mensaje, para comprobar el ORDEN (por ejemplo, que aún no hay nada guardado).</summary>
    public Func<Task>? OnSend { get; set; }

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        if (!IsEnabled)
        {
            throw new InvalidOperationException("Se envió correo con el correo apagado: el caso de uso debía comprobar IsEnabled antes.");
        }

        if (OnSend is not null)
        {
            await OnSend();
        }

        if (FailWith is { } category)
        {
            throw new EmailDeliveryException(category);
        }

        Sent.Add(message);
    }
}

/// <summary>Los enlaces de los correos, con una base fija y reconocible: lo que importa es que el caso de uso use SIEMPRE esta clase y no construya enlaces por su cuenta.</summary>
public sealed class FakeEmailLinks : IEmailLinks
{
    public const string BaseUrl = "https://app.fake.example";

    public string AcceptInvitation(string token) => $"{BaseUrl}/auth/accept-invitation#token={token}";
}
