# Tasks — 008-rbac-authorization

**Spec relacionada:** [./spec.md](./spec.md)
**ADR relacionada:** [../../adr/ADR-012-autorizacion-rbac.md](../../adr/ADR-012-autorizacion-rbac.md)
**Estado general:** En curso (spec y matriz aprobadas 2026-10-06)

---

## Convención

- Cada tarea completable y verificable en una sesión (1-4h).
- Se marca `[x]` solo cuando cumple la Definición de "Done" (sección 86).
- Orden por defecto: Domain → Application → Infrastructure → API → Tests.
- Cada guardarraíl se verifica **rompiendo a propósito el código real** y viendo el test en rojo antes de restaurarlo (lección de la spec 007).
- ⚠️ Las tareas **[externo]** (push, PR, merge) requieren confirmación explícita en el momento de hacerlas.

## Checklist

### Bloque 1 — Domain (≈ 2 h)
- [x] `Permission` (enumerado) en `Domain/Tenancy`, con los 8 permisos de la matriz
- [x] `RolePermissions`: la tabla única rol → permisos
- [x] Test de la matriz: cada celda de la tabla de la spec, para los 5 roles y los 8 permisos
- [x] Test: todo rol del enumerado `TenantRole` aparece en la tabla (un rol nuevo sin permisos declarados falla)
- [x] Test: `Owner` tiene todos los permisos del enumerado

### Bloque 2 — Application (≈ 4 h)
- [x] `RequiresPermissionAttribute` y `AllowAnonymousRequestAttribute`
- [x] `ICurrentUser` (`UserId`, `Role`) en `Common/Abstractions`
- [x] `ForbiddenException` con el código `auth.forbidden`
- [x] `AuthorizationBehavior`: deniega sin permiso, sin declaración (fail-closed) o sin rol válido; las anónimas pasan
- [x] Registrarlo **antes** que `ValidationBehavior`
- [x] Declarar el permiso en las 13 operaciones autenticadas y marcar las 3 anónimas
- [x] Evento de seguridad `1301` (`AccessDenied`) en `SecurityEventIds` y su registro en el behavior
- [x] Tests con un `ICurrentUser` de prueba: cada rol frente a cada tipo de operación, sin declaración, rol ausente, y el orden respecto a la validación

### Bloque 3 — Infrastructure y Api (≈ 2 h)
- [x] `CurrentUser` en Infrastructure: lee los claims del usuario autenticado (comprobar el nombre real del claim del rol con un token emitido por `TokenService`) — hecho en el bloque 2
- [x] Registro en `AddInfrastructure` — hecho en el bloque 2
- [ ] `GlobalExceptionHandler`: `ForbiddenException` → 403 con `code`
- [x] Mensaje de `auth.forbidden` en `ErrorMessages.resx` (el test del diccionario ya exige que no falte) — hecho en el bloque 2
- [ ] Título del ProblemDetails de 403 en el diccionario

### Bloque 4 — Guardarraíl fail-closed (≈ 2 h)
- [ ] Test de arquitectura: toda operación (`IRequest`) de Application declara `[RequiresPermission]` o `[AllowAnonymousRequest]`, con mensaje que indica qué hacer
- [ ] Meta-tests con operaciones sintéticas mal declaradas (sin atributo; con los dos a la vez)
- [ ] Demostración con mutaciones del código real: quitar el atributo de una operación; desactivar el behavior; invertir un permiso en la tabla. Ver el rojo y restaurar con git

### Bloque 5 — Extremo a extremo por HTTP (≈ 3 h)
- [ ] Sembrar en la base de datos de prueba usuarios de cada rol (credenciales + `User` + `TenantUser`) en la empresa A, y hacer login por HTTP para obtener tokens reales
- [ ] Un test por rol y operación relevante contra la matriz (403 / éxito), con control positivo para cada denegación
- [ ] 403 antes que 400: datos inválidos sin permiso
- [ ] 403 idéntico para el `Id` de otra empresa y para uno inexistente
- [ ] Token sin claim de rol o con rol inválido → 403
- [ ] Las operaciones anónimas siguen funcionando
- [ ] Los tests de aislamiento de la spec 007 pasan sin cambios

### Documentación y cierre (≈ 1-2 h)
- [ ] ADR-012: de "Propuesto" a "Aceptado" (si no cambia durante la implementación)
- [ ] ADR-003 y ADR-009: marcar el behavior de autorización como hecho; añadir el evento `1301` al catálogo de R5
- [ ] `docs/README.md`: filas de la ADR-012 y de la spec 008
- [ ] Manual técnico (rama `docs/manual-tecnico`): actualizar la mención de "RBAC pendiente" en los capítulos 0, 2 y 4
- [ ] [externo] Push de la rama y PR contra `master`; los 4 checks en verde
- [ ] [externo] Merge (con confirmación explícita)

---

## Notas / bloqueos

- **Decisiones a confirmar antes de aprobar:** la matriz de permisos (sobre todo `Employee` y `Manager`) y que `Owner` y `Admin` sean idénticos por ahora.
- El rol viaja en el token: un cambio de rol no se nota hasta renovar la sesión (riesgo aceptado en ADR-012).
- Sin invitaciones, los usuarios de cada rol solo existen sembrados en los tests. La spec de invitar y gestionar usuarios es el siguiente paso natural.
- **Bloque 1 (2026-10-06):** `Permission` y `RolePermissions` en `Domain/Tenancy` (tabla inmutable, `FrozenDictionary`/`FrozenSet`; un rol fuera de la tabla no tiene ningún permiso). Las filas de `Owner` y `Admin` se listan una a una a propósito, sin derivarlas del enumerado: un permiso nuevo no se concede a nadie, tampoco al `Owner`, hasta decidirlo. `RolePermissionsTests` (46 tests): la matriz aprobada copiada literal en el test, aparte de la tabla de producción (40 celdas, una por rol y permiso), más 6 de coherencia (la matriz del test cubre todos los permisos y roles; todo rol tiene fila; `Owner` los tiene todos; ningún permiso queda sin rol; un rol fuera del enumerado no puede nada; los conjuntos no se pueden modificar desde fuera). Mutaciones, restauradas desde copia: (1) `Technician` recibe `CustomersDelete` → rojo en su celda; (2) `Owner` pierde `JobsCancel` → rojo en su celda y en "Owner tiene todos"; (3) se borra la fila de `Employee` → rojo en sus 4 celdas permitidas y en "todo rol tiene fila"; (4) permiso nuevo sin decidir quién lo tiene → rojo en 3 tests. Build con 0 avisos; suite completa en verde: Domain 100, Application 53, Api 61, Infrastructure 34.
- **Bloque 2 (2026-10-06):** en `Application`: `RequiresPermissionAttribute`, `AllowAnonymousRequestAttribute`, `ICurrentUser`, `ForbiddenException`, `AuthorizationBehavior` (registrado antes que `ValidationBehavior`; fail-closed: sin declarar, o anónima y con permiso a la vez, se deniega; todos los permisos repetidos se exigen) y el evento `1301`; las 16 operaciones declaradas (13 con permiso, 3 anónimas). **Cambio de orden respecto al plan:** `CurrentUser` (Infrastructure), su registro y el mensaje `auth.forbidden` del diccionario se hicieron aquí y no en el bloque 3, para que cada commit siga en verde (sin `ICurrentUser` registrado toda petición fallaba, y el test del diccionario exige un mensaje por cada código). Al bloque 3 le quedan el mapeo a 403 y el título del ProblemDetails. 35 tests nuevos en `Application.Tests` (11 del behavior con operaciones sintéticas mal y bien declaradas; 17 de la tabla operación → permiso de la spec, que además falla si aparece una operación sin listar; 7 con el pipeline real de MediatR, incluido "403 antes que 400" y su control) y 17 en `Infrastructure.Tests` sobre `CurrentUser` y el claim del rol. Mutaciones sobre el código real: Delete de cliente con otro permiso → rojo en la tabla y en el pipeline; validación registrada antes que la autorización → rojo en 3 tests; behavior fail-open → rojo; behavior que no comprueba el permiso → 8 en rojo; quitar el atributo a `GetJobsQuery` → rojo en la tabla.
- **Hallazgo 1 (lo cazó el test de extremo a extremo, el riesgo que la spec pedía verificar):** `JwtBearer` renombra por defecto los claims al validar (`role` → `ClaimTypes.Role`, `sub` → `NameIdentifier`), así que `CurrentUser` no encontraba el rol y **el Owner recibía 403 en todo**. Corregido con `options.MapInboundClaims = false` en `AddInfrastructure` (el `tenant_id` no tiene renombrado y por eso el aislamiento nunca lo notó). Mi primer test unitario del claim validaba con un manejador de JWT suelto y dio falsa seguridad; ahora usa la configuración **real** de `JwtBearer` que registra `AddInfrastructure`, y se comprobó que se pone en rojo con el renombrado activado. `TokenService` escribe ahora el claim como `OfiFlowClaimTypes.Role` (`"role"`) de forma explícita, con test que fija el nombre.
- **Hallazgo 2:** `CurrentUser` aceptaba el rol `"999"` (`Enum.TryParse` acepta cualquier número y, para un valor sin nombre, `ToString()` devuelve ese número); lo cazó su test. Ahora exige además que el valor esté definido.
- **Error de método (corregido):** restauré tres ficheros tras las mutaciones con `git checkout` cuando mis cambios aún no estaban commiteados, y perdí esas tres anotaciones; las reapliqué. Para el resto de mutaciones se usaron copias de seguridad. Lección: `git checkout` solo restaura lo ya commiteado.
