# Tasks — 001-customer

**Spec relacionada:** [./spec.md](./spec.md)
**Estado general:** Pendiente

---

## Convención

- Cada tarea completable y verificable en una sesión (1-4h).
- Se marca `[x]` solo cuando cumple la Definición de "Done" (sección 86): código + reglas de negocio + validaciones + autorización + multi-tenancy comprobado + tests + probado manualmente.
- Orden de incrementos: Domain → Application → Infrastructure → API → Tests. No adelantar un bloque sin cerrar el anterior.

## Checklist

### Domain (`src/OfiFlow.Domain/Customers/`)
- [x] `CustomerType` (enum: `Person`, `Company`)
- [x] `Email` (Value Object, valida formato)
- [x] `PhoneNumber` (Value Object, valida formato)
- [x] `Customer` (AggregateRoot, `ITenantOwned`, `IAuditable`) con factory method `Customer.Create(...)` que aplica invariantes (Name obligatorio)
- [x] Método `Customer.UpdateContactInfo(...)` (encapsula la actualización, no setters públicos sueltos)
- [x] Unit tests: creación válida, `Name` vacío rechazado, `Email`/`PhoneNumber` con formato inválido rechazados (22/22 tests, incluye los 4 de `Common`)

### Application (`src/OfiFlow.Application/Customers/`)
- [x] `CreateCustomerCommand` + Handler + Validator (FluentValidation)
- [x] `UpdateCustomerCommand` + Handler + Validator
- [x] `DeleteCustomerCommand` + Handler (verifica que el Customer existe antes de borrar; el aislamiento de tenant real depende del Global Query Filter que se configura en el bloque de Infrastructure)
- [x] `GetCustomerQuery` + Handler + DTO
- [x] `GetCustomersQuery` + Handler + DTO (lista del tenant activo)
- [x] Unit tests de cada Handler (8/8, con `TestDbContext` de EF Core InMemory — no SQL Server real)
- [x] Plumbing añadido de paso: `IApplicationDbContext`, `ITenantContext`, `ValidationBehavior` (MediatR + FluentValidation), `NotFoundException`, `DependencyInjection.AddApplication()`

### Infrastructure (`src/OfiFlow.Infrastructure/Persistence/`)
- [x] `IEntityTypeConfiguration<Customer>` (mapeo EF Core: tipos, longitudes, conversión de Value Objects)
- [x] Registrar `Customer` en `ApplicationDbContext` (`DbSet<Customer>`)
- [x] Verificar que el Global Query Filter genérico (`ITenantOwned`) se aplica correctamente a `Customer` (test con InMemory — el test obligatorio contra SQL Server real sigue pendiente, ver Tests)
- [x] Primera migración de EF Core incluyendo la tabla `Customers`
- [x] Añadido de paso (necesario para que lo anterior funcione, no estaba desglosado): `ApplicationDbContext` real con Global Query Filter genérico por reflection sobre `ITenantOwned`, `TenantContext` (lee el claim `tenant_id` del JWT), `AuditableEntitySaveChangesInterceptor` (rellena `CreatedAt`/`UpdatedAt`), `ApplicationDbContextFactory` (design-time, para generar migraciones sin depender de Api todavía), `DependencyInjection.AddInfrastructure()`

### API (`src/OfiFlow.Api/`)
- [x] Endpoint `POST /api/v1/customers`
- [x] Endpoint `GET /api/v1/customers/{id}`
- [x] Endpoint `GET /api/v1/customers`
- [x] Endpoint `PUT /api/v1/customers/{id}`
- [x] Endpoint `DELETE /api/v1/customers/{id}`
- [x] Documentación OpenAPI de los 5 endpoints
- [x] **Desbloqueado y completado el 2026-09-21** tras `specs/002-tenancy-identity/` (login/JWT reales)

### Tests de integración (`tests/OfiFlow.Infrastructure.Tests`, `tests/OfiFlow.Api.Tests`)
- [x] Sanity check del Global Query Filter con EF Core InMemory (`ApplicationDbContextTenantFilterTests`)
- [x] **Test obligatorio verificado manualmente (2026-09-21) contra LocalDB real**, de punta a punta por HTTP: 2 tenants distintos, JWT reales, Tenant B no puede leer ni borrar un Customer de Tenant A (404 en ambos casos) — ver detalle en `specs/002-tenancy-identity/tasks.md`
- [ ] **Pendiente:** automatizar esta verificación como test de integración real (no manual) — bloqueado por la misma decisión LocalDB vs Testcontainers de ADR-006
- [x] Verificado manualmente: crear Customer vía API completa (JWT → Command → SQL Server) y recuperarlo
- [x] Verificado manualmente: listar solo devuelve los Customers del tenant activo

---

## Notas / bloqueos

- Domain, Application e Infrastructure: **completados** (2026-09-21). 31 tests en verde.
- **Bloque API pausado (2026-09-21):** `ITenantContext.TenantId` lee el claim `tenant_id` de un JWT que todavía no existe — no hay `Tenant`, `User`, `TenantUser`, login ni emisión de JWT. Construir los endpoints ahora los dejaría sin forma real de probarse por HTTP (funcionan en tests con `TenantContext` falso, pero `TenantContext` real lanzaría excepción en una petición real). Decisión: parar aquí y construir primero `specs/002-tenancy-identity/`. Se retoma este bloque API cuando esa spec esté implementada.
