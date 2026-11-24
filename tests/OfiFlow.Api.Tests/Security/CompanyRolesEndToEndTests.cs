using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OfiFlow.Api.Tests.Errors;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Api.Tests.Security;

/// <summary>
/// Los dos permisos del perfil fiscal (spec 024 R7, D3) por HTTP, con la API real, SQL Server real y un usuario de cada rol que ha hecho login de verdad. Cada operación se prueba en los
/// cinco roles; cada permiso concedido comprueba que la operación tuvo efecto, y cada denegación, que no cambió nada.
/// </summary>
public class CompanyRolesEndToEndTests(RolesFixture fixture) : IClassFixture<RolesFixture>
{
    private const string Route = "/api/v1/company/profile";

    private static readonly TenantRole[] Columns =
        [TenantRole.Owner, TenantRole.Admin, TenantRole.Manager, TenantRole.Employee, TenantRole.Technician];

    // Escrita aparte de RolePermissions, de la matriz de Domain y de la tabla de operaciones: copias independientes que tienen que coincidir. X = permitido, - = denegado.
    private static readonly string[] Matrix =
    [
        //                  Owner Admin Manager Employee Technician
        "company_get        X     X     X       X        -",
        "company_put        X     X     -       -        -"
    ];

    public static TheoryData<string, TenantRole, bool> Cells()
    {
        var data = new TheoryData<string, TenantRole, bool>();

        foreach (var row in Matrix)
        {
            var parts = row.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            for (var column = 0; column < Columns.Length; column++)
            {
                data.Add(parts[0], Columns[column], parts[column + 1] == "X");
            }
        }

        return data;
    }

    private HttpClient Owner => fixture.ClientOf(TenantRole.Owner);

    private static object Body(string legalName) =>
        new { legalName, taxId = "12345678Z", addressLine = "Calle Mayor 1", postalCode = "28001", city = "Madrid" };

    private async Task<string?> LegalNameAsOwnerAsync() =>
        (await (await Owner.GetAsync(Route)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("legalName").GetString();

    private static async Task AssertForbiddenAsync(HttpResponseMessage response)
    {
        var problem = await Dictionary.ReadProblemAsync(response);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("auth.forbidden", problem.GetProperty("code").GetString());
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public async Task EveryOperation_IsAllowedToTheRolesThatHaveItsPermission_AndDeniedToTheOthers(string operation, TenantRole role, bool allowed)
    {
        var client = fixture.ClientOf(role);
        var original = $"Original {Guid.NewGuid():N}";
        Assert.Equal(HttpStatusCode.NoContent, (await Owner.PutAsJsonAsync(Route, Body(original))).StatusCode); // el punto de partida (control positivo del Owner)

        switch (operation)
        {
            case "company_get":
            {
                var response = await client.GetAsync(Route);
                if (allowed)
                {
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    Assert.Equal(original, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("legalName").GetString());
                }
                else
                {
                    await AssertForbiddenAsync(response);
                }

                break;
            }

            case "company_put":
            {
                var edited = $"Editado por {role} {Guid.NewGuid():N}";
                var response = await client.PutAsJsonAsync(Route, Body(edited));
                if (allowed)
                {
                    Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
                    Assert.Equal(edited, await LegalNameAsOwnerAsync());
                }
                else
                {
                    await AssertForbiddenAsync(response);
                    Assert.Equal(original, await LegalNameAsOwnerAsync()); // no cambió nada
                }

                break;
            }

            default:
                throw new InvalidOperationException(operation);
        }
    }

    [Fact]
    public void TheMatrix_CoversEveryEndpointOfTheProfile()
    {
        // Un centinela: un endpoint nuevo del perfil tiene que entrar en la matriz de arriba (dos operaciones, cinco roles).
        Assert.Equal(2, Matrix.Length);
        Assert.Equal(2 * Columns.Length, Cells().Count);
    }

    [Fact]
    public async Task ADeniedRole_GetsTheForbidden_WhateverTheBody_EvenAnInvalidOneIsNotValidatedFirst()
    {
        // La autorización va antes que la validación del contenido: quien no puede modificar el perfil no averigua nada de él (ni siquiera qué NIF se rechaza).
        var manager = fixture.ClientOf(TenantRole.Manager);

        await AssertForbiddenAsync(await manager.PutAsJsonAsync(Route, Body("Válido")));
        await AssertForbiddenAsync(await manager.PutAsJsonAsync(Route, new { legalName = "", taxId = "no-es-un-nif" }));
        await AssertForbiddenAsync(await fixture.ClientOf(TenantRole.Technician).GetAsync(Route));
    }

    [Fact]
    public async Task TheEmployeeAndTheManager_SeeTheProfile_ButCannotChangeIt_AndTheAdminCanBoth()
    {
        var admin = fixture.ClientOf(TenantRole.Admin);
        var edited = $"Del admin {Guid.NewGuid():N}";

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync(Route, Body(edited))).StatusCode);

        foreach (var role in new[] { TenantRole.Manager, TenantRole.Employee })
        {
            var client = fixture.ClientOf(role);
            Assert.Equal(edited, (await (await client.GetAsync(Route)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("legalName").GetString());
            await AssertForbiddenAsync(await client.PutAsJsonAsync(Route, Body("No debe guardarse")));
        }

        Assert.Equal(edited, await LegalNameAsOwnerAsync());
    }

    [Fact]
    public async Task ThePermissions_AreExposedInMe_ForEachRole_ExactlyAsTheMatrixSays()
    {
        foreach (var role in Columns)
        {
            var me = await (await fixture.ClientOf(role).GetAsync("/api/v1/account/me")).Content.ReadFromJsonAsync<JsonElement>();
            var permissions = me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!).ToHashSet();

            Assert.Equal(role != TenantRole.Technician, permissions.Contains("Company.Read"));
            Assert.Equal(role is TenantRole.Owner or TenantRole.Admin, permissions.Contains("Company.Manage"));
        }
    }
}
