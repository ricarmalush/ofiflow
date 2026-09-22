# Tasks — 003-job

**Spec relacionada:** [./spec.md](./spec.md)
**Estado general:** Pendiente

---

## Convención

- Cada tarea completable y verificable en una sesión (1-4h).
- Se marca `[x]` solo cuando cumple la Definición de "Done" (sección 86).
- Orden de incrementos: Domain → Application → Infrastructure → API → Tests.

## Checklist

### Domain (`src/OfiFlow.Domain/Jobs/`)
- [x] `JobStatus` (enum: `New, Pending, Scheduled, InProgress, Completed, Cancelled` — ya decidido en ADR-006)
- [x] `JobPriority` (enum: `Low, Normal, High, Urgent`)
- [x] `Job` (AggregateRoot, `ITenantOwned`, `IAuditable`) — factory `Job.Create(tenantId, customerId, title, description, priority)`
- [x] Métodos de transición con guard clauses: `Start()` (New→InProgress), `Complete()` (InProgress→Completed), `Cancel()` (cualquiera salvo Completed→Cancelled)
- [x] `AssignTo(tenantUserId)` / `Unassign()`
- [x] `UpdateDetails(title, description, priority)`
- [x] Unit tests: 14 tests nuevos (44/44 en Domain.Tests total)

### Application
- [x] Ampliar `IApplicationDbContext`: `DbSet<Job>`
- [x] `CreateJobCommand` + Handler + Validator — verifica que el `Customer` exista en el tenant activo
- [x] `UpdateJobCommand` + Handler + Validator
- [x] `StartJobCommand`, `CompleteJobCommand`, `CancelJobCommand` + Handlers (`InvalidOperationException` de Domain → `BusinessRuleException` de Application, para que el mapeo HTTP sea explícito y no dependa de un tipo genérico de .NET)
- [x] `AssignJobCommand` + Handler + Validator — verifica que el `TenantUserId` pertenezca al tenant activo
- [x] `GetJobQuery` + Handler + DTO
- [x] `GetJobsQuery` + Handler + DTO
- [x] Nueva excepción `BusinessRuleException` en `Common/Exceptions`
- [x] **Modificado `DeleteCustomerCommandHandler`**: comprueba Jobs activos antes de borrar, `BusinessRuleException` si los hay
- [x] Unit tests: 15 nuevos (30/30 en Application.Tests total), incluyendo 2 casos nuevos en `DeleteCustomerCommandHandlerTests`
- [x] Añadido de paso (necesario para no romper la solución): `DbSet<Job>` en el `ApplicationDbContext` real de Infrastructure y en `TestDbContext`

### Infrastructure
- [x] `JobConfiguration` (EF Core: `Status`/`Priority` como string, longitudes, índices en `CustomerId`/`AssignedTenantUserId`)
- [x] Registrar `Job` en `ApplicationDbContext` (hecho de paso en el bloque Application)
- [x] Migración `AddJobs`

### API (`src/OfiFlow.Api/`)
- [x] Endpoint `POST /api/v1/jobs`
- [x] Endpoint `GET /api/v1/jobs/{id}`
- [x] Endpoint `GET /api/v1/jobs`
- [x] Endpoint `PUT /api/v1/jobs/{id}`
- [x] Endpoint `POST /api/v1/jobs/{id}/start`
- [x] Endpoint `POST /api/v1/jobs/{id}/complete`
- [x] Endpoint `POST /api/v1/jobs/{id}/cancel`
- [x] Endpoint `POST /api/v1/jobs/{id}/assign`
- [x] `GlobalExceptionHandler` ampliado: `BusinessRuleException` → 400
- [x] Documentación OpenAPI de los 8 endpoints

### Tests
- [x] **Verificación manual end-to-end contra LocalDB real (2026-09-22):** crear Job, intentar borrar el Customer con el Job activo (400, mensaje claro), intentar completar un Job en `New` (400), iniciar → completar el Job, borrar el Customer ya sin Jobs activos (204) — el Job completado sigue existiendo tras borrar el Customer, tal como preveía la spec
- [ ] Test obligatorio de aislamiento de tenant para `Job` — pendiente de automatizar (misma decisión LocalDB vs Testcontainers de ADR-006 que en 001/002)

---

## Notas / bloqueos

- Ninguno — `Customer` y `TenantUser` (para asignación) ya existen y están probados.
- Esta spec **modifica** código ya "completado" de `specs/001-customer/tasks.md` (`DeleteCustomerCommandHandler`) — se anota aquí y allí para que quede trazable.
