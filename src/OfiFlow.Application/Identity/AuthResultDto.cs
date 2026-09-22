namespace OfiFlow.Application.Identity;

public sealed record AuthResultDto(string AccessToken, string RefreshToken, DateTime ExpiresAt);
