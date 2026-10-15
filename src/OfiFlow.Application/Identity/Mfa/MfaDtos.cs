namespace OfiFlow.Application.Identity.Mfa;

/// <summary>Cómo está el segundo factor de la propia cuenta. Nunca lleva secretos ni códigos.</summary>
public sealed record MfaStatusDto(bool Enabled, bool SetupPending, int RecoveryCodesRemaining);

/// <summary>
/// El secreto recién creado, para escanearlo en la app del móvil. Es la <b>única</b> vez que se muestra: no se puede
/// volver a pedir. La respuesta HTTP lleva <c>Cache-Control: no-store</c>.
/// </summary>
/// <param name="Secret">El secreto en Base32, por si se quiere escribir a mano en la app.</param>
/// <param name="OtpAuthUri">La URI <c>otpauth://</c> que el frontend dibuja como QR.</param>
public sealed record MfaSetupDto(string Secret, string OtpAuthUri);

/// <summary>Los códigos de recuperación recién generados. También se muestran una sola vez (<c>no-store</c>).</summary>
public sealed record RecoveryCodesDto(IReadOnlyList<string> Codes);
