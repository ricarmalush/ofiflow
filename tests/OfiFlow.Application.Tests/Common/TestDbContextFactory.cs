using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;

namespace OfiFlow.Application.Tests.Common;

public static class TestDbContextFactory
{
    // Las opciones de cada contexto creado, para poder abrir otro sobre la MISMA base de datos en memoria.
    private static readonly ConditionalWeakTable<TestDbContext, DbContextOptions<TestDbContext>> OptionsOf = new();

    /// <summary>Otro contexto sobre la MISMA base de datos en memoria: es «otra petición» que ve y escribe lo mismo (para probar carreras).</summary>
    public static TestDbContext CreateSharing(TestDbContext other) => new(OptionsOf.TryGetValue(other, out var options)
        ? options
        : throw new InvalidOperationException("Este contexto no se creó con TestDbContextFactory.Create."));

    public static TestDbContext Create()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var context = new TestDbContext(options);
        OptionsOf.Add(context, options);

        return context;
    }
}
