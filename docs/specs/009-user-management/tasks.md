# Tasks — 009-user-management

**Spec relacionada:** [./spec.md](./spec.md)
**ADR relacionada:** [../../adr/ADR-013-gestion-de-usuarios.md](../../adr/ADR-013-gestion-de-usuarios.md)
**Estado general:** En curso (spec aprobada 2026-10-15). Bloques 1, 2 y 3 hechos.

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
- [x] Spec, tareas y ADR-013 aprobadas (2026-10-15); se trabaja en la rama `spec/009-user-management`, ya actualizada con `master`

### Bloque 1 — Domain (≈ 3 h)
- [x] `Permission`: añadir `UsersRead` y `UsersManage`; `RolePermissions` ampliada según la spec (un permiso nuevo sin decidir no lo tiene nadie)
- [x] Actualizar el test de matriz de `RolePermissions` con las dos filas nuevas (10 celdas) copiadas literal de la spec
- [x] `ManageableRoles`: tabla única rol que actúa → roles que puede gestionar (inmutable)
- [x] Test de matriz de `ManageableRoles`: las 25 celdas de la spec; todo rol del enumerado tiene fila; `Owner` no es gestionable por nadie
- [x] `Invitation` (aggregate, `ITenantOwned`, `IAuditable`): creación con validaciones (rol distinto de `Owner` y definido, email, caducidad), `Accept`, `Revoke`, estado derivado; no se puede aceptar si no está pendiente
- [x] Errores en `TenancyErrors`/`IdentityErrors`: `invitation.invalid`, `invitation.already_pending`, `invitation.limit_reached`, `invitation.not_found`, y el 403 de jerarquía (`tenant_user.role_not_manageable`, `tenant_user.cannot_manage_self`)
- [x] Tests de `Invitation`: transiciones válidas e inválidas, caducidad, rol `Owner` rechazado
- [x] [manual] Capítulo 8 (permisos y matriz con `Users.Read`/`Users.Manage`) y capítulo 3 (carpetas: dónde vive `Invitation` y `ManageableRoles`)

### Bloque 2 — Infrastructure: persistencia y credenciales (≈ 4 h)
- [x] `InvitationConfiguration`: tamaños con las constantes de Domain, índice único por `TokenHash`, índice `(TenantId, Email)`, token de concurrencia (`rowversion`)
- [x] Migración `AddInvitations` revisada a mano (el SQL generado, no solo el nombre)
- [x] `IIdentityService.DeleteUserAsync` e implementación; `ITokenService`: revocar/borrar todos los refresh tokens de un usuario
- [x] Tests de integración (Testcontainers): token duplicado imposible; concurrencia (dos actualizaciones de la misma invitación: una falla); borrar credenciales no deja huérfanos
- [x] Clasificación del modelo de la spec 007: `Invitation` aparece como `ITenantOwned` y su filtro funciona (el test existente lo exige)
- [x] [manual] Capítulo 5 (multi-tenancy): `Invitation` como entidad de la empresa y el nuevo uso justificado de `IgnoreQueryFilters()`; capítulo 6: borrado de credenciales

### Bloque 3 — Application: invitaciones (≈ 5 h)
- [x] Generación del token (32 bytes aleatorios) y hash SHA-256 en un único sitio, compartido con el refresh token si tiene sentido, con test de longitud y no repetición
- [x] `InviteUserCommand` (+ validador, + límites R5, + email con cuenta existente) [`Users.Manage`]
- [x] `GetInvitationsQuery` (sin token ni hash en el DTO) [`Users.Manage`]
- [x] `RevokeInvitationCommand` [`Users.Manage`]
- [x] `AcceptInvitationCommand` anónima: búsqueda por hash con `IgnoreQueryFilters()` (lista blanca con motivo), respuesta idéntica en los cuatro casos de fallo, transacción única, manejo de la concurrencia, el token no se consume si la validación falla
- [x] Declarar permiso o marca anónima en las operaciones nuevas; el test de arquitectura de la spec 008 sigue en verde
- [x] Tests con dobles: cada rama de las reglas R1–R8
- [x] [manual] Capítulo 6 (identidad): invitaciones, token de un solo uso, aceptación atómica y respuesta idéntica en los fallos; capítulo 7 si hay códigos de error nuevos

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
- **Nivel de riesgo de las pruebas: alto** (autorización, aislamiento entre empresas, tokens e invitaciones). Batería completa y mutaciones sobre el código real en cada bloque.
- **Estimación total:** ≈ 30 h de código y ≈ 8 h de manual (repartidas por bloques) en 8 bloques + cierre. Es la spec más grande hasta ahora; se puede partir en dos PRs si prefieres (bloques 1-4 + 6-7 por un lado, revalidación por petición por otro), a costa de dejar unas semanas el riesgo del rol desfasado.
- **Pendiente de decidir en el plan:** purga de invitaciones aceptadas, caducadas o revocadas (guardan el email).
- **Orden de ramas:** el bloque 0 publica primero el manual (caps. 6–10) en `master`; después la spec 009 sale de `master` con el manual ya al día, y cada bloque lleva sus capítulos en la misma rama.
- **Bloque 1 (2026-10-15):** en `Domain/Tenancy`: `Permission` con `UsersRead` y `UsersManage`; `RolePermissions` ampliada (Owner y Admin, ambos; Manager, solo `UsersRead`); `ManageableRoles` (tabla única inmutable, 5 filas, con `CanManage`, `CanChange` —exige poder gestionar el rol actual **y** el nuevo, R10— e `IsGrantable`); `Invitation` (agregado `ITenantOwned`, solo guarda el hash del token, caduca a los 7 días, estado deducido con `StatusAt(utcNow)`, la hora entra por parámetro) e `InvitationStatus`. **Desviaciones del plan:** (1) los 6 códigos de error y **sus mensajes en `ErrorMessages.resx`** se hicieron aquí y no en el bloque 6, para que el commit siga en verde (el test del diccionario exige un mensaje por cada código, igual que en la spec 008); (2) los códigos `invitation.*` van en una clase propia, `InvitationErrors`, y los `tenant_user.*` en `TenancyErrors`; (3) `Invitation` guarda además `NormalizedEmail` (el email en mayúsculas, como `ApplicationUser`) para buscar sin distinguir mayúsculas y para el índice `(TenantId, NormalizedEmail)` del bloque 2; (4) el rechazo del rol `Owner` al crear una invitación usa `tenant_user.role_not_manageable` (400 desde Domain; el 403 por jerarquía lo aplica Application antes). **Tests:** Domain pasa de 100 a 180 (+10 celdas de la matriz de permisos, +47 de `ManageableRolesTests` —25 celdas más coherencia y casos de `CanChange`—, +23 de `InvitationTests`). Suite completa: 480 casos en verde (180 + 88 + 56 + 156). **Mutaciones sobre el código real (10), restauradas desde copia:** el Admin gestiona a otro Admin → 5 en rojo; el Owner gestionable → 7; el Manager recibe `Users.Manage` → 1; el Owner pierde `Users.Manage` → 2; `Create` admite el rol Owner → 2; `Accept` sin comprobar que esté pendiente → 3; caducidad con `>` en vez de `>=` → 3; la caducidad manda sobre lo definitivo → 1; `CanChange` con O en vez de Y → 5; falta el mensaje de `invitation.invalid` → 1 (guarda del diccionario). **Incidente:** al correr la suite completa, 9 tests de integración fallaron porque el motor de Docker no estaba en marcha (no era el código); se arrancó Docker Desktop y todo pasó. **Manual (mismo bloque):** cap. 3 (carpeta `Tenancy`), cap. 7 (27 códigos y 35 entradas del diccionario, tabla de clases), cap. 8 (permisos nuevos, aviso de "en construcción" y la tabla de jerarquía) y cap. 9 (180 y 480 casos, 240 métodos y las nuevas tablas).
- **Bloque 2 (2026-10-15):** `Invitations` en `IApplicationDbContext`/`ApplicationDbContext` (y en el `TestDbContext` de Application.Tests, con el convertidor del email); `InvitationConfiguration` (longitudes desde las constantes de Domain, índice **único** por `TokenHash`, índice `(TenantId, NormalizedEmail)`, y una columna `RowVersion` como propiedad *sombra* —Domain no sabe que existe— para el control de concurrencia optimista); migración `20261015064341_AddInvitations`, con su SQL revisado: solo crea la tabla `Invitations`, los dos índices y la columna `rowversion`. `IIdentityService.DeleteUserAsync` y `ITokenService.DeleteRefreshTokensAsync`, ambos **sin** `SaveChanges` (lo confirma el Handler del bloque 4, junto con el resto de la baja). **Decisión:** los tokens se **borran** (también los ya revocados) en vez de solo revocarse, porque guardan el `UserId` sin clave foránea y quedarían huérfanos al borrar la cuenta; el efecto sobre la sesión es el mismo (un token borrado se rechaza como desconocido). **Tests:** Infrastructure pasa de 56 a 73 (+7 de `IdentityDeletionTests` en memoria y +9 de `InvitationPersistenceTests` contra SQL Server real, +1 de aislamiento entre empresas de las invitaciones); suite completa: **497 casos** en verde (180 + 88 + 73 + 156). Dos guardarraíles propios: `TheMigrations_MatchTheModel` (`HasPendingModelChanges`: falla si se cambia el modelo sin generar migración) y la lista centinela de entidades de empresa de la spec 007 (`TheTenantOwnedEntitiesAreTheExpectedOnes`), que **saltó sola** al añadir `Invitation` y obligó a clasificarla a propósito. **Mutaciones sobre el código real (10), restauradas desde copia:** quitar el índice único o el control de concurrencia del modelo → 13 tests en rojo (EF se niega a migrar si el modelo y la migración no coinciden: primera barrera); `DeleteUserAsync` sin borrar → 3; `DeleteUserAsync` que confirma por su cuenta → 2; `DeleteRefreshTokens` solo los activos → 2; los de todos → 2; que confirme por su cuenta → 2; `Invitation` sin `ITenantOwned` → 6. Como la primera barrera tapa a los tests específicos, se mutó además **la propia migración** (modelo intacto): índice del hash sin `unique` → falla `TwoInvitations_CannotShareTheSameTokenHash`; columna sin `rowversion` → fallan `TwoSimultaneousAcceptances_OnlyOneWins` y `ARemoval_IsAllOrNothing...`. **Manual (mismo bloque):** cap. 5 (cuatro entidades de empresa), cap. 6 (las operaciones gemelas de borrado y por qué los tokens se borran), cap. 9 (497 casos, 257 métodos, 73 en Infrastructure y la nueva familia de tests con SQL real) y las referencias a líneas de `IdentityService`/`TokenService` que las inserciones desplazaron (caps. 5 y 6).
- **Bloque 3 (2026-10-15):** `InvitationToken` (único sitio donde se genera el token —32 bytes aleatorios, 43 caracteres base64 para URL— y se calcula su hash SHA-256; también `IsWellFormed`). `InviteUserCommand`, `GetInvitationsQuery`, `RevokeInvitationCommand` (todas `Users.Manage`, además de la jerarquía) y `AcceptInvitationCommand` (anónima, en `Identity`), con sus validadores y handlers. `TimeProvider` entra por inyección (registrado con `TryAddSingleton` en `AddApplication`; los tests usan un `FakeTimeProvider` propio, sin paquete nuevo). `IIdentityService.IsEmailRegisteredAsync` (R6). Eventos `1401`–`1404` en `SecurityEventIds`, y la jerarquía denegada se registra como `1301` con motivo `RoleNotManageable`. Tercer uso justificado de `IgnoreQueryFilters()` (aceptar), añadido a la lista blanca con su motivo. **Desviaciones del plan:** (1) los eventos de seguridad se añadieron aquí, junto a la acción que los produce, y no en el bloque 6; (2) `Invitation.NormalizeEmail` (nuevo, público) para que quien comprueba "ya hay una pendiente" use exactamente la misma regla que la entidad; (3) el listado devuelve solo las pendientes y solo las de roles que quien consulta puede gestionar (un Admin no ve las de Admin); (4) revocar aplica la misma jerarquía que invitar. **Riesgo aceptado, anotado:** dos invitaciones *distintas* para el mismo email aceptadas a la vez: la perdedora recibe `invitation.invalid` y no `user.email_already_registered` (ambos 400). Y dos invitaciones a la vez del mismo email por el mismo Admin pueden crear dos pendientes (no hay índice único que lo impida, porque la caducidad depende del reloj); inocuo: gana la primera que se acepte. **Hallazgos reales:** (a) el test contra SQL Server real cazó que la perdedora de dos aceptaciones simultáneas del mismo token recibía una violación del índice único del email (un 500), no el error de concurrencia esperado: el handler captura ahora `DbUpdateException` (la base de la de concurrencia) y responde `invitation.invalid`; (b) una mutación **sobrevivió** (comparar el email duplicado sin normalizar): el test usaba un segundo email ya en mayúsculas; se reforzó con 4 variantes y la mutación se detecta; (c) un test **intermitente** destapó que `Base64Url.IsValid` ignora los espacios en blanco, así que `IsWellFormed` aceptaba tokens con un espacio según el azar; ahora se comprueba el alfabeto carácter a carácter, con un test de 300 tokens × 4 posiciones × 8 caracteres intrusos que falla siempre con la versión antigua. **Tests:** Domain 183 (+3), Application 185 (+97: el token, invitar, listar y revocar, aceptar, los validadores y la tabla de operaciones), Infrastructure 79 (+3 de aceptar contra SQL Server real y +3 de `IsEmailRegisteredAsync`); suite completa: **603 casos** en verde. **Mutaciones sobre el código real (20, más la repetición de una tras reforzar su test), restauradas desde copia:** 17 sobre Application (jerarquía al invitar y al revocar, token en claro, email con cuenta, duplicada, límite, caducadas, normalización, filtro de jerarquía y de estado del listado, estado al aceptar, respuesta distinta, conflicto, rol y empresa del miembro, hash, longitud del token) —16 detectadas a la primera y una, la de normalización, tras reforzar el test—; 2 solo detectables con SQL real (quitar `IgnoreQueryFilters()` y quitar la captura de la carrera), ambas detectadas; y la reversión a `Base64Url.IsValid` (4 en rojo, siempre). **Manual (mismo bloque):** cap. 3 (carpeta `Tenancy`), cap. 5 (tres usos de `IgnoreQueryFilters`), cap. 6 (nueva Parte 6: invitaciones, con las dos lecciones, 4 filas de amenazas y 3 preguntas), cap. 8 (once eventos, 3 filas del mapa de amenazas, estado "sin endpoint") y cap. 9 (603 casos, `FakeTimeProvider` y 3 errores reales nuevos).
