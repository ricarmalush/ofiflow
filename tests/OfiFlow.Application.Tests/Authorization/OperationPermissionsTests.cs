using MediatR;
using OfiFlow.Application.Common.Authorization;
using OfiFlow.Application.Customers.Commands.CreateCustomer;
using OfiFlow.Application.Customers.Commands.DeleteCustomer;
using OfiFlow.Application.Customers.Commands.UpdateCustomer;
using OfiFlow.Application.Customers.Queries.GetCustomer;
using OfiFlow.Application.Customers.Queries.GetCustomers;
using OfiFlow.Application.Identity.Commands.AcceptInvitation;
using OfiFlow.Application.Identity.Commands.Login;
using OfiFlow.Application.Identity.Commands.RefreshToken;
using OfiFlow.Application.Identity.Commands.Register;
using OfiFlow.Application.Identity.Commands.VerifyMfaChallenge;
using OfiFlow.Application.Identity.Mfa.Commands.DisableMfa;
using OfiFlow.Application.Identity.Mfa.Commands.EnableMfa;
using OfiFlow.Application.Identity.Mfa.Commands.RegenerateRecoveryCodes;
using OfiFlow.Application.Identity.Mfa.Commands.StartMfaSetup;
using OfiFlow.Application.Identity.Mfa.Queries.GetMfaStatus;
using OfiFlow.Application.Jobs.Commands.AssignJob;
using OfiFlow.Application.Jobs.Commands.CancelJob;
using OfiFlow.Application.Jobs.Commands.CompleteJob;
using OfiFlow.Application.Jobs.Commands.CreateJob;
using OfiFlow.Application.Jobs.Commands.StartJob;
using OfiFlow.Application.Jobs.Commands.UpdateJob;
using OfiFlow.Application.Jobs.Queries.GetJob;
using OfiFlow.Application.Jobs.Queries.GetJobs;
using OfiFlow.Application.Platform.Commands.PlatformLogin;
using OfiFlow.Application.Platform.Commands.ReactivateTenant;
using OfiFlow.Application.Platform.Commands.SuspendTenant;
using OfiFlow.Application.Platform.Commands.VerifyPlatformChallenge;
using OfiFlow.Application.Platform.Queries.ListTenants;
using OfiFlow.Application.Tenancy.Commands.ChangeUserRole;
using OfiFlow.Application.Tenancy.Commands.InviteUser;
using OfiFlow.Application.Tenancy.Commands.RemoveUser;
using OfiFlow.Application.Tenancy.Commands.ResetUserMfa;
using OfiFlow.Application.Tenancy.Commands.RevokeInvitation;
using OfiFlow.Application.Tenancy.Queries.GetInvitations;
using OfiFlow.Application.Tenancy.Queries.GetUsers;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Tests.Authorization;

/// <summary>
/// La tabla "operación → permiso" de la spec 008 como especificación ejecutable: cada una de las
/// 35 operaciones (las 16 de la spec 008, las de invitaciones y miembros de la spec 009, las del segundo factor de la spec 010 y, de la spec 011, las dos del inicio de sesión de plataforma y las tres del panel del operador) declara exactamente el permiso aprobado, o la marca de anónima.
/// </summary>
public class OperationPermissionsTests
{
    private static readonly (Type Operation, Permission Permission)[] AuthenticatedTable =
    [
        (typeof(GetCustomerQuery), Permission.CustomersRead),
        (typeof(GetCustomersQuery), Permission.CustomersRead),
        (typeof(CreateCustomerCommand), Permission.CustomersWrite),
        (typeof(UpdateCustomerCommand), Permission.CustomersWrite),
        (typeof(DeleteCustomerCommand), Permission.CustomersDelete),
        (typeof(GetJobQuery), Permission.JobsRead),
        (typeof(GetJobsQuery), Permission.JobsRead),
        (typeof(CreateJobCommand), Permission.JobsWrite),
        (typeof(UpdateJobCommand), Permission.JobsWrite),
        (typeof(AssignJobCommand), Permission.JobsAssign),
        (typeof(CancelJobCommand), Permission.JobsCancel),
        (typeof(StartJobCommand), Permission.JobsExecute),
        (typeof(CompleteJobCommand), Permission.JobsExecute),
        // Spec 009: invitar y gestionar usuarios. Cada una exige Users.Manage además de la jerarquía de roles.
        (typeof(InviteUserCommand), Permission.UsersManage),
        (typeof(GetInvitationsQuery), Permission.UsersManage),
        (typeof(RevokeInvitationCommand), Permission.UsersManage),
        (typeof(GetUsersQuery), Permission.UsersRead),
        (typeof(ChangeUserRoleCommand), Permission.UsersManage),
        (typeof(RemoveUserCommand), Permission.UsersManage),
        // Spec 010: el segundo factor de la propia cuenta (los cinco roles) y el reinicio por un gestor (con la jerarquía).
        (typeof(GetMfaStatusQuery), Permission.MfaManage),
        (typeof(StartMfaSetupCommand), Permission.MfaManage),
        (typeof(EnableMfaCommand), Permission.MfaManage),
        (typeof(DisableMfaCommand), Permission.MfaManage),
        (typeof(RegenerateRecoveryCodesCommand), Permission.MfaManage),
        (typeof(ResetUserMfaCommand), Permission.UsersManage)
    ];

    private static readonly Type[] AnonymousTable =
    [
        typeof(RegisterCommand),
        typeof(LoginCommand),
        typeof(RefreshTokenCommand),
        typeof(AcceptInvitationCommand), // spec 009: anónima, el token de la invitación es la credencial
        typeof(VerifyMfaChallengeCommand), // spec 010: anónima, el desafío del primer paso es la credencial
        typeof(PlatformLoginCommand), // spec 011: anónima, la contraseña del operador es la credencial (y solo da un desafío)
        typeof(VerifyPlatformChallengeCommand) // spec 011: anónima, el desafío de plataforma es la credencial
    ];

    // Spec 011: las operaciones del panel no piden un permiso de empresa (un operador no tiene ninguna): piden ser un operador activo.
    private static readonly Type[] PlatformTable =
    [
        typeof(ListTenantsQuery),
        typeof(SuspendTenantCommand),
        typeof(ReactivateTenantCommand)
    ];

    public static TheoryData<Type> Platform
    {
        get
        {
            var data = new TheoryData<Type>();
            foreach (var operation in PlatformTable)
            {
                data.Add(operation);
            }

            return data;
        }
    }

    public static TheoryData<Type, Permission> Authenticated
    {
        get
        {
            var data = new TheoryData<Type, Permission>();
            foreach (var (operation, permission) in AuthenticatedTable)
            {
                data.Add(operation, permission);
            }

            return data;
        }
    }

    public static TheoryData<Type> Anonymous
    {
        get
        {
            var data = new TheoryData<Type>();
            foreach (var operation in AnonymousTable)
            {
                data.Add(operation);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Authenticated))]
    public void AnAuthenticatedOperation_DeclaresExactlyTheApprovedPermission(Type operation, Permission expected)
    {
        var declared = operation.GetCustomAttributes(typeof(RequiresPermissionAttribute), inherit: true)
            .Cast<RequiresPermissionAttribute>()
            .Select(attribute => attribute.Permission)
            .ToList();

        Assert.Equal([expected], declared);
        Assert.False(operation.IsDefined(typeof(AllowAnonymousRequestAttribute), inherit: true));
        Assert.False(operation.IsDefined(typeof(RequiresPlatformOperatorAttribute), inherit: true));
    }

    [Theory]
    [MemberData(nameof(Anonymous))]
    public void AnAnonymousOperation_IsMarkedAsSuchAndDemandsNoPermission(Type operation)
    {
        Assert.True(operation.IsDefined(typeof(AllowAnonymousRequestAttribute), inherit: true));
        Assert.False(operation.IsDefined(typeof(RequiresPermissionAttribute), inherit: true));
        Assert.False(operation.IsDefined(typeof(RequiresPlatformOperatorAttribute), inherit: true));
    }

    [Theory]
    [MemberData(nameof(Platform))]
    public void APlatformOperation_DemandsAnActiveOperator_AndNoCompanyPermissionNorAnonymousAccess(Type operation)
    {
        Assert.True(operation.IsDefined(typeof(RequiresPlatformOperatorAttribute), inherit: true));
        Assert.False(operation.IsDefined(typeof(RequiresPermissionAttribute), inherit: true));
        Assert.False(operation.IsDefined(typeof(AllowAnonymousRequestAttribute), inherit: true));
    }

    [Fact]
    public void TheTableCoversEveryOperationOfTheApplication()
    {
        // Si se añade una operación y no se añade aquí, este test falla: la tabla aprobada no queda incompleta.
        var inTheTable = AuthenticatedTable.Select(row => row.Operation).Concat(AnonymousTable).Concat(PlatformTable).ToHashSet();

        var inTheApplication = typeof(OfiFlow.Application.DependencyInjection).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } && type.GetInterfaces().Any(IsMediatRRequest))
            .ToHashSet();

        Assert.Equal(inTheApplication.OrderBy(t => t.Name), inTheTable.OrderBy(t => t.Name));
        Assert.Equal(35, inTheTable.Count);
    }

    private static bool IsMediatRRequest(Type type) =>
        type == typeof(IRequest) || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IRequest<>));
}
