namespace OfiFlow.Application.Common.Abstractions;

/// <summary>
/// La auditoría persistente de lo que hace un operador de la plataforma (spec 011 R17, D7). A diferencia del log, que
/// caduca a los 14 días (ADR-015), estas filas se conservan: responden a «¿quién suspendió mi empresa el mes pasado?».
/// </summary>
public interface IPlatformAudit
{
    /// <summary>
    /// Añade una fila de auditoría <b>sin confirmarla</b>: la confirma el handler en su único SaveChanges, <b>en la misma
    /// transacción</b> que la acción. Si la acción falla, no queda fila; si queda la fila, la acción ocurrió. Lleva solo Ids,
    /// un código de acción (<see cref="Platform.PlatformAuditActions"/>) y la hora: ningún dato personal.
    /// </summary>
    void Add(Guid operatorUserId, string action, Guid? tenantId, DateTime utcNow);
}
