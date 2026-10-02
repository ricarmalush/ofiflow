namespace OfiFlow.Api.Common;

/// <summary>
/// Códigos de error propios de la API, que no pertenecen a ningún bounded context (ADR-011).
/// Son contrato público, igual que los de Domain. Su texto está en Resources/ErrorMessages.resx.
/// </summary>
public static class ApiErrors
{
    /// <summary>La petición no supera la validación; el detalle por campo va en "errors".</summary>
    public const string ValidationFailed = "validation.failed";

    public const string InvalidRequestBody = "request.invalid_body";

    public const string RateLimitExceeded = "rate_limit.exceeded";

    public const string Unexpected = "server.unexpected";
}

/// <summary>
/// Claves de los títulos de ProblemDetails en Resources/ErrorMessages.resx. No son códigos de
/// error (no se devuelven en "code"): solo el texto corto que acompaña al estado HTTP.
/// </summary>
public static class ProblemTitles
{
    public const string Validation = "problem.title.validation";
    public const string BusinessRule = "problem.title.business_rule";
    public const string Registration = "problem.title.registration";
    public const string NotFound = "problem.title.not_found";
    public const string BadRequest = "problem.title.bad_request";
    public const string TooManyRequests = "problem.title.too_many_requests";
    public const string ServerError = "problem.title.server_error";
}
