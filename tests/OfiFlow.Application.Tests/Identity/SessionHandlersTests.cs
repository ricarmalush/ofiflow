using System.Text.Json;
using MediatR;
using OfiFlow.Application.Common.Exceptions;
using OfiFlow.Application.Identity;
using OfiFlow.Application.Identity.Commands.CloseSession;
using OfiFlow.Application.Identity.Queries.ListMySessions;
using OfiFlow.Application.Tests.Common;
using OfiFlow.Domain.Identity;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Tests.Identity;

/// <summary>
/// Listar y cerrar una sesión concreta (spec 016, bloque 3), sin base de datos: lo que se prueba aquí es qué le pide el caso de uso al servicio de tokens
/// (siempre la cuenta del token, nunca un dato de la petición) y qué sale hacia fuera. El comportamiento contra la base de datos está en Infrastructure.
/// </summary>
public class SessionHandlersTests
{
    private static readonly DateTime Now = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
    private const string ChromeWindows = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    private static ActiveSession Session(Guid? id = null, int lastActiveMinutesAgo = 5, string? userAgent = ChromeWindows, string? ip = "203.0.113.0") =>
        new(id ?? Guid.NewGuid(), Now.AddHours(-3), Now.AddMinutes(-lastActiveMinutesAgo), userAgent, ip);

    // ===== Listar =====

    [Fact]
    public async Task List_AsksForTheSessionsOfTheAccountInTheToken_AndNothingElse()
    {
        var userId = Guid.NewGuid();
        var tokens = new FakeTokenService();
        var handler = new ListMySessionsQueryHandler(tokens, new FakeCurrentUser(TenantRole.Technician, userId, Guid.NewGuid(), sessionId: Guid.NewGuid()));

        await handler.Handle(new ListMySessionsQuery(), CancellationToken.None);

        Assert.Equal([userId], tokens.UsersWhoseSessionsWereListed);
    }

    [Fact]
    public async Task List_MarksOnlyTheSessionOfThisTokenAsCurrent()
    {
        var mine = Guid.NewGuid();
        var tokens = new FakeTokenService { ActiveSessions = { Session(), Session(mine), Session() } };
        var handler = new ListMySessionsQueryHandler(tokens, new FakeCurrentUser(TenantRole.Owner, Guid.NewGuid(), Guid.NewGuid(), sessionId: mine));

        var sessions = await handler.Handle(new ListMySessionsQuery(), CancellationToken.None);

        Assert.Equal(3, sessions.Count);
        Assert.Equal(mine, Assert.Single(sessions, s => s.Current).Id);
    }

    [Fact]
    public async Task List_WhenTheTokenHasNoSession_NoneIsCurrent()
    {
        var tokens = new FakeTokenService { ActiveSessions = { Session(), Session() } };
        var handler = new ListMySessionsQueryHandler(tokens, new FakeCurrentUser(TenantRole.Owner, Guid.NewGuid(), Guid.NewGuid(), sessionId: null));

        var sessions = await handler.Handle(new ListMySessionsQuery(), CancellationToken.None);

        Assert.All(sessions, s => Assert.False(s.Current));
    }

    [Fact]
    public async Task List_PutsTheMostRecentlyActiveFirst_WhateverOrderTheServiceUsed()
    {
        var oldest = Guid.NewGuid();
        var newest = Guid.NewGuid();
        var middle = Guid.NewGuid();
        var tokens = new FakeTokenService
        {
            ActiveSessions = { Session(oldest, lastActiveMinutesAgo: 600), Session(newest, lastActiveMinutesAgo: 1), Session(middle, lastActiveMinutesAgo: 90) }
        };
        var handler = new ListMySessionsQueryHandler(tokens, new FakeCurrentUser(TenantRole.Owner, Guid.NewGuid()));

        var sessions = await handler.Handle(new ListMySessionsQuery(), CancellationToken.None);

        Assert.Equal([newest, middle, oldest], sessions.Select(s => s.Id));
    }

    [Fact]
    public async Task List_ShowsTheDeviceAsALabel_NeverTheRawBrowser()
    {
        const string hostile = "<script>alert('x')</script> Windows Chrome/120";
        var tokens = new FakeTokenService { ActiveSessions = { Session(userAgent: ChromeWindows), Session(userAgent: hostile), Session(userAgent: null) } };
        var handler = new ListMySessionsQueryHandler(tokens, new FakeCurrentUser(TenantRole.Owner, Guid.NewGuid()));

        var sessions = await handler.Handle(new ListMySessionsQuery(), CancellationToken.None);

        Assert.All(sessions.Take(2), s => Assert.Equal("Chrome en Windows", s.Device));
        Assert.Equal("Dispositivo desconocido", sessions[2].Device); // una sesión anterior a la spec 016: sin navegador
        var json = JsonSerializer.Serialize(sessions);
        Assert.DoesNotContain("script", json);
        Assert.DoesNotContain("Mozilla", json);
        Assert.DoesNotContain("AppleWebKit", json);
    }

    [Fact]
    public async Task List_PassesTheTruncatedNetworkAsIs_AndNullWhenThereIsNone()
    {
        var tokens = new FakeTokenService { ActiveSessions = { Session(ip: "203.0.113.0", lastActiveMinutesAgo: 1), Session(ip: null, lastActiveMinutesAgo: 2) } };
        var handler = new ListMySessionsQueryHandler(tokens, new FakeCurrentUser(TenantRole.Owner, Guid.NewGuid()));

        var sessions = await handler.Handle(new ListMySessionsQuery(), CancellationToken.None);

        Assert.Equal("203.0.113.0", sessions[0].ApproximateIp);
        Assert.Null(sessions[1].ApproximateIp);
    }

    [Fact]
    public async Task List_WithNoSessions_IsAnEmptyList()
    {
        var handler = new ListMySessionsQueryHandler(new FakeTokenService(), new FakeCurrentUser(TenantRole.Owner, Guid.NewGuid()));

        Assert.Empty(await handler.Handle(new ListMySessionsQuery(), CancellationToken.None));
    }

    [Fact]
    public void TheListDto_CannotCarryASecret_ByConstruction()
    {
        // R11: ni refresh token, ni hash, ni User-Agent crudo, ni IP completa. Si alguien añade un campo, este test lo señala y obliga a decidirlo.
        var properties = typeof(SessionDto).GetProperties().Select(p => p.Name).Where(n => n != "EqualityContract").Order();

        Assert.Equal(["ApproximateIp", "Current", "Device", "Id", "LastActiveAt", "StartedAt"], properties);
    }

    [Fact]
    public void TheListQuery_HasNoPropertiesToForge_TheAccountComesFromTheToken() =>
        Assert.Empty(typeof(ListMySessionsQuery).GetProperties().Where(p => p.DeclaringType == typeof(ListMySessionsQuery) && p.Name != "EqualityContract"));

    // ===== Cerrar una =====

    [Fact]
    public async Task Close_UsesTheAccountOfTheToken_AndTheIdOfTheRoute()
    {
        var userId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var tokens = new FakeTokenService();
        var handler = new CloseSessionCommandHandler(tokens, new FakeCurrentUser(TenantRole.Employee, userId, Guid.NewGuid(), sessionId: Guid.NewGuid()));

        var result = await handler.Handle(new CloseSessionCommand(sessionId), CancellationToken.None);

        Assert.Equal(Unit.Value, result);
        Assert.Equal((userId, sessionId), (tokens.SessionsClosed.Single().UserId, tokens.SessionsClosed.Single().SessionId));
    }

    [Fact]
    public async Task Close_TellsTheServiceWhetherItIsTheCurrentSession()
    {
        var mine = Guid.NewGuid();
        var tokens = new FakeTokenService();
        var handler = new CloseSessionCommandHandler(tokens, new FakeCurrentUser(TenantRole.Owner, Guid.NewGuid(), Guid.NewGuid(), sessionId: mine));

        await handler.Handle(new CloseSessionCommand(mine), CancellationToken.None);
        await handler.Handle(new CloseSessionCommand(Guid.NewGuid()), CancellationToken.None);

        Assert.Equal([true, false], tokens.SessionsClosed.Select(c => c.IsCurrent));
    }

    [Fact]
    public async Task Close_WhenThereIsNoSuchSession_IsTheSameNotFound_NoMatterWhy()
    {
        // R14: «no existe», «es de otra cuenta» y «ya estaba cerrada» llegan aquí igual (el servicio dice solo «no encontré una activa»), y el error es idéntico.
        var tokens = new FakeTokenService { CloseSessionFindsTheSession = false };
        var handler = new CloseSessionCommandHandler(tokens, new FakeCurrentUser(TenantRole.Owner, Guid.NewGuid(), Guid.NewGuid()));

        var first = await Assert.ThrowsAsync<NotFoundException>(() => handler.Handle(new CloseSessionCommand(Guid.NewGuid()), CancellationToken.None));
        var second = await Assert.ThrowsAsync<NotFoundException>(() => handler.Handle(new CloseSessionCommand(Guid.NewGuid()), CancellationToken.None));

        Assert.Equal(IdentityErrors.SessionNotFound, first.Code);
        Assert.Equal(first.Code, second.Code);
    }

    [Fact]
    public void TheCloseCommand_CarriesOnlyTheSessionId_ThereIsNoUserIdToForge() =>
        Assert.Equal(["SessionId"], typeof(CloseSessionCommand).GetProperties().Select(p => p.Name).Where(n => n != "EqualityContract"));
}
