using System.Diagnostics;
using System.Net.Mime;
using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Common.Logging;

namespace OfiFlow.Api.Common;

/// <summary>
/// Traduce las excepciones de negocio de Application a respuestas ProblemDetails con el
/// código HTTP correcto (sección 41 del prompt maestro) — sin esto, cualquier
/// NotFoundException/ValidationException llegaría al cliente como un 500 genérico.
/// Los 500 se registran con la excepción completa; el cliente solo recibe un mensaje genérico
/// y el traceId para poder localizar la entrada en el log (ADR-009 R5).
/// </summary>
public sealed partial class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (statusCode, title, detail) = exception switch
        {
            NotFoundException notFound => (StatusCodes.Status404NotFound, "Recurso no encontrado", notFound.Message),
            ValidationException validation => (StatusCodes.Status400BadRequest, "Error de validación",
                string.Join("; ", validation.Errors.Select(e => e.ErrorMessage))),
            IdentityOperationException identity => (StatusCodes.Status400BadRequest, "Error de registro", identity.Message),
            BusinessRuleException businessRule => (StatusCodes.Status400BadRequest, "Regla de negocio violada", businessRule.Message),
            // JSON mal formado o de tipo incorrecto: es un error del cliente (ASP.NET ya trae el 4xx),
            // no un 500. El detalle del parser no se devuelve: revela tipos internos.
            BadHttpRequestException badRequest => (badRequest.StatusCode, "Petición no válida", "El cuerpo de la petición no tiene un formato válido."),
            _ => (StatusCodes.Status500InternalServerError, "Error interno", "Ha ocurrido un error inesperado.")
        };

        var traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        if (statusCode == StatusCodes.Status500InternalServerError)
        {
            LogUnhandledException(logger, exception, httpContext.Request.Method, httpContext.Request.Path, traceId);
        }

        httpContext.Response.StatusCode = statusCode;

        var problem = new ProblemDetails { Status = statusCode, Title = title, Detail = detail };
        problem.Extensions["traceId"] = traceId;

        await httpContext.Response.WriteAsJsonAsync(problem, options: null, contentType: MediaTypeNames.Application.ProblemJson, cancellationToken);

        return true;
    }

    // Solo método y ruta (sin query string ni cuerpo): la ruta lleva Ids, nunca datos personales.
    [LoggerMessage(EventId = SecurityEventIds.UnhandledException, Level = LogLevel.Error,
        Message = "Excepción no controlada en {Method} {Path}. TraceId {TraceId}")]
    private static partial void LogUnhandledException(ILogger logger, Exception exception, string method, string path, string traceId);
}
