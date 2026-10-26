using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OfiFlow.Infrastructure.Identity;

namespace OfiFlow.Infrastructure.Tests.Identity;

/// <summary>
/// La clave de cifrado del segundo factor (spec 010 R3): sin ella, o con una inválida, la API no arranca.
/// Como <c>Jwt:Secret</c>, arrancar "a medias" sería peor que no arrancar.
/// </summary>
public class MfaOptionsTests
{
    private static readonly string ValidKey = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

    [Fact]
    public void AValidKey_IsParsedToItsThirtyTwoBytes()
    {
        var key = MfaOptions.ParseKey(ValidKey);

        Assert.Equal(32, key.Length);
        Assert.Equal(1, key[0]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AMissingKey_FailsWithAMessageThatSaysHowToSetIt(string? value)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => MfaOptions.ParseKey(value));

        Assert.Contains("Falta 'Mfa:EncryptionKey'", exception.Message);
        Assert.Contains("user-secrets", exception.Message);
        Assert.Contains("Mfa__EncryptionKey", exception.Message);
    }

    [Fact]
    public void AKeyThatIsNotBase64_Fails_WithoutEchoingIt()
    {
        const string notBase64 = "esto-no-es-base64-secretísimo!!";

        var exception = Assert.Throws<InvalidOperationException>(() => MfaOptions.ParseKey(notBase64));

        Assert.Contains("no es Base64", exception.Message);
        Assert.DoesNotContain(notBase64, exception.Message);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(64)]
    public void AKeyOfTheWrongLength_Fails_SayingHowLongItWas(int length)
    {
        var base64 = Convert.ToBase64String(new byte[length]);

        var exception = Assert.Throws<InvalidOperationException>(() => MfaOptions.ParseKey(base64));

        Assert.Contains($"mide {length} bytes", exception.Message);
        Assert.Contains("32", exception.Message);
        Assert.DoesNotContain(base64, exception.Message);
    }

    // ===== Que de verdad lo exija el arranque (AddInfrastructure) =====

    private static ServiceCollection Register(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Secret"] = Convert.ToBase64String(new byte[32]),
            ["Jwt:PlatformSecret"] = Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray()),
            ["Jwt:Issuer"] = "OfiFlow.Tests",
            ["Jwt:Audience"] = "OfiFlow.Tests",
            ["ConnectionStrings:Default"] = "Server=no-se-usa;Database=no-se-usa"
        }.Concat(settings).ToDictionary()).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(configuration);

        return services;
    }

    [Fact]
    public void Startup_WithoutTheKey_Fails()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Secret"] = Convert.ToBase64String(new byte[32]),
            ["Jwt:PlatformSecret"] = Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray()),
            ["Jwt:Issuer"] = "OfiFlow.Tests",
            ["Jwt:Audience"] = "OfiFlow.Tests",
            ["ConnectionStrings:Default"] = "Server=no-se-usa;Database=no-se-usa"
        }).Build();

        var exception = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddInfrastructure(configuration));

        Assert.Contains("Mfa:EncryptionKey", exception.Message);
    }

    [Fact]
    public void Startup_WithAKeyOfTheWrongLength_Fails()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            Register(new Dictionary<string, string?> { ["Mfa:EncryptionKey"] = Convert.ToBase64String(new byte[31]) }));

        Assert.Contains("31 bytes", exception.Message);
    }

    [Fact]
    public void Startup_WithAValidKey_RegistersAWorkingProtector()
    {
        // Control positivo: la configuración correcta arranca, y el protector que sale cifra y descifra.
        using var provider = Register(new Dictionary<string, string?> { ["Mfa:EncryptionKey"] = ValidKey }).BuildServiceProvider();
        var protector = provider.GetRequiredService<ISecretProtector>();
        var user = Guid.NewGuid().ToByteArray();

        var stored = protector.Protect([1, 2, 3], user);

        Assert.Equal(new byte[] { 1, 2, 3 }, protector.Unprotect(stored, user));
        Assert.Same(protector, provider.GetRequiredService<ISecretProtector>()); // un único protector
    }
}
