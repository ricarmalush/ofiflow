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
- [ ] `Permission` (enumerado) en `Domain/Tenancy`, con los 8 permisos de la matriz
- [ ] `RolePermissions`: la tabla única rol → permisos
- [ ] Test de la matriz: cada celda de la tabla de la spec, para los 5 roles y los 8 permisos
- [ ] Test: todo rol del enumerado `TenantRole` aparece en la tabla (un rol nuevo sin permisos declarados falla)
- [ ] Test: `Owner` tiene todos los permisos del enumerado

### Bloque 2 — Application (≈ 4 h)
- [ ] `RequiresPermissionAttribute` y `AllowAnonymousRequestAttribute`
- [ ] `ICurrentUser` (`UserId`, `Role`) en `Common/Abstractions`
- [ ] `ForbiddenException` con el código `auth.forbidden`
- [ ] `AuthorizationBehavior`: deniega sin permiso, sin declaración (fail-closed) o sin rol válido; las anónimas pasan
- [ ] Registrarlo **antes** que `ValidationBehavior`
- [ ] Declarar el permiso en las 13 operaciones autenticadas y marcar las 3 anónimas
- [ ] Evento de seguridad `1301` (`AccessDenied`) en `SecurityEventIds` y su registro en el behavior
- [ ] Tests con un `ICurrentUser` de prueba: cada rol frente a cada tipo de operación, sin declaración, rol ausente, y el orden respecto a la validación

### Bloque 3 — Infrastructure y Api (≈ 2 h)
- [ ] `CurrentUser` en Infrastructure: lee los claims del usuario autenticado (comprobar el nombre real del claim del rol con un token emitido por `TokenService`)
- [ ] Registro en `AddInfrastructure`
- [ ] `GlobalExceptionHandler`: `ForbiddenException` → 403 con `code`
- [ ] Mensaje de `auth.forbidden` en `ErrorMessages.resx` (el test del diccionario ya exige que no falte)
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
