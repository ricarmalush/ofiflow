# Tasks — 004-security-hardening

**Spec relacionada:** [./spec.md](./spec.md)
**ADR relacionada:** [../../adr/ADR-009-seguridad-base.md](../../adr/ADR-009-seguridad-base.md)
**Estado general:** Implementada (2026-09-24). Pendiente solo la regresión de `TenantIsolationIntegrationTests`, que depende de Docker (ADR-008).

---

## Convención

- Cada tarea completable y verificable en una sesión (1-4h).
- Se marca `[x]` solo cuando cumple la Definición de "Done" (sección 86).
- Orden de incrementos: Domain → Application → Infrastructure → API → Tests. Aquí los guardarraíles de compilación van **primero**, porque protegen todo lo demás desde el minuto uno.
- El cambio pendiente de Testcontainers (ADR-008) se mantiene **fuera** de los commits de esta spec.

## Checklist

### Secretos (ADR-009 R8), hecho al aprobar la spec
- [x] `dotnet user-secrets init` en `OfiFlow.Api` y `Jwt:Secret` **nuevo** (32 bytes aleatorios) guardado ahí; el antiguo, que sigue en el historial de git, queda invalidado
- [x] `Jwt:Secret` eliminado de `appsettings.Development.json`
- [x] `AddInfrastructure()` falla al arrancar con un mensaje que indica el comando exacto si falta `Jwt:Secret`
- [x] README: instrucciones de configuración local
- [x] Verificado: la API arranca, y el registro y el login emiten JWT firmados con el secreto nuevo

### Guardarraíles de compilación
- [x] `Directory.Build.props`: `EF1002;NU1903;NU1904` como error y `NuGetAuditMode=all` (también dependencias transitivas)
- [x] Verificado: un `FromSqlRaw` con interpolación introducido a propósito rompe la compilación con `error EF1002`; 0 paquetes vulnerables

### Domain
- [x] Constantes de longitud en `Customer`, `Job`, `Tenant` y `User`, y `MaxLength` en los value objects `Email` y `PhoneNumber`
- [x] **Hallazgo nuevo:** `Email.Create`/`PhoneNumber.Create` incluían el valor recibido en el mensaje de la excepción (dato personal que acabaría en el log al registrar los 500). Ahora el mensaje es genérico.
- [x] `Email.IsValid` / `PhoneNumber.IsValid` públicos (longitud antes que regex), para que los Validators apliquen la misma regla que Domain
- [x] Unit tests: 10 nuevos (54/54 en Domain.Tests)

### Application
- [x] `PasswordRules` (8-128) en `Application/Identity/`
- [x] `ValidationRules.ValidEmail()` / `ValidPhone()` reutilizables; un único error si falla la longitud
- [x] `MaximumLength` en todos los Validators con texto (Create/Update de Customer y Job, Register, Login y RefreshToken)
- [x] **Hallazgo nuevo:** el teléfono no se validaba y el email se validaba de forma más permisiva que en Domain (`a@b` pasaba). Ambos acababan en 500. Ahora dan 400.
- [x] **Hallazgo nuevo:** `CustomerType` y `JobPriority` aceptaban cualquier número (`999` se guardaba como texto). Ahora hay `IsInEnum()`.
- [x] `SecurityEventIds`: catálogo de eventos de seguridad (base para la spec 006)
- [x] `LoginCommandHandler`: evento 1002 cuando las credenciales son válidas pero el usuario no pertenece a ningún tenant
- [x] Unit tests: 23 nuevos (53/53 en Application.Tests)

### Infrastructure
- [x] Configuraciones EF usando las constantes de Domain en lugar de números literales
- [x] Verificado: `dotnet ef migrations has-pending-model-changes` indica que no hay cambios en el modelo, así que el esquema no cambia
- [x] `TokenService`: evento 1003 por reutilización de un token revocado y 1004 por token rechazado, sin el valor ni el hash del token
- [x] `IdentityService`: evento 1001 por login fallido (motivo y UserId), **sin email** ni contraseña
- [x] Tests: 3 nuevos con `ListLogger`, que comprueban que el evento se registra y que no contiene token, email ni contraseña (10/10, excluyendo los 2 de Docker)

### API
- [x] `GlobalExceptionHandler`: evento 1201 (`LogError` con la excepción) en los 500; `traceId` en todas las respuestas de error para localizar la entrada en el log
- [x] **Hallazgo nuevo:** un JSON mal formado (`BadHttpRequestException`) acababa en 500. Ahora respeta su 400 con un mensaje genérico, sin nombres de tipos internos.
- [x] `RateLimiting`: `auth-login` 5/min, `auth-refresh` 20/min y `auth-register` 3/h por IP; 429 `ProblemDetails` con `Retry-After` y evento 1101
- [x] `AuthEndpoints`: `.RequireRateLimiting(...)` en los tres endpoints
- [x] `SecurityHeadersMiddleware` con `OnStarting`, para que las cabeceras sobrevivan también a las respuestas 500
- [x] `UseHsts()` (365 días) fuera de Development; orden del pipeline documentado en `Program.cs`
- [x] `RequestLogScopeMiddleware`: `SourceIp` en el scope de logging; consola con `FormatterName=simple` e `IncludeScopes`

### Tests
- [x] `OfiFlow.Api.Tests`: `Microsoft.AspNetCore.Mvc.Testing` y `OfiFlowApiFactory`, con un secreto JWT generado en cada ejecución (no depende de user-secrets ni de SQL Server)
- [x] **Arquitectura:** sin `FromSqlRaw`/`ExecuteSqlRaw` en `src/`
- [x] **Arquitectura:** `IgnoreQueryFilters()` solo en la lista blanca
- [x] **Arquitectura (multi-tenant):** ningún `IRequest` con propiedad `TenantId`
- [x] Verificado: los dos últimos detectan un Command con `TenantId` y un `IgnoreQueryFilters()` introducidos a propósito, con mensaje que indica regla y fichero
- [x] **Integración:** el 6.º login desde la misma IP da 429 con `Retry-After`
- [x] **Integración:** las 4 cabeceras de seguridad están presentes
- [x] **Integración:** una contraseña de 129 caracteres da 400; un JSON mal formado da 400 sin tipos internos; las respuestas de error llevan `traceId`
- [x] **Integración:** un 500 devuelve un mensaje genérico (sin el detalle interno), mantiene las cabeceras y queda registrado como `Error` con la excepción
- [x] Regresión: Domain 54, Application 53, Infrastructure 10 y Api 10, todos en verde (**127 tests**)
- [ ] Regresión: `TenantIsolationIntegrationTests`, pendiente de que Docker funcione (ADR-008). Se ejecutará en CI con la spec 005.

### Verificación manual (API real + LocalDB)
- [x] `Notes` de 2001 caracteres → 400; teléfono inválido → 400; `type: 999` → 400; enum como texto → 400 con `traceId`; cliente válido → 201
- [x] 6.º login incorrecto → 429; cabeceras presentes
- [x] Log: eventos 1001 con `SourceIp` y `TraceId`, y evento 1101; **0** emails o teléfonos en el log; **0** errores 500

---

## Notas / bloqueos

- Los valores del rate limiting (5/min, 20/min, 3/h) son iniciales; se revisan con datos reales en la Beta (sección 69).
- **La verificación manual encontró el fallo del JSON mal formado (500 en vez de 400)**, que los tests unitarios no cubrían. Ahora tiene su test de regresión.
- **Riesgo residual conocido:** los mensajes de `DbUpdateException` de SQL Server pueden incluir valores. Por ejemplo, en una violación de índice único ("The duplicate key value is (…)"), si dos registros con el mismo email llegan a la vez, el email acabaría en el log del 500. Es un caso raro, porque la validación previa lo evita casi siempre. Se revisará en la spec 006, al definir qué se registra de cada excepción.
- **Fuera de alcance (no es de seguridad):** la API **devuelve** los enums como texto (`"Person"`) pero **solo los acepta** como número. Es una incoherencia del contrato de la API. Añadir `JsonStringEnumConverter` es una decisión de diseño de la API, así que va al backlog.
