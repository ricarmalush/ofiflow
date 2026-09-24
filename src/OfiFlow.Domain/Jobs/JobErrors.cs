namespace OfiFlow.Domain.Jobs;

/// <summary>
/// Códigos de error del contexto Jobs (ADR-011). Son contrato público: no se cambian ni se
/// reutilizan con otro significado. El texto de cada uno está en Api/Resources/ErrorMessages.resx.
/// </summary>
public static class JobErrors
{
    public const string TitleRequired = "job.title_required";

    /// <summary>Argumento 0: estado actual del trabajo (<see cref="JobStatus"/>).</summary>
    public const string CannotStart = "job.cannot_start";

    /// <summary>Argumento 0: estado actual del trabajo (<see cref="JobStatus"/>).</summary>
    public const string CannotComplete = "job.cannot_complete";

    public const string CannotCancelCompleted = "job.cannot_cancel_completed";

    public const string NotFound = "job.not_found";
}
