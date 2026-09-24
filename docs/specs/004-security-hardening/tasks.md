# Tasks — 004-security-hardening

**Spec relacionada:** [./spec.md](./spec.md)
**ADR relacionada:** [../../adr/ADR-009-seguridad-base.md](../../adr/ADR-009-seguridad-base.md)
**Estado general:** Pendiente (spec en Borrador, pendiente de aprobación)

---

## Convención

- Cada tarea completable y verificable en una sesión (1-4h).
- Se marca `[x]` solo cuando cumple la Definición de "Done" (sección 86).
- Orden de incrementos: Domain → Application → Infrastructure → API → Tests. Aquí los guardarraíles de compilación van **primero**, porque protegen todo lo demás desde el minuto uno.
- **Prerrequisito:** cerrar y commitear antes el cambio pendiente de Testcontainers (ADR-008) para no mezclar ambos trabajos en el mismo commit.

## Checklist

### Guardarraíles de compilación (≈ 0,5 h)
- [ ] `Directory.Build.props`: `<WarningsAsErrors>EF1002;NU1903;NU1904</WarningsAsErrors>` (ADR-009 R1, R7)
- [ ] Verificar que la solución sigue compilando con 0 errores. Si NuGetAudit detecta alguna vulnerabilidad existente, actualizar el paquete, no silenciar el aviso.

### Domain (≈ 0,5 h)
- [ ] Constantes de longitud en `Customer`, `Job`, `Tenant` y `User` (`NameMaxLength`, etc.), con los mismos valores que ya tienen las configuraciones EF
- [ ] Sin cambios de comportamiento: los tests de Domain existentes siguen en verde

### Application (≈ 2 h)
- [ ] `PasswordRules` (`MinLength = 8`, `MaxLength = 128`) en `Application/Identity/`
- [ ] `MaximumLength` en todos los Validators con texto:
  - `CreateCustomerCommandValidator`
  - `UpdateCustomerCommandValidator`
  - `CreateJobCommandValidator`
  - `UpdateJobCommandValidator`
  - `RegisterCommandValidator` (incluye contraseña 8-128)
  - `LoginCommandValidator` (email ≤ 320, contraseña ≤ 128)
  - `RefreshTokenCommandValidator` (el token de 64 bytes en base64 ocupa 88 caracteres; límite holgado de 200)
- [ ] Unit tests: un caso "longitud máxima + 1 → inválido" y otro "longitud máxima exacta → válido" por campo con límite

### Infrastructure (≈ 1 h)
- [ ] Configuraciones EF (`CustomerConfiguration`, `JobConfiguration`, `TenantConfiguration`, `UserConfiguration`) usando las constantes de Domain en lugar de números literales
- [ ] Verificar que **no** se genera migración: `dotnet ef migrations add Check --no-build` no debe producir diferencias. Si las produce, algún valor no coincide y hay que corregirlo; no se commitea esa migración.
- [ ] `TokenService`: `ILogger` → `LogWarning` al detectar la reutilización de un refresh token revocado (`UserId`, `TenantId`; **sin** el valor del token)
- [ ] `IdentityService` / `LoginCommandHandler`: `LogWarning` en login fallido, sin email en claro ni contraseña. La IP la añade el scope de logging de la API.

### API (≈ 2-3 h)
- [ ] `GlobalExceptionHandler`: inyectar `ILogger`; `LogError` con la excepción en los 500 y `LogInformation` sin datos del cuerpo en los 4xx de negocio
- [ ] Rate limiting en `Program.cs` o en `Common/RateLimitingExtensions.cs`: políticas `auth-login` (5/min), `auth-refresh` (20/min) y `auth-register` (3/hora), `FixedWindowLimiter` por IP, `RejectionStatusCode = 429` y respuesta `ProblemDetails` en `OnRejected` con `LogWarning`
- [ ] `AuthEndpoints`: `.RequireRateLimiting(...)` en cada uno de los tres endpoints
- [ ] Middleware `SecurityHeadersMiddleware` en `Common/`, con las 4 cabeceras de ADR-009 R6
- [ ] `Program.cs`: `UseHsts()` fuera de Development; orden del pipeline: ExceptionHandler → HSTS/HTTPS → SecurityHeaders → RateLimiter → Authentication → Authorization
- [ ] Scope de logging con la IP de la petición (`RemoteIpAddress`) para que los `LogWarning` de Infrastructure la incluyan sin tener que pasarla por Application

### Tests (≈ 2-3 h)
- [ ] `OfiFlow.Api.Tests`: añadir `Microsoft.AspNetCore.Mvc.Testing` y referencias a los proyectos de `src/`
- [ ] **Arquitectura:** ningún `.cs` de `src/` contiene `FromSqlRaw` ni `ExecuteSqlRaw`
- [ ] **Arquitectura:** `IgnoreQueryFilters()` solo aparece en la lista blanca (`LoginCommandHandler.cs`, `TokenService.cs`); el mensaje de fallo indica el fichero infractor
- [ ] **Arquitectura (multi-tenant):** ningún tipo del ensamblado Application que implemente `IRequest`/`IRequest<T>` tiene una propiedad `TenantId`
- [ ] **Integración (WebApplicationFactory):** la 6.ª petición a `/login` desde la misma IP en menos de un minuto devuelve 429 (con cuerpo inválido para no tocar la BD)
- [ ] **Integración:** una respuesta cualquiera incluye las 4 cabeceras de seguridad
- [ ] **Integración:** un campo demasiado largo devuelve 400 y no 500 (por ejemplo, `/register` con contraseña de 129 caracteres)
- [ ] Regresión: todas las suites existentes en verde (Domain, Application, Infrastructure, incluido `TenantIsolationIntegrationTests`)

### Verificación manual (≈ 0,5 h)
- [ ] `dotnet run` y comprobar con curl: 429 en el 6.º login, cabeceras presentes, 400 con un `Notes` de 2001 caracteres
- [ ] Revisar la salida del log tras un login fallido y tras un 500 forzado: la excepción aparece en el 500, y no aparecen ni email, ni contraseña, ni token

---

## Notas / bloqueos

- **Decisión pendiente del autor (ADR-009 R8):** secreto JWT de desarrollo commiteado. Si se elige `dotnet user-secrets`, añadir aquí una tarea: mover el secreto, **generar uno nuevo** (el actual ya está en el historial de git) y actualizar el README con el comando de configuración local.
- Los valores del rate limiting (5/min, 20/min, 3/h) son iniciales; se revisan con datos reales en la Beta (sección 69).
- Estimación total: ≈ 8-12 h (unas 2-3 sesiones de 4 h).
