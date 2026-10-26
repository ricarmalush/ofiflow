namespace OfiFlow.Application.Common;

/// <summary>Una página de resultados, con lo necesario para pedir la siguiente. <paramref name="TotalCount"/> es el total <b>con el filtro aplicado</b>.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);
