namespace OfiFlow.Application.Platform;

/// <summary>
/// Los códigos de acción de la auditoría de plataforma (spec 011 R17). Son un contrato estable, como los códigos de error:
/// quien consulte la auditoría dentro de dos años los busca por este texto, así que no se renombran ni se reutilizan.
/// </summary>
public static class PlatformAuditActions
{
    /// <summary>Un operador superó la contraseña y el segundo factor y recibió una sesión de plataforma.</summary>
    public const string SessionIssued = "platform.session_issued";

    public const string TenantSuspended = "platform.tenant_suspended";

    public const string TenantReactivated = "platform.tenant_reactivated";

    public const string OperatorCreated = "platform.operator_created";

    public const string OperatorDisabled = "platform.operator_disabled";

    /// <summary>Longitud máxima de un código de acción; la comparten la entidad, EF y el test que los comprueba todos.</summary>
    public const int MaxLength = 64;
}
