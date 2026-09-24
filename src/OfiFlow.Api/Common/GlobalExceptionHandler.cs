using System.Diagnostics;
using System.Net.Mime;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using OfiFlow.Api.Resources;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Domain.Common;

namespace OfiFlow.Api.Common;

/// <summary>
/// Traduce las excepciones a respuestas ProblemDetails (RFC 9457) con el código HTTP correcto
/// (sección 41 del prompt maestro) y un código de error estable (ADR-011):
/// <list type="bullet">
/// <item>"code": identificador estable del error, contrato público para el cliente.</item>
/// <item>"detail" y "title": texto del diccionario Resources/ErrorMessages.resx, en el idioma de la petición.</item>
/// <item>"errors": solo en validación, un elemento por campo con su propio código y mensaje.</item>
/// <item>"traceId": para localizar la entrada del log.</item>
/// </list>
/// Los 500 se registran con la excepción completa y el cliente solo recibe el mensaje genérico
/// (ADR-009 R5). Un error de negocio nunca se confunde con un bug: solo los tipos propios
/// (DomainException, BusinessRuleException…) dan 4xx; cualquier otra excepción es un 500.
/// </summary>
public sealed partial class GlobalExceptionHandler(
    ILogger<GlobalExceptionHandler> logger,
    IStringLocalizer<ErrorMessages> messages) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var error = Describe(exception);
        var traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        if (error.Status == StatusCodes.Status500InternalServerError)
        {
            LogUnhandledException(logger, exception, httpContext.Request.Method, httpContext.Request.Path, traceId);
        }

        httpContext.Response.StatusCode = error.Status;

        var problem = new ProblemDetails
        {
            Status = error.Status,
            Title = Text(error.TitleKey),
            Detail = Text(error.Code, error.Arguments)
        };
        problem.Extensions["code"] = error.Code;
        if (error.FieldErrors is not null)
        {
            problem.Extensions["errors"] = error.FieldErrors;
        }
        problem.Extensions["traceId"] = traceId;

        await httpContext.Response.WriteAsJsonAsync(problem, options: null, contentType: MediaTypeNames.Application.ProblemJson, cancellationToken);

        return true;
    }

    private ErrorDescription Describe(Exception exception) => exception switch
    {
        ValidationException validation => new(StatusCodes.Status400BadRequest, ProblemTitles.Validation, ApiErrors.ValidationFailed,
            FieldErrors: validation.Errors.Select(ToFieldError).ToList()),
        // Regla de un aggregate (Domain) o entre aggregates (Application).
        DomainException domain => new(StatusCodes.Status400BadRequest, ProblemTitles.BusinessRule, domain.Code, domain.Arguments),
        BusinessRuleException businessRule => new(StatusCodes.Status400BadRequest, ProblemTitles.BusinessRule, businessRule.Code, businessRule.Arguments),
        IdentityOperationException identity => new(StatusCodes.Status400BadRequest, ProblemTitles.Registration,
            identity.Codes.FirstOrDefault() ?? ApiErrors.Unexpected),
        // Mismo código para "no existe" y "es de otra empresa": no se revela que existe (ADR-002).
        NotFoundException notFound => new(StatusCodes.Status404NotFound, ProblemTitles.NotFound, notFound.Code),
        // JSON mal formado o de tipo incorrecto: error del cliente (ASP.NET ya trae el 4xx), no un 500.
        // El detalle del parser no se devuelve: revela tipos internos.
        BadHttpRequestException badRequest => new(badRequest.StatusCode, ProblemTitles.BadRequest, ApiErrors.InvalidRequestBody),
        _ => new(StatusCodes.Status500InternalServerError, ProblemTitles.ServerError, ApiErrors.Unexpected)
    };

    /// <summary>
    /// Las reglas propias (ValidEmail, ValidPhone) llevan un código de OfiFlow con entrada en el
    /// diccionario. Las estándar de FluentValidation (NotEmpty, MaximumLength…) llevan su propio
    /// código y un mensaje que FluentValidation ya traduce al idioma de la petición.
    /// </summary>
    private FieldError ToFieldError(ValidationFailure failure)
    {
        var fromDictionary = messages[failure.ErrorCode];
        var message = fromDictionary.ResourceNotFound ? failure.ErrorMessage : fromDictionary.Value;

        return new FieldError(failure.PropertyName, failure.ErrorCode, message);
    }

    /// <summary>
    /// Texto del diccionario. Si faltara la entrada se devuelve el propio código, nunca una
    /// excepción; el test de diccionario completo impide que eso llegue a producción.
    /// </summary>
    private string Text(string key, IReadOnlyList<object?>? arguments = null)
    {
        var text = arguments is { Count: > 0 } ? messages[key, arguments.ToArray()!] : messages[key];
        return text.ResourceNotFound ? key : text.Value;
    }

    // Solo método y ruta (sin query string ni cuerpo): la ruta lleva Ids, nunca datos personales.
    [LoggerMessage(EventId = SecurityEventIds.UnhandledException, Level = LogLevel.Error,
        Message = "Excepción no controlada en {Method} {Path}. TraceId {TraceId}")]
    private static partial void LogUnhandledException(ILogger logger, Exception exception, string method, string path, string traceId);

    private sealed record ErrorDescription(
        int Status,
        string TitleKey,
        string Code,
        IReadOnlyList<object?>? Arguments = null,
        IReadOnlyList<FieldError>? FieldErrors = null);

    /// <summary>Un error de validación de un campo. Se serializa como { field, code, message }.</summary>
    public sealed record FieldError(string Field, string Code, string Message);
}
