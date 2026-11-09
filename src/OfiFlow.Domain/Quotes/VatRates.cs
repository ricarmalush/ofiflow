namespace OfiFlow.Domain.Quotes;

/// <summary>
/// Los tipos de IVA admitidos (spec 023 R3, D10): el caso español (exento, superreducido, reducido y general). Es una lista
/// <b>cerrada y en un solo sitio</b>: un porcentaje libre sería un campo más que validar sin beneficio y permitiría un 13,37 %
/// mandado por un cliente manipulado. Otro país o un tipo nuevo se añade aquí y en ningún otro sitio.
/// </summary>
public static class VatRates
{
    public static readonly IReadOnlyList<int> Allowed = [0, 4, 10, 21];

    public static bool IsAllowed(int percent) => Allowed.Contains(percent);
}
