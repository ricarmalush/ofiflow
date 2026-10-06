using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using OfiFlow.Application.Common.Abstractions;
using OfiFlow.Application.Common.Behaviors;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Common.Persistence;
using OfiFlow.Application.Customers.Commands.CreateCustomer;
using OfiFlow.Application.Customers.Commands.DeleteCustomer;
using OfiFlow.Application.Identity.Commands.Login;
using OfiFlow.Application.Jobs.Commands.CreateJob;
using OfiFlow.Application.Tests.Common;
using OfiFlow.Domain.Customers;
using OfiFlow.Domain.Jobs;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Tests.Authorization;

/// <summary>
/// Spec 008 con el pipeline real de MediatR (el que registra AddApplication), no con el behavior
/// suelto: comprueba el registro, el orden (la autorización va antes que la validación) y que los
/// handlers solo se alcanzan con permiso.
/// </summary>
public class AuthorizationPipelineTests
{
    private static readonly Guid TenantId = Guid.NewGuid();

    private static (ServiceProvider Provider, TestDbContext Db) Build(TenantRole? role)
    {
        var db = TestDbContextFactory.Create();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApplication();
        services.AddSingleton<ICurrentUser>(new FakeCurrentUser(role, Guid.NewGuid(), TenantId));
        services.AddSingleton<ITenantContext>(new FakeTenantContext(TenantId));
        services.AddSingleton<IApplicationDbContext>(db);
        services.AddSingleton<IIdentityService>(new FakeIdentityService());
        services.AddSingleton<ITokenService>(new FakeTokenService());

        return (services.BuildServiceProvider(), db);
    }

    [Fact]
    public void TheAuthorizationBehavior_IsRegisteredBeforeTheValidationBehavior()
    {
        var services = new ServiceCollection();
        services.AddApplication();

        var behaviors = services
            .Where(descriptor => descriptor.ServiceType == typeof(IPipelineBehavior<,>))
            .Select(descriptor => descriptor.ImplementationType)
            .ToList();

        Assert.Contains(typeof(AuthorizationBehavior<,>), behaviors);
        Assert.Contains(typeof(ValidationBehavior<,>), behaviors);
        Assert.True(
            behaviors.IndexOf(typeof(AuthorizationBehavior<,>)) < behaviors.IndexOf(typeof(ValidationBehavior<,>)),
            "La autorización debe registrarse antes que la validación: MediatR ejecuta los behaviors en el orden de registro.");
    }

    [Fact]
    public async Task WithoutPermission_TheUserGetsForbidden_NotAValidationError()
    {
        // Título vacío y cliente vacío: el validador lo rechazaría. Pero Technician no tiene Jobs.Write,
        // así que la autorización responde primero y no revela nada sobre la validez de los datos.
        var (provider, _) = Build(TenantRole.Technician);

        var exception = await Assert.ThrowsAsync<ForbiddenException>(() =>
            provider.GetRequiredService<ISender>().Send(new CreateJobCommand(Guid.Empty, "", null, JobPriority.Normal)));

        Assert.Equal(TenancyErrors.Forbidden, exception.Code);
    }

    [Fact]
    public async Task WithPermission_TheSameInvalidData_FailsValidation()
    {
        // Control del test anterior: Employee sí tiene Jobs.Write, así que ahora sí llega a la validación.
        var (provider, _) = Build(TenantRole.Employee);

        await Assert.ThrowsAsync<ValidationException>(() =>
            provider.GetRequiredService<ISender>().Send(new CreateJobCommand(Guid.Empty, "", null, JobPriority.Normal)));
    }

    [Fact]
    public async Task WithPermission_TheOperationReachesItsHandler()
    {
        var (provider, db) = Build(TenantRole.Manager);
        var customer = Customer.Create(TenantId, CustomerType.Person, "Juan", null, null, null, null);
        db.Customers.Add(customer);
        await db.SaveChangesAsync();

        var jobId = await provider.GetRequiredService<ISender>()
            .Send(new CreateJobCommand(customer.Id, "Reparar fuga", null, JobPriority.Normal));

        Assert.NotEqual(Guid.Empty, jobId);
        Assert.Single(db.Jobs);
    }

    [Fact]
    public async Task WithoutPermission_TheHandlerNeverRuns()
    {
        var (provider, db) = Build(TenantRole.Manager);
        var customer = Customer.Create(TenantId, CustomerType.Person, "Juan", null, null, null, null);
        db.Customers.Add(customer);
        await db.SaveChangesAsync();

        // Manager no tiene Customers.Delete (borrado físico, irreversible).
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            provider.GetRequiredService<ISender>().Send(new DeleteCustomerCommand(customer.Id)));

        Assert.Single(db.Customers);
    }

    [Fact]
    public async Task AnAnonymousOperation_PassesAuthorizationWithoutAnyRole()
    {
        var (provider, _) = Build(role: null);

        // Credenciales inválidas para el fake: devuelve null, pero llega al handler (no se deniega).
        var result = await provider.GetRequiredService<ISender>().Send(new LoginCommand("nadie@example.com", "x"));

        Assert.Null(result);
    }

    [Fact]
    public async Task AnAuthenticatedOperationWithoutARole_IsForbidden()
    {
        var (provider, _) = Build(role: null);

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            provider.GetRequiredService<ISender>().Send(new CreateCustomerCommand(CustomerType.Person, "Juan", null, null, null, null)));
    }
}
