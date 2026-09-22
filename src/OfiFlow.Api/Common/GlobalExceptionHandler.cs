using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using OfiFlow.Application.Common.Exceptions;

namespace OfiFlow.Api.Common;

/// <summary>
/// Traduce las excepciones de negocio de Application a respuestas ProblemDetails con el
/// código HTTP correcto (sección 41 del prompt maestro) — sin esto, cualquier
/// NotFoundException/ValidationException llegaría al cliente como un 500 genérico.
/// </summary>
public sealed class GlobalExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (statusCode, title, detail) = exception switch
        {
            NotFoundException notFound => (StatusCodes.Status404NotFound, "Recurso no encontrado", notFound.Message),
            ValidationException validation => (StatusCodes.Status400BadRequest, "Error de validación",
                string.Join("; ", validation.Errors.Select(e => e.ErrorMessage))),
            IdentityOperationException identity => (StatusCodes.Status400BadRequest, "Error de registro", identity.Message),
            _ => (StatusCodes.Status500InternalServerError, "Error interno", "Ha ocurrido un error inesperado.")
        };

        httpContext.Response.StatusCode = statusCode;

        await httpContext.Response.WriteAsJsonAsync(
            new ProblemDetails { Status = statusCode, Title = title, Detail = detail },
            cancellationToken);

        return true;
    }
}
