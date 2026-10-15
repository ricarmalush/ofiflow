using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using OfiFlow.Api.Endpoints;

namespace OfiFlow.Api.Common;

/// <summary>
/// Declara en el documento OpenAPI que las operaciones usan un token Bearer (JWT). Sin esto, la
/// pantalla de Swagger no tiene el botón "Authorize" y no se puede probar ninguna ruta protegida
/// desde el navegador. Solo describe la API: no la protege ni cambia su comportamiento.
/// Las rutas de /auth son anónimas, así que no se marcan (no muestran el candado).
/// </summary>
internal sealed class BearerSecuritySchemeTransformer : IOpenApiDocumentTransformer
{
    private const string SchemeName = "Bearer";
    private const string AnonymousPrefix = AuthEndpoints.Route;

    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>
        {
            [SchemeName] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                In = ParameterLocation.Header,
                Description = "Token de acceso que devuelve /api/v1/auth/login (solo el token, sin la palabra Bearer)."
            }
        };

        foreach (var (path, item) in document.Paths)
        {
            if (path.StartsWith(AnonymousPrefix, StringComparison.Ordinal) || item.Operations is null)
            {
                continue;
            }

            foreach (var operation in item.Operations.Values)
            {
                operation.Security ??= [];
                operation.Security.Add(new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference(SchemeName, document)] = []
                });
            }
        }

        return Task.CompletedTask;
    }
}
