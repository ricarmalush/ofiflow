using FluentValidation.TestHelper;
using Microsoft.Extensions.Logging;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Logging;
using OfiFlow.Application.Identity;
using OfiFlow.Application.Identity.Commands.ForgotPassword;
using OfiFlow.Application.Tests.Common;
using OfiFlow.Domain.Common;

namespace OfiFlow.Application.Tests.Identity;

/// <summary>
/// «He olvidado mi contraseña» (spec 018, bloque 2): lo que el caso de uso hace con cada resultado y, sobre todo, lo que <b>no</b> hace — no cambia lo que ve quien lo pide,
/// no envía el correo dentro de la petición y no registra el email, el token ni el enlace.
/// </summary>
public class ForgotPasswordCommandHandlerTests
{
    private const string Address = "victima.secreta@example.com";
    private const string Token = "tok_SECRETO_que_no_debe_salir_en_ningun_log_1234567";
    private static readonly Guid UserId = Guid.NewGuid();

    private readonly FakePasswordResetService _resets = new();
    private readonly FakeEmailSender _sender = new(isEnabled: true);
    private readonly FakeEmailQueue _queue = new();
    private readonly ListLogger<ForgotPasswordCommandHandler> _logger = new();

    private ForgotPasswordCommandHandler Handler() => new(_resets, _sender, _queue, new FakeEmailLinks(), _logger);

    private Task AskAsync(string email = Address) => Handler().Handle(new ForgotPasswordCommand(email), CancellationToken.None);

    private static PasswordResetIssue Issued() => new(PasswordResetIssueOutcome.Issued, UserId, Token);

    // ===== Con cuenta: el correo se encola =====

    [Fact]
    public async Task WithAnAccount_OneEmailIsQueued_ToTheTypedAddress_WithTheResetLink()
    {
        _resets.Result = Issued();

        await AskAsync();

        var message = Assert.Single(_queue.Queued);
        Assert.Equal(Address, message.To.Value);
        Assert.Equal(PasswordResetEmail.Subject, message.Subject);
        Assert.Contains($"{FakeEmailLinks.BaseUrl}/auth/reset-password#token={Token}", message.TextBody);
    }

    [Fact]
    public async Task TheTypedEmail_IsPassedToTheServiceUnchanged()
    {
        _resets.Result = Issued();

        await AskAsync("Ana.Mayusculas@Example.com");

        Assert.Equal(["Ana.Mayusculas@Example.com"], _resets.Requested);
    }

    [Fact]
    public async Task TheEmailIsNeverSentInsideTheRequest_ItOnlyGoesToTheQueue()
    {
        // R2, D3: enviarlo dentro de la petición tardaría segundos solo cuando la cuenta existe: un oráculo de tiempo.
        _resets.Result = Issued();

        await AskAsync();

        Assert.Empty(_sender.Sent);
        Assert.Single(_queue.Queued);
    }

    [Fact]
    public async Task AFullQueue_DoesNotChangeWhatTheCallerSees_NorFailTheRequest()
    {
        // R15: si la cola está llena, ella lo registra; la petición termina igual (y la respuesta HTTP no puede delatar el estado interno de la cola).
        _resets.Result = Issued();
        _queue.Accepts = false;

        await AskAsync();

        Assert.Empty(_queue.Queued);
        Assert.Empty(_sender.Sent);
    }

    // ===== Sin cuenta, limitada o con el correo apagado: nada sale, y no se nota =====

    [Fact]
    public async Task WithoutAnAccount_NothingIsQueued_AndTheRequestCompletesTheSame()
    {
        _resets.Result = new(PasswordResetIssueOutcome.UnknownAccount);

        await AskAsync();

        Assert.Empty(_queue.Queued);
        Assert.Empty(_sender.Sent);
    }

    [Fact]
    public async Task AThrottledAccount_QueuesNothing_AndTheRequestCompletesTheSame()
    {
        _resets.Result = new(PasswordResetIssueOutcome.Throttled, UserId);

        await AskAsync();

        Assert.Empty(_queue.Queued);
    }

    [Fact]
    public async Task WithTheEmailOff_NoTokenIsAskedFor_NothingIsQueued_AndAWarningIsLogged()
    {
        // R6: sin correo no hay cómo entregar el token, así que ni se crea (una fila que nadie va a poder usar no debe existir).
        _sender.IsEnabled = false;
        _resets.Result = Issued();

        await AskAsync();

        Assert.Empty(_resets.Requested);
        Assert.Empty(_queue.Queued);
        var warning = Assert.Single(_logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("Email:Provider = None", warning.Message);
    }

    [Fact]
    public async Task TheFourOutcomes_NeverThrow_AndNeverReturnAnything()
    {
        // Lo que ve quien lo pide es lo mismo con todos: el comando no devuelve nada y no lanza.
        foreach (var result in new[]
                 {
                     Issued(),
                     new PasswordResetIssue(PasswordResetIssueOutcome.UnknownAccount),
                     new PasswordResetIssue(PasswordResetIssueOutcome.Throttled, UserId)
                 })
        {
            _resets.Result = result;
            await AskAsync();
        }

        _sender.IsEnabled = false;
        await AskAsync();
    }

    [Fact]
    public async Task AnUnknownOutcome_IsAProgrammingError()
    {
        _resets.Result = new((PasswordResetIssueOutcome)42);

        await Assert.ThrowsAsync<InvalidOperationException>(() => AskAsync());
    }

    // ===== Eventos y registro =====

    [Fact]
    public async Task WithAnAccount_TheRequestEventSaysItExisted_WithTheUserId()
    {
        _resets.Result = Issued();

        await AskAsync();

        var entry = Assert.Single(_logger.Entries, e => e.EventId.Id == SecurityEventIds.PasswordResetRequested);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("True", entry.Message);
        Assert.Contains(UserId.ToString(), entry.Message);
    }

    [Fact]
    public async Task WithoutAnAccount_TheRequestEventSaysItDidNotExist_OnlyInTheLog()
    {
        _resets.Result = new(PasswordResetIssueOutcome.UnknownAccount);

        await AskAsync();

        var entry = Assert.Single(_logger.Entries, e => e.EventId.Id == SecurityEventIds.PasswordResetRequested);
        Assert.Contains("False", entry.Message);
    }

    [Fact]
    public async Task AThrottledRequest_LeavesItsOwnWarning_WithTheUserId()
    {
        _resets.Result = new(PasswordResetIssueOutcome.Throttled, UserId);

        await AskAsync();

        var entry = Assert.Single(_logger.Entries, e => e.EventId.Id == SecurityEventIds.PasswordResetThrottled);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains(UserId.ToString(), entry.Message);
        Assert.DoesNotContain(_logger.Entries, e => e.EventId.Id == SecurityEventIds.PasswordResetRequested);
    }

    [Theory]
    [InlineData(PasswordResetIssueOutcome.Issued)]
    [InlineData(PasswordResetIssueOutcome.UnknownAccount)]
    [InlineData(PasswordResetIssueOutcome.Throttled)]
    public async Task NothingThatIsLogged_CarriesTheAddress_TheToken_OrTheLink(PasswordResetIssueOutcome outcome)
    {
        _resets.Result = outcome == PasswordResetIssueOutcome.Issued ? Issued() : new(outcome, UserId);

        await AskAsync();

        var everything = string.Join(" ", _logger.Entries.Select(e => e.Message));
        Assert.DoesNotContain("victima", everything);
        Assert.DoesNotContain(Address, everything);
        Assert.DoesNotContain(Token, everything);
        Assert.DoesNotContain(FakeEmailLinks.BaseUrl, everything);
        Assert.DoesNotContain("reset-password", everything);
    }

    [Fact]
    public async Task WithTheEmailOff_TheLogCarriesNoPersonalDataEither()
    {
        _sender.IsEnabled = false;

        await AskAsync();

        Assert.DoesNotContain("victima", string.Join(" ", _logger.Entries.Select(e => e.Message)));
    }

    // ===== La validación: lo único que se decide sin consultar nada =====

    private readonly ForgotPasswordCommandValidator _validator = new();

    [Theory]
    [InlineData("ana@example.com")]
    [InlineData("Ana.Perez+etiqueta@sub.example.co.uk")]
    public void AWellFormedEmail_IsValid(string email)
    {
        _validator.TestValidate(new ForgotPasswordCommand(email)).ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sin-arroba")]
    [InlineData("a@b")]
    [InlineData("a b@example.com")]
    [InlineData("ana@example.com\n")]
    [InlineData("ana@example.com\r\nBcc: otro@example.com")]
    public void AMalformedEmail_IsRefused_BeforeAnythingIsLookedUp(string email)
    {
        _validator.TestValidate(new ForgotPasswordCommand(email)).ShouldHaveValidationErrorFor(x => x.Email);
    }

    [Fact]
    public void AnEmailLongerThanTheLimit_IsRefused_WithASingleError()
    {
        var result = _validator.TestValidate(new ForgotPasswordCommand(new string('a', Email.MaxLength) + "@example.com"));

        Assert.Single(result.Errors.Where(e => e.PropertyName == nameof(ForgotPasswordCommand.Email)));
    }

    [Fact]
    public void TheCommand_IsMarkedAsAnonymous()
    {
        // La persona no puede iniciar sesión: por eso lo pide. Sin la marca, el AuthorizationBehavior la rechazaría (fail-closed).
        Assert.NotEmpty(typeof(ForgotPasswordCommand).GetCustomAttributes(typeof(OfiFlow.Application.Common.Authorization.AllowAnonymousRequestAttribute), inherit: false));
    }
}
