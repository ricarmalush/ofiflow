using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Resources;
using System.Security.Cryptography;
using System.Text.Json;
using MediatR;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OfiFlow.Api.Resources;
using OfiFlow.Api.Tests.Common;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Domain.Common;
using OfiFlow.Domain.Customers;
using OfiFlow.Domain.Identity;
using OfiFlow.Domain.Jobs;

namespace OfiFlow.Api.Tests.Errors;

/// <summary>
/// Contrato de las respuestas de error (ADR-011 R4). Los códigos se comprueban con literales
/// porque son contrato público; los mensajes, contra el diccionario, porque su redacción puede
/// cambiar sin romper a ningún cliente.
/// </summary>
internal static class Dictionary
{
    private static readonly ResourceManager Messages = new(typeof(ErrorMessages).FullName!, typeof(ErrorMessages).Assembly);

    public static string Spanish(string key) => Messages.GetString(key, new CultureInfo("es"))!;

    public static async Task<JsonElement> ReadProblemAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();
}

/// <summary>
/// Validación real (pipeline completo de MediatR y FluentValidation) contra /auth/login, que es
/// anónimo y responde 400 antes de tocar la base de datos. Máximo 5 peticiones por el rate limit.
/// </summary>
public class ValidationErrorContractTests(OfiFlowApiFactory factory) : IClassFixture<OfiFlowApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task InvalidEmail_ReturnsFieldErrorWithOurCodeAndDictionaryMessage()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/auth/login", new { email = "a@b", password = "x" });
        var problem = await Dictionary.ReadProblemAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation.failed", problem.GetProperty("code").GetString());

        var emailError = problem.GetProperty("errors").EnumerateArray().Single(e => e.GetProperty("field").GetString() == "Email");
        Assert.Equal("common.email_invalid", emailError.GetProperty("code").GetString());
        Assert.Equal(Dictionary.Spanish(CommonErrors.EmailInvalid), emailError.GetProperty("message").GetString());
    }

    [Fact]
    public async Task UnsupportedLanguage_FallsBackToSpanish_IncludingFluentValidationMessages()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new { email = "", password = "x" })
        };
        request.Headers.AcceptLanguage.ParseAdd("en-US");

        var response = await _client.SendAsync(request);
        var problem = await Dictionary.ReadProblemAsync(response);

        Assert.Equal(Dictionary.Spanish("validation.failed"), problem.GetProperty("detail").GetString());

        // Mensaje estándar de FluentValidation: también en español, aunque se pida inglés y el
        // servidor tenga el sistema operativo en cualquier idioma.
        var emailError = problem.GetProperty("errors").EnumerateArray().First(e => e.GetProperty("field").GetString() == "Email");
        Assert.Equal("NotEmptyValidator", emailError.GetProperty("code").GetString());
        Assert.Contains("vacío", emailError.GetProperty("message").GetString());
    }
}

/// <summary>
/// Traducción de cada tipo de excepción a HTTP + código + mensaje, sin base de datos: el ISender
/// de MediatR se sustituye por uno que lanza la excepción que indica cada test. Se usa
/// /auth/refresh porque es anónimo y su límite (20/min) admite todas las peticiones de la clase.
/// </summary>
public class ExceptionMappingContractTests(ExceptionMappingContractTests.ConfigurableSenderFactory factory)
    : IClassFixture<ExceptionMappingContractTests.ConfigurableSenderFactory>
{
    public static TheoryData<Exception, HttpStatusCode, string> Cases => new()
    {
        { new DomainException(JobErrors.CannotComplete, JobStatus.New), HttpStatusCode.BadRequest, "job.cannot_complete" },
        { new BusinessRuleException(CustomerErrors.HasActiveJobs), HttpStatusCode.BadRequest, "customer.has_active_jobs" },
        { new NotFoundException(CustomerErrors.NotFound, Guid.NewGuid()), HttpStatusCode.NotFound, "customer.not_found" },
        { new IdentityOperationException([IdentityErrors.EmailAlreadyRegistered]), HttpStatusCode.BadRequest, "user.email_already_registered" }
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Exception_IsTranslatedToStatusCodeAndDictionaryMessage(Exception exception, HttpStatusCode status, string code)
    {
        var response = await SendAsync(exception);
        var problem = await Dictionary.ReadProblemAsync(response);

        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.Equal(Dictionary.Spanish(code), problem.GetProperty("detail").GetString());
        Assert.True(problem.TryGetProperty("traceId", out _));
    }

    [Fact]
    public async Task UnsupportedLanguage_FallsBackToSpanish()
    {
        var response = await SendAsync(new DomainException(JobErrors.CannotComplete, JobStatus.New), acceptLanguage: "fr-FR");
        var problem = await Dictionary.ReadProblemAsync(response);

        Assert.Equal(Dictionary.Spanish(JobErrors.CannotComplete), problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task UnexpectedException_Is500WithGenericDictionaryMessage_NeverABusinessError()
    {
        // Antes de ADR-011 un InvalidOperationException de Domain se convertía en 400. Ahora un
        // tipo genérico es siempre un bug: 500 con el mensaje genérico.
        var response = await SendAsync(new InvalidOperationException("detalle interno"));
        var problem = await Dictionary.ReadProblemAsync(response);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("server.unexpected", problem.GetProperty("code").GetString());
        Assert.Equal(Dictionary.Spanish("server.unexpected"), problem.GetProperty("detail").GetString());
    }

    private async Task<HttpResponseMessage> SendAsync(Exception exception, string? acceptLanguage = null)
    {
        factory.ExceptionToThrow = exception;
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh")
        {
            Content = JsonContent.Create(new { refreshToken = "x" })
        };
        if (acceptLanguage is not null)
        {
            request.Headers.AcceptLanguage.ParseAdd(acceptLanguage);
        }

        return await factory.CreateClient().SendAsync(request);
    }

    public sealed class ConfigurableSenderFactory : WebApplicationFactory<Program>
    {
        public Exception ExceptionToThrow { get; set; } = new InvalidOperationException("sin configurar");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Jwt:Secret", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            builder.ConfigureTestServices(services => services.AddSingleton<ISender>(new ThrowingSender(this)));
        }
    }

    private sealed class ThrowingSender(ConfigurableSenderFactory factory) : ISender
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw factory.ExceptionToThrow;

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw factory.ExceptionToThrow;

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            throw factory.ExceptionToThrow;

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
