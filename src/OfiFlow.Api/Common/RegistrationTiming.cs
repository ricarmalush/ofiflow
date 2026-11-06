namespace OfiFlow.Api.Common;

/// <summary>
/// El suelo de tiempo de «pedir el registro» (spec 021 R2, D2). Es el mismo razonamiento que el de «he olvidado mi contraseña» (<see cref="ForgotPasswordTimingOptions"/>, spec 018): la respuesta
/// es idéntica byte a byte exista ya la cuenta o no, pero el <b>trabajo</b> no lo es (con un email nuevo se busca, se cuenta, se anula, se inserta y se confirma; con uno que ya tiene cuenta solo se
/// busca), y esa diferencia de tiempo delata al email aunque la respuesta no lo haga. Se <b>mide</b> con la API real al implementar el bloque 2 antes de darlo por bueno.
/// </summary>
/// <remarks>
/// La opción es propia (<c>Registration:ResponseFloor</c>) y no la de la recuperación: son dos endpoints con dos cargas de trabajo distintas y se deben poder ajustar por separado.
/// La espera la hace el mismo <see cref="ResponseTimeFloor"/>, aplicado <b>después</b> del trabajo.
/// </remarks>
public sealed class RegistrationTimingOptions
{
    public const string SectionName = "Registration";

    public static readonly TimeSpan MaxFloor = TimeSpan.FromSeconds(5);

    /// <summary>Lo que como mínimo tarda la petición. Cero lo desactiva (los tests que no miden el tiempo lo usan para no esperar). 250 ms: el valor de la recuperación, a confirmar con la medición.</summary>
    public TimeSpan ResponseFloor { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Devuelve el motivo si la configuración no vale, o <c>null</c>. Nombra el ajuste y el rango; nunca refleja un valor.</summary>
    public string? Validate() =>
        ResponseFloor < TimeSpan.Zero || ResponseFloor > MaxFloor
            ? $"{SectionName}:ResponseFloor debe estar entre 0 y {MaxFloor.TotalSeconds:0} segundos (un valor mayor mantendría peticiones abiertas sin ganar nada)."
            : null;
}
