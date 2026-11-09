using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OfiFlow.Api.Tests.Errors;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Api.Tests.Security;

/// <summary>
/// Los tres permisos de los presupuestos (spec 023 R11, D11) por HTTP, con la API real, SQL Server real y un usuario de cada rol que ha hecho login de verdad. Cada operación
/// se prueba en los cinco roles, con un presupuesto nuevo en el estado que esa operación necesita (así una denegación nunca se debe a un estado equivocado); cada permiso concedido
/// comprueba que la operación tuvo efecto, y cada denegación, que no cambió nada.
/// </summary>
public class QuoteRolesEndToEndTests(RolesFixture fixture) : IClassFixture<RolesFixture>
{
    private static readonly TenantRole[] Columns =
        [TenantRole.Owner, TenantRole.Admin, TenantRole.Manager, TenantRole.Employee, TenantRole.Technician];

    // Escrita aparte de RolePermissions, de la matriz de Domain y de la tabla de operaciones: copias independientes que tienen que coincidir. X = permitido, - = denegado.
    private static readonly string[] Matrix =
    [
        //                    Owner Admin Manager Employee Technician
        "quotes_list          X     X     X       X        -",
        "quote_get            X     X     X       X        -",
        "quote_create         X     X     X       X        -",
        "quote_update         X     X     X       X        -",
        "quote_delete         X     X     X       X        -",
        "quote_send           X     X     X       -        -",
        "quote_accept         X     X     X       -        -",
        "quote_reject         X     X     X       -        -"
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

    private async Task<Guid> CustomerAsync() => await TwoTenantsFixture.CreateCustomerAsync(Owner, $"Cliente {Guid.NewGuid():N}");

    private static object Line(long price = 10000) => new { description = "Mano de obra", quantity = 1, unitPriceCents = price, vatPercent = 21 };

    private async Task<Guid> NewDraftAsync()
    {
        var response = await Owner.PostAsJsonAsync("/api/v1/quotes", new { customerId = await CustomerAsync(), title = "Reforma", items = new[] { Line() } });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task<Guid> NewSentAsync()
    {
        var id = await NewDraftAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await Owner.PostAsync($"/api/v1/quotes/{id}/send", null)).StatusCode);
        return id;
    }

    private async Task<JsonElement> ReadAsOwnerAsync(Guid id) =>
        (await (await Owner.GetAsync($"/api/v1/quotes/{id}")).Content.ReadFromJsonAsync<JsonElement>()).Clone();

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

        switch (operation)
        {
            case "quotes_list":
            {
                var response = await client.GetAsync("/api/v1/quotes");
                if (allowed) Assert.Equal(HttpStatusCode.OK, response.StatusCode); else await AssertForbiddenAsync(response);
                break;
            }

            case "quote_get":
            {
                var id = await NewDraftAsync();
                var response = await client.GetAsync($"/api/v1/quotes/{id}");
                if (allowed) Assert.Equal(HttpStatusCode.OK, response.StatusCode); else await AssertForbiddenAsync(response);
                break;
            }

            case "quote_create":
            {
                var customerId = await CustomerAsync();
                var response = await client.PostAsJsonAsync("/api/v1/quotes", new { customerId, title = $"De {role}", items = new[] { Line() } });
                if (allowed)
                {
                    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                    var created = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
                    Assert.Equal($"De {role}", (await ReadAsOwnerAsync(created)).GetProperty("title").GetString());
                }
                else
                {
                    await AssertForbiddenAsync(response);
                    var list = await (await Owner.GetAsync($"/api/v1/quotes?customerId={customerId}")).Content.ReadFromJsonAsync<JsonElement>();
                    Assert.Equal(0, list.GetProperty("totalCount").GetInt32()); // no se creó nada
                }

                break;
            }

            case "quote_update":
            {
                var id = await NewDraftAsync();
                var version = (await ReadAsOwnerAsync(id)).GetProperty("version").GetString();
                var response = await client.PutAsJsonAsync($"/api/v1/quotes/{id}", new { version, title = "Editado", items = new[] { Line(500) } });
                var title = (await ReadAsOwnerAsync(id)).GetProperty("title").GetString();
                if (allowed) { Assert.Equal(HttpStatusCode.NoContent, response.StatusCode); Assert.Equal("Editado", title); }
                else { await AssertForbiddenAsync(response); Assert.Equal("Reforma", title); }
                break;
            }

            case "quote_delete":
            {
                var id = await NewDraftAsync();
                var response = await client.DeleteAsync($"/api/v1/quotes/{id}");
                var stillThere = (await Owner.GetAsync($"/api/v1/quotes/{id}")).StatusCode == HttpStatusCode.OK;
                if (allowed) { Assert.Equal(HttpStatusCode.NoContent, response.StatusCode); Assert.False(stillThere); }
                else { await AssertForbiddenAsync(response); Assert.True(stillThere); }
                break;
            }

            case "quote_send":
            {
                var id = await NewDraftAsync();
                var response = await client.PostAsync($"/api/v1/quotes/{id}/send", null);
                var status = (await ReadAsOwnerAsync(id)).GetProperty("status").GetString();
                if (allowed) { Assert.Equal(HttpStatusCode.NoContent, response.StatusCode); Assert.Equal("Sent", status); }
                else { await AssertForbiddenAsync(response); Assert.Equal("Draft", status); }
                break;
            }

            case "quote_accept":
            case "quote_reject":
            {
                var id = await NewSentAsync();
                var verb = operation == "quote_accept" ? "accept" : "reject";
                var response = await client.PostAsync($"/api/v1/quotes/{id}/{verb}", null);
                var status = (await ReadAsOwnerAsync(id)).GetProperty("status").GetString();
                if (allowed) { Assert.Equal(HttpStatusCode.NoContent, response.StatusCode); Assert.Equal(operation == "quote_accept" ? "Accepted" : "Rejected", status); }
                else { await AssertForbiddenAsync(response); Assert.Equal("Sent", status); }
                break;
            }

            default:
                throw new InvalidOperationException(operation);
        }
    }

    [Fact]
    public void TheMatrix_CoversEveryEndpointOfTheQuotes()
    {
        // Un centinela: un endpoint nuevo de presupuestos tiene que entrar en la matriz de arriba (ocho operaciones, cinco roles).
        Assert.Equal(8, Matrix.Length);
        Assert.Equal(8 * Columns.Length, Cells().Count);
    }

    [Fact]
    public async Task ADeniedRole_GetsTheSameForbidden_WhetherTheQuoteExistsOrNot_SoNothingLeaks()
    {
        var existing = await NewDraftAsync();
        var technician = fixture.ClientOf(TenantRole.Technician);

        foreach (var id in new[] { existing, Guid.NewGuid() })
        {
            await AssertForbiddenAsync(await technician.GetAsync($"/api/v1/quotes/{id}"));
            await AssertForbiddenAsync(await technician.DeleteAsync($"/api/v1/quotes/{id}"));
            await AssertForbiddenAsync(await technician.PostAsync($"/api/v1/quotes/{id}/send", null));
        }
    }

    [Fact]
    public async Task TheEmployee_PreparesButDoesNotCommit_AndTheManagerDoesBoth()
    {
        var employee = fixture.ClientOf(TenantRole.Employee);
        var manager = fixture.ClientOf(TenantRole.Manager);
        var id = await NewDraftAsync();

        await AssertForbiddenAsync(await employee.PostAsync($"/api/v1/quotes/{id}/send", null)); // no compromete
        var version = (await ReadAsOwnerAsync(id)).GetProperty("version").GetString();
        Assert.Equal(HttpStatusCode.NoContent, (await employee.PutAsJsonAsync($"/api/v1/quotes/{id}", new { version, title = "Preparado", items = new[] { Line(900) } })).StatusCode); // sí prepara
        Assert.Equal(HttpStatusCode.NoContent, (await manager.PostAsync($"/api/v1/quotes/{id}/send", null)).StatusCode); // quien gestiona sí compromete
        await AssertForbiddenAsync(await employee.PostAsync($"/api/v1/quotes/{id}/accept", null));
        Assert.Equal(HttpStatusCode.NoContent, (await manager.PostAsync($"/api/v1/quotes/{id}/accept", null)).StatusCode);
    }

    [Fact]
    public async Task ThePermissions_AreExposedInMe_ForEachRole_ExactlyAsTheMatrixSays()
    {
        foreach (var role in Columns)
        {
            var me = await (await fixture.ClientOf(role).GetAsync("/api/v1/account/me")).Content.ReadFromJsonAsync<JsonElement>();
            var permissions = me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!).ToHashSet();

            Assert.Equal(role != TenantRole.Technician, permissions.Contains("Quotes.Read"));
            Assert.Equal(role != TenantRole.Technician, permissions.Contains("Quotes.Write"));
            Assert.Equal(role is TenantRole.Owner or TenantRole.Admin or TenantRole.Manager, permissions.Contains("Quotes.Issue"));
        }
    }
}
