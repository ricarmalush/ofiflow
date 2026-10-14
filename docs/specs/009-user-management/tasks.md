# Tasks — 009-user-management

**Spec relacionada:** [./spec.md](./spec.md)
**ADR relacionada:** [../../adr/ADR-013-gestion-de-usuarios.md](../../adr/ADR-013-gestion-de-usuarios.md)
**Estado general:** Borrador (pendiente de aprobar la spec)

---

## Convención

- Cada tarea completable y verificable en una sesión (1-4h).
- Se marca `[x]` solo cuando cumple la Definición de "Done" (sección 86).
- Orden por defecto: Domain → Application → Infrastructure → API → Tests.
- Cada guardarraíl se verifica **rompiendo a propósito el código real** y viendo el test en rojo antes de restaurarlo. Restaurar con **copias de seguridad o tras commitear**, nunca con `git checkout` sobre cambios sin commitear (lección de la spec 008).
- ⚠️ Las tareas **[externo]** (push, PR, merge) requieren confirmación explícita en el momento de hacerlas.

## Regla de trabajo: manual y código en paralelo

El manual técnico y el código avanzan **a la vez**. Cada bloque termina con su tarea **[manual]**: los capítulos que describen lo que ese bloque cambió se actualizan **en el mismo bloque**, verificando cada nombre, ruta y cifra contra el repositorio. Un bloque no se da por hecho con su capítulo desfasado, y la spec no se cierra ni se pide el merge con el manual por detrás. Antes de empezar el bloque 1 el manual debe estar publicado en `master` (bloque 0).

## Checklist

### Bloque 0 — Manual al día antes de empezar (≈ 2-3 h, sin código)
- [x] [manual] Capítulo 10 (CI/CD y pipeline) revisado por ti y commiteado (6327b38)
- [ ] [externo] Publicar los capítulos 6–10: rebase de `docs/manual-cap6-identidad` sobre `master`, push, PR y los 4 checks en verde
- [ ] [externo] Merge (con confirmación explícita)
- [x] [manual] Capítulo 1 (poner en marcha): corregir lo que dice del arranque (LocalDB, Docker para los tests) y comprobarlo ejecutando los pasos desde cero — escrito y comprobado contra un SQL Server en Docker; pendiente de tu lectura y commit
- [x] [manual] Corregir los `tasks.md` desfasados de las specs 007 y 008 (PR y merge ya hechos) — hecho; además README raíz (requisitos y estado actual); pendiente de commit
- [ ] Rama `feature/009-user-management` desde `master` con la spec, las tareas y la ADR-013 ya aprobadas

### Bloque 1 — Domain (≈ 3 h)
- [ ] `Permission`: añadir `UsersRead` y `UsersManage`; `RolePermissions` ampliada según la spec (un permiso nuevo sin decidir no lo tiene nadie)
- [ ] Actualizar el test de matriz de `RolePermissions` con las dos filas nuevas (10 celdas) copiadas literal de la spec
- [ ] `ManageableRoles`: tabla única rol que actúa → roles que puede gestionar (inmutable)
- [ ] Test de matriz de `ManageableRoles`: las 25 celdas de la spec; todo rol del enumerado tiene fila; `Owner` no es gestionable por nadie
- [ ] `Invitation` (aggregate, `ITenantOwned`, `IAuditable`): creación con validaciones (rol distinto de `Owner` y definido, email, caducidad), `Accept`, `Revoke`, estado derivado; no se puede aceptar si no está pendiente
- [ ] Errores en `TenancyErrors`/`IdentityErrors`: `invitation.invalid`, `invitation.already_pending`, `invitation.limit_reached`, `invitation.not_found`, y el 403 de jerarquía (`tenant_user.role_not_manageable`, `tenant_user.cannot_manage_self`)
- [ ] Tests de `Invitation`: transiciones válidas e inválidas, caducidad, rol `Owner` rechazado
- [ ] [manual] Capítulo 8 (permisos y matriz con `Users.Read`/`Users.Manage`) y capítulo 3 (carpetas: dónde vive `Invitation` y `ManageableRoles`)

### Bloque 2 — Infrastructure: persistencia y credenciales (≈ 4 h)
- [ ] `InvitationConfiguration`: tamaños con las constantes de Domain, índice único por `TokenHash`, índice `(TenantId, Email)`, token de concurrencia (`rowversion`)
- [ ] Migración `AddInvitations` revisada a mano (el SQL generado, no solo el nombre)
- [ ] `IIdentityService.DeleteUserAsync` e implementación; `ITokenService`: revocar/borrar todos los refresh tokens de un usuario
- [ ] Tests de integración (Testcontainers): token duplicado imposible; concurrencia (dos actualizaciones de la misma invitación: una falla); borrar credenciales no deja huérfanos
- [ ] Clasificación del modelo de la spec 007: `Invitation` aparece como `ITenantOwned` y su filtro funciona (el test existente lo exige)
- [ ] [manual] Capítulo 5 (multi-tenancy): `Invitation` como entidad de la empresa y el nuevo uso justificado de `IgnoreQueryFilters()`; capítulo 6: borrado de credenciales

### Bloque 3 — Application: invitaciones (≈ 5 h)
- [ ] Generación del token (32 bytes aleatorios) y hash SHA-256 en un único sitio, compartido con el refresh token si tiene sentido, con test de longitud y no repetición
- [ ] `InviteUserCommand` (+ validador, + límites R5, + email con cuenta existente) [`Users.Manage`]
- [ ] `GetInvitationsQuery` (sin token ni hash en el DTO) [`Users.Manage`]
- [ ] `RevokeInvitationCommand` [`Users.Manage`]
- [ ] `AcceptInvitationCommand` anónima: búsqueda por hash con `IgnoreQueryFilters()` (lista blanca con motivo), respuesta idéntica en los cuatro casos de fallo, transacción única, manejo de la concurrencia, el token no se consume si la validación falla
- [ ] Declarar permiso o marca anónima en las operaciones nuevas; el test de arquitectura de la spec 008 sigue en verde
- [ ] Tests con dobles: cada rama de las reglas R1–R8
- [ ] [manual] Capítulo 6 (identidad): invitaciones, token de un solo uso, aceptación atómica y respuesta idéntica en los fallos; capítulo 7 si hay códigos de error nuevos

### Bloque 4 — Application: miembros y jerarquía (≈ 5 h)
- [ ] `GetUsersQuery` (join `TenantUsers` ↔ `Users`; lista blanca de acceso a `Users`) [`Users.Read`]
- [ ] `ChangeUserRoleCommand` aplicando R9–R12 con `ManageableRoles` [`Users.Manage`]
- [ ] `RemoveUserCommand` aplicando R14 (membresía, refresh tokens, desasignar trabajos no terminados, borrar credenciales y `User`) en una transacción [`Users.Manage`]
- [ ] Tests con dobles: cada celda de jerarquía, autogestión, objetivo `Owner`, `Id` de otra empresa ≡ inexistente, trabajos desasignados y terminados intactos
- [ ] [manual] Capítulo 8: jerarquía de roles (`ManageableRoles`) y sus reglas, con la tabla de la spec

### Bloque 5 — Revalidación por petición (≈ 3 h)
- [ ] Puerto para leer la pertenencia actual por `(TenantId, UserId)` y su implementación (una consulta por el índice único)
- [ ] `AuthorizationBehavior`: usa el rol de la base de datos, 403 sin pertenencia, evento `1302`; sigue antes de la validación
- [ ] Actualizar los tests de la spec 008 afectados, **de forma explícita** (el behavior ya consulta la base de datos) y anotarlo
- [ ] Mutaciones: ignorar la pertenencia → rojo; usar el rol del token en vez del de la base de datos → rojo; mover el behavior detrás de la validación → rojo
- [ ] [manual] Capítulo 8 (autorización: el rol sale de la base de datos y ya no del token; actualizar lo que decía de "rol desfasado") y capítulo 4 (la petición de principio a fin: el paso nuevo del pipeline)

### Bloque 6 — API, rate limiting y eventos (≈ 3 h)
- [ ] Endpoints `/api/v1/users` e `/api/v1/invitations`; la aceptación bajo `/api/v1/auth` o `/invitations/accept`, anónima
- [ ] Política de rate limiting de la aceptación (D7) y su prueba (11.ª petición → 429)
- [ ] Eventos `1302`, `1401`–`1406` en `SecurityEventIds` y su registro (solo Ids, rol, operación)
- [ ] Mapeo del 403 de jerarquía en `GlobalExceptionHandler` y mensajes/títulos en `ErrorMessages.resx` (el test del diccionario los exige)
- [ ] [manual] Capítulo 8: tablas de límites de peticiones y de eventos de seguridad con los valores nuevos; capítulo 7: tabla de códigos de error y número de entradas del diccionario

### Bloque 7 — Extremo a extremo por HTTP (≈ 5 h)
- [ ] Ciclo completo: el `Owner` invita, la persona acepta, hace login y actúa con su rol (sin sembrar datos: **primera vez que los 5 roles nacen por la API**)
- [ ] Hacer que `RolesFixture` de la spec 008 cree sus usuarios por la API en vez de sembrarlos, o decidir y anotar por qué se mantiene
- [ ] Un test por cada criterio de aceptación (token, jerarquía, quitar, revalidar, aislamiento), cada denegación con su control positivo
- [ ] El token en claro no aparece en la base de datos ni en el log; ningún dato personal en los eventos nuevos
- [ ] Concurrencia: dos aceptaciones simultáneas, exactamente una gana
- [ ] Mutaciones sobre el código real: la jerarquía permite a `Admin` tocar `Admin`; `Owner` gestionable; quitar sin revocar refresh tokens; aceptar sin marcar usada; respuesta distinta para token caducado vs. desconocido
- [ ] [manual] Capítulo 9 (tests): nueva familia de tests y nuevas cifras; capítulo 8: mapa de amenazas con las filas nuevas (escalada de privilegios, token de invitación, enumeración, baja con token vivo) y "Controles sin test propio" si queda alguno

### Documentación y cierre (≈ 2-3 h)
- [ ] ADR-013: de "Propuesto" a "Aceptado" con nota de estado de lo que aclaró la implementación
- [ ] ADR-012 y ADR-009: marcar cerrados los NEXT de `Users.Manage` y de la revalidación por petición; ADR-012, riesgo del rol desfasado resuelto
- [ ] `docs/README.md`: filas de la ADR-013 y de la spec 009
- [ ] `OfiFlow.Api.http`: peticiones nuevas del ciclo de invitación
- [ ] [manual] Revisión final de los capítulos 0, 5, 6, 7, 8, 9, 10 y 11 contra el repositorio (cifras de tests, códigos de error, eventos, límites conocidos) y capítulo 12 (glosario) con los términos nuevos
- [ ] [externo] Push de la rama y PR contra `master`; los 4 checks en verde
- [ ] [externo] Merge (con confirmación explícita)

---

## Notas / bloqueos

- **Decisiones D1–D7** de la spec a confirmar antes de empezar el bloque 1 (sobre todo D1, D2 y D3).
- **Estimación total:** ≈ 30 h de código y ≈ 8 h de manual (repartidas por bloques) en 8 bloques + cierre. Es la spec más grande hasta ahora; se puede partir en dos PRs si prefieres (bloques 1-4 + 6-7 por un lado, revalidación por petición por otro), a costa de dejar unas semanas el riesgo del rol desfasado.
- **Pendiente de decidir en el plan:** purga de invitaciones aceptadas, caducadas o revocadas (guardan el email).
- **Orden de ramas:** el bloque 0 publica primero el manual (caps. 6–10) en `master`; después la spec 009 sale de `master` con el manual ya al día, y cada bloque lleva sus capítulos en la misma rama.
