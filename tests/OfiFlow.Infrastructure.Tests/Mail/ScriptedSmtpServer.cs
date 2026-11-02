using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace OfiFlow.Infrastructure.Tests.Mail;

/// <summary>
/// Un servidor SMTP de mentira, dentro del propio test, que se comporta como el test quiere: callar, cortar, rechazar a un destinatario... Sirve para provocar
/// los fallos que un servidor real no sabe provocar a demanda, sin Docker y sin depender del tiempo (spec 017, D11). Escucha solo en bucle local.
/// </summary>
public sealed class ScriptedSmtpServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _acceptLoop;

    /// <summary>Qué líneas de cliente ha visto (para comprobar, por ejemplo, que no llegó a mandar el mensaje).</summary>
    public List<string> ClientLines { get; } = [];

    private ScriptedSmtpServer(Func<StreamReader, StreamWriter, CancellationToken, Task> script, X509Certificate2? tlsCertificate = null)
    {
        _listener.Start();
        _acceptLoop = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        try
                        {
                            await using var network = client.GetStream();
                            Stream stream = network;
                            if (tlsCertificate is not null)
                            {
                                // TLS desde el primer byte: el servidor presenta su certificado antes de decir nada.
                                var tls = new SslStream(network);
                                await tls.AuthenticateAsServerAsync(tlsCertificate);
                                stream = tls;
                            }

                            using var reader = new StreamReader(stream, Encoding.ASCII);
                            await using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
                            await script(reader, writer, _stop.Token);
                        }
                        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException or SocketException)
                        {
                            // El cliente se fue o el test terminó: es lo normal al final de cada escenario.
                        }
                    }
                });
            }
        });
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Acepta la conexión y no dice nada, nunca: el cliente se queda esperando el saludo.</summary>
    public static ScriptedSmtpServer Silent() => new(async (_, _, token) => await Task.Delay(Timeout.Infinite, token));

    /// <summary>Acepta la conexión y la cierra sin decir nada.</summary>
    public static ScriptedSmtpServer DropsTheConnection() => new((_, _, _) => Task.CompletedTask);

    /// <summary>Saluda diciendo que no atiende (554), como un servidor que rechaza al cliente.</summary>
    public static ScriptedSmtpServer RefusesTheGreeting() => new(async (_, writer, _) => await writer.WriteLineAsync("554 5.3.2 Servicio no disponible"));

    /// <summary>Un servidor correcto que NO ofrece STARTTLS: el cliente que lo exija no puede continuar y no debe enviar en claro.</summary>
    public static ScriptedSmtpServer WithoutStartTls() => Create(async (reader, writer, server, _) =>
    {
        await writer.WriteLineAsync("220 prueba ESMTP");
        while (await reader.ReadLineAsync() is { } line)
        {
            server.ClientLines.Add(line);
            if (line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase))
            {
                await writer.WriteLineAsync("250-prueba");
                await writer.WriteLineAsync("250 8BITMIME");
            }
            else
            {
                await writer.WriteLineAsync(line.StartsWith("QUIT", StringComparison.OrdinalIgnoreCase) ? "221 adiós" : "250 OK");
            }
        }
    });

    /// <summary>Acepta al remitente y rechaza al destinatario con un texto que repite su dirección, como hacen los servidores reales.</summary>
    public static ScriptedSmtpServer RejectsTheRecipient() => Create(async (reader, writer, server, _) =>
    {
        await writer.WriteLineAsync("220 prueba ESMTP");
        while (await reader.ReadLineAsync() is { } line)
        {
            server.ClientLines.Add(line);
            if (line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase))
            {
                await writer.WriteLineAsync("250-prueba");
                await writer.WriteLineAsync("250 8BITMIME");
            }
            else if (line.StartsWith("RCPT", StringComparison.OrdinalIgnoreCase))
            {
                await writer.WriteLineAsync($"550 5.1.1 {line[(line.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim()}: Recipient address rejected: User unknown");
            }
            else if (line.StartsWith("QUIT", StringComparison.OrdinalIgnoreCase))
            {
                await writer.WriteLineAsync("221 adiós");
            }
            else
            {
                await writer.WriteLineAsync("250 OK");
            }
        }
    });

    /// <summary>Un servidor correcto que habla TLS desde el primer byte (como el puerto 465) con el certificado dado, y acepta el mensaje entero. Lo recibido queda en <see cref="ClientLines"/>.</summary>
    public static ScriptedSmtpServer AcceptingOverTls(X509Certificate2 certificate) => Create(async (reader, writer, server, _) =>
    {
        await writer.WriteLineAsync("220 prueba ESMTP");
        var inData = false;
        while (await reader.ReadLineAsync() is { } line)
        {
            server.ClientLines.Add(line);
            if (inData)
            {
                if (line == ".")
                {
                    inData = false;
                    await writer.WriteLineAsync("250 OK: queued");
                }

                continue;
            }

            if (line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase))
            {
                await writer.WriteLineAsync("250-prueba");
                await writer.WriteLineAsync("250 8BITMIME");
            }
            else if (line.StartsWith("DATA", StringComparison.OrdinalIgnoreCase))
            {
                inData = true;
                await writer.WriteLineAsync("354 adelante");
            }
            else if (line.StartsWith("QUIT", StringComparison.OrdinalIgnoreCase))
            {
                await writer.WriteLineAsync("221 adiós");
            }
            else
            {
                await writer.WriteLineAsync("250 OK");
            }
        }
    }, certificate);

    private static ScriptedSmtpServer Create(Func<StreamReader, StreamWriter, ScriptedSmtpServer, CancellationToken, Task> script, X509Certificate2? tlsCertificate = null)
    {
        ScriptedSmtpServer? server = null;
        server = new ScriptedSmtpServer((reader, writer, token) => script(reader, writer, server!, token), tlsCertificate);
        return server;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        try
        {
            _acceptLoop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Se cancela a propósito.
        }

        _stop.Dispose();
    }
}
