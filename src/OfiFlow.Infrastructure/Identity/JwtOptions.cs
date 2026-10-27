namespace OfiFlow.Infrastructure.Identity;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Secret { get; set; } = string.Empty;

    public string Issuer { get; set; } = string.Empty;

    public string Audience { get; set; } = string.Empty;

    public int AccessTokenMinutes { get; set; } = 30;

    public int RefreshTokenDays { get; set; } = 7;

    /// <summary>
    /// La clave que firma las sesiones de los operadores de la plataforma (spec 011 R7). Es <b>otra</b> que <see cref="Secret"/>
    /// a propósito: con una sola clave, un fallo en la validación dejaría que un token de empresa valiera en la plataforma. No tiene
    /// valor por defecto y la API no arranca sin ella (ver <see cref="PlatformAuthentication.ParseKey"/>).
    /// </summary>
    public string PlatformSecret { get; set; } = string.Empty;

    /// <summary>La audiencia de las sesiones de plataforma. Debe ser distinta de <see cref="Audience"/>: no es un secreto, así que tiene valor por defecto.</summary>
    public string PlatformAudience { get; set; } = "OfiFlow.Platform";
}
