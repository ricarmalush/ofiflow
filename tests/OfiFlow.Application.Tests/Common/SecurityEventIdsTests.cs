using System.Reflection;
using OfiFlow.Application.Common.Logging;

namespace OfiFlow.Application.Tests.Common;

/// <summary>
/// Los números de los eventos de seguridad son un contrato con quien analiza los registros (una alerta se escribe «avísame si sale el 1304»): no se repiten ni se reutilizan con otro
/// significado. Este test lo fija para TODOS, sin lista: un número duplicado, o fuera de las familias conocidas, falla aquí aunque ningún otro test lo note.
/// </summary>
public class SecurityEventIdsTests
{
    private static List<(string Name, int Id)> All() =>
        [.. typeof(SecurityEventIds).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(int))
            .Select(field => (field.Name, (int)field.GetRawConstantValue()!))];

    [Fact]
    public void EveryEventHasItsOwnNumber()
    {
        var events = All();

        Assert.True(events.Count > 50, "Control positivo: la lista de eventos se está leyendo de verdad.");
        var repeated = events.GroupBy(e => e.Id).Where(group => group.Count() > 1).Select(group => $"{group.Key}: {string.Join(", ", group.Select(e => e.Name))}").ToList();
        Assert.True(repeated.Count == 0, "Números de evento repetidos: " + string.Join("; ", repeated));
    }

    [Fact]
    public void EveryNumberBelongsToAKnownFamily()
    {
        // 10xx acceso y sesiones · 11xx límites · 12xx errores · 13xx denegaciones · 14xx equipo · 15xx segundo factor · 16xx plataforma.
        var outside = All().Where(e => e.Id / 100 is < 10 or > 16).Select(e => $"{e.Name}={e.Id}").ToList();

        Assert.True(outside.Count == 0, "Eventos fuera de las familias conocidas: " + string.Join(", ", outside));
    }

    [Fact]
    public void TheSecondFactorEvents_AreInTheirOwnFamily()
    {
        Assert.Equal(1304, SecurityEventIds.MfaEnrollmentRequiredDenied); // una denegación (13xx)
        Assert.Equal(1508, SecurityEventIds.MfaDisableDeniedForRole);     // del segundo factor (15xx)
        Assert.Equal(15, SecurityEventIds.MfaDisableDeniedForRole / 100);
    }
}
