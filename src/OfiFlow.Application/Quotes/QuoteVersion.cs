namespace OfiFlow.Application.Quotes;

/// <summary>La versión de fila de un presupuesto como texto (base64) y su comparación, para el control de concurrencia optimista (spec 023 R10, D9).</summary>
public static class QuoteVersion
{
    public static string Encode(byte[] rowVersion) => Convert.ToBase64String(rowVersion);

    /// <summary>Si el texto es base64 válido y no vacío: lo comprueba el validador antes de llegar al handler.</summary>
    public static bool IsWellFormed(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return false;
        }

        Span<byte> buffer = stackalloc byte[version.Length];
        return Convert.TryFromBase64String(version, buffer, out var written) && written > 0;
    }

    public static bool Matches(string version, byte[] current) => string.Equals(version, Encode(current), StringComparison.Ordinal);
}
