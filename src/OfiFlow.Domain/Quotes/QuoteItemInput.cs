namespace OfiFlow.Domain.Quotes;

/// <summary>
/// Una línea tal y como llega al dominio (spec 023 R3): descripción, cantidad, precio unitario en <b>céntimos enteros</b> y tipo de IVA.
/// Nunca lleva un importe calculado: el total de la línea lo calcula el servidor (R5).
/// </summary>
public sealed record QuoteItemInput(string Description, decimal Quantity, long UnitPriceCents, int VatPercent);
