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
- [x] Test de clasificación: toda entidad mapeada es `ITenantOwned` o está en la lista de globales (`Tenant`, `User`, `ApplicationUser`, `RefreshToken`, cada una con su motivo en el propio test)
- [x] Test: toda entidad `ITenantOwned` tiene el filtro aplicado en el modelo de EF Core
- [x] Test: una entidad con propiedad `TenantId` sin `ITenantOwned` solo se admite si está en la lista de globales
- [x] Demostrar que los tests fallan: meta-tests permanentes sobre un modelo con entidades mal clasificadas a propósito, más dos mutaciones del código real (ver "Notas")

### Bloque 2 — Guardarraíles de código fuente (≈ 1-2 h)
- [x] `IgnoreQueryFilters`: lista blanca por ruta relativa en vez de por nombre de fichero
- [x] Test de arquitectura: `Users` y `Tenants` (y, por coherencia, `ApplicationUsers` y `RefreshTokens`) solo se leen en la lista blanca; crear con `.Add` se admite en cualquier sitio
- [x] Demostrar el rojo con ficheros temporales que incumplen las reglas y retirarlos, más meta-tests permanentes con ficheros sintéticos

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
- **Bloque 1 (2026-10-06):** reglas en `TenantModelRules.cs` y tests en `TenantModelClassificationTests.cs` (`OfiFlow.Infrastructure.Tests/Persistence`), sobre el modelo de EF Core con el proveedor en memoria (sin Docker). Además de las 4 reglas y un test que fija el conjunto actual de entidades de empresa (`Customer`, `Job`, `TenantUser`, para que nunca pasen en vacío), hay 5 *meta-tests* que aplican las reglas a un modelo con entidades mal clasificadas y exigen que las detecten. Mutaciones sobre el código real, restauradas con git: (1) desactivar `ApplyTenantQueryFilters` → rojo en "filtro aplicado" con las 3 entidades; (2) quitar `ITenantOwned` de `Job` → rojo en 3 tests, con el mensaje que indica qué hacer. Build: 0 avisos, 0 errores. Suite sin Docker: Domain 54, Application 53, Api 23, Infrastructure 20, todo en verde.
- **Bloque 2 (2026-10-06):** la lógica de las dos reglas está en `TenantAccessRules.cs` (funciones puras: reciben ficheros, devuelven incumplimientos) y `SourceCode` ahora devuelve rutas siempre con `/` (misma comparación en Windows y en el runner de Linux). `IgnoreQueryFilters` se compara por ruta relativa completa. Regla nueva: ningún fichero de `src` puede leer `Users`, `Tenants`, `ApplicationUsers` ni `RefreshTokens` fuera de una lista blanca (hoy: `IdentityService.cs` para `ApplicationUsers` y `TokenService.cs` para `RefreshTokens`; `Users` y `Tenants` sin ningún fichero permitido); `.Add(` y `.AddAsync(` se admiten en cualquier sitio y los comentarios se ignoran. Ampliada a `ApplicationUsers` y `RefreshTokens` respecto a la spec, que solo nombraba `Users` y `Tenants`, porque son las otras dos entidades globales. 16 tests nuevos en `OfiFlow.Api.Tests` (15 meta-tests con ficheros sintéticos, incluido el mismo nombre en otra ruta, la lectura en varias líneas y el reporte de la línea exacta, más el test sobre el código real). Demostrado en rojo con dos ficheros temporales reales (`TempLeak.cs` leyendo `Users`; `Jobs/TokenService.cs` con `IgnoreQueryFilters`), retirados después. Limitación conocida: el escaneo es por texto; una consulta a `Users` partida de forma rara o por otra variable que no sea un `DbSet` con ese nombre no se detecta.
- La spec parte de los puntos débiles identificados en el capítulo 5 del manual técnico (puntos 1, 2, 3 y 6). Los puntos 4 y 5 (revalidación por petición y login con varias empresas) quedan en NEXT.
