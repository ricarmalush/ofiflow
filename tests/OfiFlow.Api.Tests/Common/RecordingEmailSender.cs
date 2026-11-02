using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Mail;

namespace OfiFlow.Api.Tests.Common;

/// <summary>
/// El transporte de correo de los tests de la API: guarda lo que se «envía» y puede fallar a demanda (spec 017, D11). El transporte SMTP de verdad se prueba en
/// Infrastructure contra un servidor SMTP; aquí lo que importa es el camino HTTP completo alrededor de él.
/// </summary>
public sealed class RecordingEmailSender : IEmailSender
{
    private readonly List<EmailMessage> _sent = [];

    public bool IsEnabled => true;

    /// <summary>Si tiene valor, el siguiente envío falla con esta categoría.</summary>
    public EmailFailureCategory? FailWith { get; set; }

    public IReadOnlyList<EmailMessage> Sent
    {
        get
        {
            lock (_sent)
            {
                return [.. _sent];
            }
        }
    }

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        if (FailWith is { } category)
        {
            throw new EmailDeliveryException(category);
        }

        lock (_sent)
        {
            _sent.Add(message);
        }

        return Task.CompletedTask;
    }
}
