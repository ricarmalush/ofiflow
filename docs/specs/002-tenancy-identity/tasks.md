# Tasks — 002-tenancy-identity

**Spec relacionada:** [./spec.md](./spec.md)
**Estado general:** Pendiente

---

## Convención

- Cada tarea completable y verificable en una sesión (1-4h).
- Se marca `[x]` solo cuando cumple la Definición de "Done" (sección 86).
- Orden de incrementos: Domain → Application → Infrastructure → API → Tests.

## Checklist

### Domain
- [x] **Refactor previo:** mover `Email` de `Domain/Customers/` a `Domain/Common/` (pasa a ser compartido entre `Customer` y `User`) — actualizado en `Customer.cs`, `CustomerConfiguration.cs`, `TestDbContext.cs`, `CustomerTests.cs`, Handlers de Application, y `EmailTests.cs` movido a `Domain.Tests/Common/`
- [x] `TenantRole` (enum: `Owner`, `Admin`, `Manager`, `Technician`, `Employee`) en `Domain/Tenancy/`
- [x] `Tenant` (AggregateRoot, `IAuditable`) en `Domain/Tenancy/` — factory `Tenant.Create(name)`
- [x] `TenantUser` (AggregateRoot, `ITenantOwned`, `IAuditable`) en `Domain/Tenancy/` — factories `TenantUser.CreateOwner(tenantId, userId)` y `TenantUser.Create(tenantId, userId, role)`
- [x] `User` (AggregateRoot, `IAuditable`, **sin** `ITenantOwned`) en `Domain/Identity/` — factory `User.Create(id, name, contactEmail)` (recibe el `id` en vez de generarlo, para que coincida con el `ApplicationUser` creado en la misma transacción — ADR-007)
- [x] Unit tests: `Tenant`, `TenantUser`, `User` + `Email` reubicado — 30/30 tests en Domain

### Application
- [x] Ampliar `IApplicationDbContext`: añadir `DbSet<Tenant>`, `DbSet<User>`, `DbSet<TenantUser>`
- [x] `IIdentityService` (abstracción, implementada en Infrastructure): `CreateUserAsync(id, email, password)`, `ValidateCredentialsAsync(email, password)`
- [x] `ITokenService` (implementada en Infrastructure): `IssueTokensAsync(userId, tenantId, role)`, `RotateRefreshTokenAsync(refreshToken)` (rotación + detección de reutilización, ADR-007)
- [x] `AuthResultDto` (AccessToken, RefreshToken, ExpiresAt)
- [x] `RegisterCommand` + Handler + Validator — crea `Tenant` + `User` + `ApplicationUser` (vía `IIdentityService`) + `TenantUser(Owner)`
- [x] `LoginCommand` + Handler + Validator — valida credenciales, busca `TenantUser` del usuario (**con `IgnoreQueryFilters()`**, ver nota abajo), emite tokens
- [x] `RefreshTokenCommand` + Handler — delega la rotación en `ITokenService`
- [x] Unit tests de cada Handler (15/15, con `TestDbContext` InMemory + `FakeIdentityService`/`FakeTokenService`)
- [x] Añadido de paso (necesario para no romper la solución): `IEntityTypeConfiguration` mínima para `Tenant`, `TenantUser` (con índice único `TenantId+UserId`) y `User` en Infrastructure, más los 3 `DbSet` en el `ApplicationDbContext` real

### Infrastructure
- [x] `ApplicationUser : IdentityUser<Guid>` en `Infrastructure/Identity/` (ADR-007) — **decisión tomada:** tratada como entidad EF normal, sin `UserManager`/`IdentityDbContext`, para poder confirmarla en la misma transacción que Tenant/User/TenantUser (ver Notas)
- [x] `RefreshToken` (entidad EF, no Domain) en `Infrastructure/Identity/` — `Id`, `UserId`, `TenantId`, `TokenHash`, `ExpiresAt`, `RevokedAt`
- [x] ~~Configurar ASP.NET Core Identity (`AddIdentityCore`)~~ — descartado a propósito, ver Notas
- [x] Implementación de `IIdentityService` sobre `PasswordHasher<ApplicationUser>` (sin `UserManager`)
- [x] Implementación de `ITokenService`: JWT (claims `sub`, `tenant_id`, `role`), rotación de `RefreshToken` con hash SHA-256 (no texto plano) y revocación en cadena si se reutiliza uno ya revocado
- [x] `IEntityTypeConfiguration` para `Tenant`, `TenantUser`, `User` (hechas de paso en el bloque Application), `ApplicationUser`, `RefreshToken`
- [x] Migración `AddTenancyAndIdentity`: `ApplicationUsers`, `RefreshTokens`, `Tenants`, `TenantUsers`, `Users`, con índices únicos (`NormalizedEmail`, `TokenHash`, `TenantId+UserId`)
- [x] Autenticación JWT Bearer registrada en `DependencyInjection.AddInfrastructure()` (falla explícitamente al arrancar si falta `Jwt:Secret`, en vez de usar una clave por defecto insegura)
- [x] Tests: `IdentityServiceTests` (3) + `TokenServiceTests` (3) + `ApplicationDbContextTenantFilterTests` (1) = 7/7 en Infrastructure.Tests

### API (`src/OfiFlow.Api/`)
- [x] `Program.cs`: `AddApplication()` + `AddInfrastructure()`, autenticación JWT, cadena de conexión LocalDB en `appsettings.Development.json`
- [x] Endpoint `POST /api/v1/auth/register` (anónimo)
- [x] Endpoint `POST /api/v1/auth/login` (anónimo)
- [x] Endpoint `POST /api/v1/auth/refresh` (anónimo)
- [x] `RequireAuthorization()` aplicado a los 5 endpoints de `Customer` (desbloqueando `specs/001-customer/tasks.md`)
- [x] Documentación OpenAPI (vía `WithName`/`WithSummary`/`WithTags`, `AddOpenApi()` ya expuesto en `/openapi`)
- [x] Añadido de paso (necesario para respuestas HTTP correctas): `GlobalExceptionHandler` — mapea `NotFoundException`→404, `ValidationException`→400, `IdentityOperationException`→400, en vez de dejar que cualquier excepción de negocio llegue como 500 genérico (sección 41 del prompt maestro)

### Tests de integración
- [x] Cubierto por `IdentityServiceTests`/`TokenServiceTests` (InMemory) — registro, login, rotación, revocación en cadena: todos verificados a nivel de servicio
- [x] **Verificación manual end-to-end contra LocalDB real (2026-09-21):** migraciones aplicadas, API levantada, y probado por HTTP con curl: registro de 2 tenants distintos, login, creación de Customer con JWT, y **aislamiento de tenant confirmado de verdad** — el Tenant B no ve, no puede leer ni puede borrar el Customer del Tenant A (404 en ambos casos), y el Customer de A sigue intacto después del intento de borrado de B
- [x] **Automatizado (2026-09-22, ADR-008):** `TenantIsolationIntegrationTests` (Customer + Job) contra LocalDB real con base de datos propia por ejecución — no cubre todavía el flujo HTTP completo con `WebApplicationFactory`, pero sí el mecanismo de aislamiento contra SQL Server real

---

## Notas / bloqueos

- **Decisión tomada (2026-09-21) sobre `ApplicationUser`:** en vez de `UserManager<ApplicationUser>` + `IdentityDbContext`/`IdentityUserContext` completos, `ApplicationUser` se trata como una entidad EF normal. `IIdentityService.CreateUserAsync` valida duplicados y genera el hash con `PasswordHasher<ApplicationUser>`, pero **no llama a `SaveChangesAsync`** — solo hace `Add()`. Así `RegisterCommandHandler` confirma `ApplicationUser` + `Tenant` + `User` + `TenantUser` en una única transacción real, resolviendo el riesgo de atomicidad anotado en el bloque Application. Coste aceptado: sin las funcionalidades de `UserManager` que no se usan todavía (confirmación de email, lockout automático, tokens de recuperación) — ya marcadas como LATER en la spec; se puede migrar a `UserManager` completo cuando esas features se construyan de verdad.
- **`IgnoreQueryFilters()` en `LoginCommandHandler`:** al hacer login todavía no hay tenant activo (es lo que se está determinando), así que la consulta a `TenantUsers` por `UserId` debe ignorar el Global Query Filter explícitamente — es exactamente el caso excepcional que ADR-002 preveía, documentado aquí para que quede auditado.
- Este bloque desbloquea el bloque API de `specs/001-customer/tasks.md`, que queda pausado hasta que `Program.cs` tenga autenticación real.
- Sigue pendiente (no bloquea esta spec): decisión LocalDB vs Testcontainers para los tests de integración contra SQL Server real (ADR-006).
