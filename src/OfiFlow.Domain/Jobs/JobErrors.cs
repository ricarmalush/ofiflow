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

    /// <summary>Argumento 0: estado actual del trabajo (<see cref="JobStatus"/>). Solo se programa un trabajo nuevo o ya programado (spec 020 R2).</summary>
    public const string CannotSchedule = "job.cannot_schedule";

    /// <summary>Argumento 0: estado actual del trabajo (<see cref="JobStatus"/>). Solo se quita de la agenda un trabajo programado (spec 020 R7).</summary>
    public const string CannotUnschedule = "job.cannot_unschedule";

    /// <summary>La fecha de la programación no es un instante con desfase explícito dentro de las cotas (spec 020 R4 y R5).</summary>
    public const string ScheduleDateInvalid = "job.schedule_date_invalid";

    /// <summary>La duración estimada no es un número entero de minutos entre 1 y 1440 (spec 020 R6).</summary>
    public const string EstimatedDurationInvalid = "job.estimated_duration_invalid";

    /// <summary>El rango de la agenda no vale: falta un extremo, no lleva desfase, el final no es posterior al principio o pasa de 42 días (spec 020 R11).</summary>
    public const string AgendaRangeInvalid = "job.agenda_range_invalid";

    public const string NotFound = "job.not_found";
}
