# Tasks — 007-tenant-isolation-hardening

**Spec relacionada:** [./spec.md](./spec.md)
**Estado general:** En curso (spec aprobada 2026-10-06)

---

## Convención

- Cada tarea completable y verificable en una sesión (1-4h).
- Se marca `[x]` solo cuando cumple la Definición de "Done" (sección 86).
- Esta spec añade tests y guardarraíles. El código de producción solo cambia si un test descubre un fallo real; en ese caso se anota aquí como hallazgo.
- ⚠️ Las tareas marcadas **[externo]** (push, PR, merge) requieren confirmación explícita en el momento de hacerlas.

## Checklist

### Bloque 1 — Guardarraíles sobre el modelo (≈ 2 h)
- [ ] Test de clasificación: toda entidad mapeada es `ITenantOwned` o está en la lista de globales (`Tenant`, `User`, `ApplicationUser`, `RefreshToken`, cada una con su motivo en el propio test)
- [ ] Test: toda entidad `ITenantOwned` tiene el filtro aplicado en el modelo de EF Core
- [ ] Test: una entidad con propiedad `TenantId` sin `ITenantOwned` solo se admite si está en la lista de globales
- [ ] Demostrar que los tests fallan: añadir una entidad de prueba sin clasificar, ver el rojo con el mensaje claro, y retirarla (anotar el resultado en "Notas")

### Bloque 2 — Guardarraíles de código fuente (≈ 1-2 h)
- [ ] `IgnoreQueryFilters`: lista blanca por ruta relativa en vez de por nombre de fichero
- [ ] Test de arquitectura: `Users` y `Tenants` solo se leen/escriben en la lista blanca (hoy `RegisterCommandHandler.cs`, solo escritura)
- [ ] Demostrar el rojo con un fichero temporal que lea `dbContext.Users` y retirarlo

### Bloque 3 — Escritura entre empresas contra SQL Server real (≈ 3 h)
- [ ] `CrossTenantWriteIsolationTests` en `OfiFlow.Infrastructure.Tests` con los handlers reales y el filtro real: `UpdateJob`, `StartJob`, `CompleteJob`, `CancelJob`, `AssignJob` con un `Id` ajeno → `job.not_found`; el trabajo no cambia
- [ ] Ídem para `UpdateCustomer` y `DeleteCustomer` → `customer.not_found`; el cliente sigue intacto
- [ ] `AssignJob` de un trabajo propio a un `TenantUser` ajeno → `tenant_user.not_found`
- [ ] Lectura: un `TenantUser` de A no es visible desde B (ampliar `TenantIsolationIntegrationTests`)

### Bloque 4 — Extremo a extremo por HTTP (≈ 3-4 h)
- [ ] `OfiFlow.Api.Tests`: añadir Testcontainers y una factoría que use la base de datos del contenedor (con migraciones) y un secreto JWT efímero
- [ ] Preparación: registrar y hacer login de la empresa A y la B **una sola vez** por fixture (2 registros, dentro del límite de 3/hora por IP)
- [ ] A crea un cliente y un trabajo; B los pide por `Id` (GET, PUT, DELETE, `start`/`complete`/`cancel`/`assign`) → 404 con el mismo `code` que un `Id` inexistente
- [ ] A sigue viendo sus datos intactos tras los intentos de B
- [ ] Sin token → 401 en esas rutas
- [ ] Medir cuánto añade al tiempo de CI y anotarlo

### Documentación y cierre (≈ 1 h)
- [ ] Corregir el comentario obsoleto de `ApplicationDbContextTenantFilterTests`
- [ ] Nota de revisión en ADR-009 R2 (toda entidad mapeada está clasificada; lista blanca por ruta)
- [ ] Actualizar el capítulo 5 del manual: marcar como cerrados los puntos débiles 1, 2, 3 y 6, y la fila "sin control" del mapa de amenazas
- [ ] `docs/README.md`: fila de la spec en "Implementada"
- [ ] [externo] Push de la rama y PR contra `master`; los 4 checks en verde (`build-and-test`, `gitleaks`, `analyze`, `zap-scan`)
- [ ] [externo] Merge (con confirmación explícita)

---

## Notas / bloqueos

- Hallazgos que hagan cambiar código de producción: _(ninguno todavía)_
- La spec parte de los puntos débiles identificados en el capítulo 5 del manual técnico (puntos 1, 2, 3 y 6). Los puntos 4 y 5 (revalidación por petición y login con varias empresas) quedan en NEXT.
