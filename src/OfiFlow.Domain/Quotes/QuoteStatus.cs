namespace OfiFlow.Domain.Quotes;

/// <summary>
/// Estado de un presupuesto (spec 023 R4, R7 y R8). El valor se guarda como texto en la base de datos: no se renumera ni se
/// reordena. <c>Draft → Sent → Accepted | Rejected</c>; solo un borrador se modifica y ningún presupuesto resuelto se reabre.
/// </summary>
public enum QuoteStatus
{
    Draft,
    Sent,
    Accepted,
    Rejected
}
