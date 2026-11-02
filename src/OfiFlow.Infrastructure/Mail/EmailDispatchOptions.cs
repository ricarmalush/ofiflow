namespace OfiFlow.Infrastructure.Mail;

/// <summary>
/// La configuración de la cola de correo y de la tarea que la vacía (spec 018 R14). Como las demás secciones, se valida al arrancar: una capacidad de cero
/// descartaría todo en silencio.
/// </summary>
public sealed class EmailDispatchOptions
{
    public const string SectionName = "EmailDispatch";

    public const int MaxQueueCapacity = 10_000;

    /// <summary>Apagado general. Los tests de la API lo desactivan; en producción debe estar encendido. Apagado, la cola descarta todo lo que recibe.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Cuántos mensajes caben esperando (R15). Si se llena, el nuevo se descarta y se registra. Son mensajes sueltos de unas pocas personas a la vez: 100 sobra.</summary>
    public int QueueCapacity { get; set; } = 100;

    /// <summary>Devuelve el motivo si la configuración no vale, o <c>null</c>. Nombra el ajuste y el rango; nunca refleja un valor.</summary>
    public string? Validate() =>
        QueueCapacity is < 1 or > MaxQueueCapacity
            ? $"{SectionName}:QueueCapacity debe estar entre 1 y {MaxQueueCapacity} (con cero, la cola descartaría todos los mensajes)."
            : null;
}
