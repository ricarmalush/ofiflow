# Tasks — 011-platform-operator-panel

**Spec relacionada:** [./spec.md](./spec.md)
**ADR relacionada:** [../../adr/ADR-016-panel-de-operador-de-la-plataforma.md](../../adr/ADR-016-panel-de-operador-de-la-plataforma.md)
**Estado general:** Aprobada (2026-10-22); bloques 1 a 6 implementados, verificados y commiteados (2026-10-23 a 2026-10-26); bloque 7 y cierre implementados y verificados (2026-10-26), pendientes de commit; push, PR y merge, a la espera de una orden explícita

---

## Convención

- Cada tarea completable y verificable en una sesión (1-4h).
- Se marca `[x]` solo cuando cumple la Definición de "Done" (sección 86).
- Orden por defecto: Domain → Application → Infrastructure → API → Tests.
- Cada guardarraíl se verifica **rompiendo a propósito el código real** y viendo el test en rojo antes de restaurarlo. Restaurar con **copias de seguridad o tras commitear**, nunca con `git checkout` sobre cambios sin commitear.
- ⚠️ Las tareas **[externo]** (push, PR, merge) requieren confirmación explícita en el momento de hacerlas.
- **Nivel de riesgo de las pruebas: alto** (una identidad con acceso global, el aislamiento entre empresas y las sesiones). Batería completa, pruebas contra SQL Server real para lo transaccional y mutaciones sobre el código real en cada bloque.

## Regla de trabajo: manual y código en paralelo

Cada bloque termina con su tarea **[manual]**: los capítulos que describen lo que ese bloque cambió se actualizan **en el mismo bloque**, verificando cada nombre, ruta y cifra contra el repositorio. La spec no se cierra con capítulos desfasados.

## Orden de los bloques y por qué

Se empieza por lo que **ya se puede probar sin operador**: que la suspensión se haga cumplir a los miembros (bloques 1 y 2). Así el cambio más delicado para las empresas (que una empresa pueda quedar sin acceso) se verifica aislado, antes de que exista nadie con poder para provocarlo. La identidad del operador llega después (bloques 3 a 5) y las operaciones, al final (bloque 6).

## Checklist

### Bloque 0 — Aprobación
- [x] Decisiones D1-D11 de la spec confirmadas (2026-10-22, tal como se recomendaron)
- [x] Spec y tareas aprobadas (2026-10-22); ADR-016 sigue **Propuesto** hasta el cierre (como la 014 en la spec 010); se trabaja en la rama `spec/011-platform-operator-panel`

### Bloque 1 — Domain: la empresa se puede suspender (≈ 3 h)
- [x] `Tenant.SuspendedAt`, `IsSuspended`, `Suspend(utcNow)` y `Reactivate()`; el reloj entra como parámetro (nada de `DateTime.UtcNow` dentro del dominio)
- [x] Errores con código en `TenancyErrors` y sus mensajes en `ErrorMessages.resx` (el test del diccionario los exige): `tenant.suspended`, `tenant.already_suspended`, `tenant.not_suspended`
- [x] Configuración EF de la columna y **migración** `AddTenantSuspension` revisada a mano (el SQL generado): columna nula, sin valor por defecto, no toca ninguna otra tabla **Hecho (2026-10-22):** el SQL es una sola sentencia, `ALTER TABLE [Tenants] ADD [SuspendedAt] datetime2 NULL`; las empresas existentes quedan activas. La herramienta `dotnet ef` se ejecuta con `--startup-project src/OfiFlow.Infrastructure` (el proyecto de la API no referencia `EF Design`).
- [x] Tests de Domain: suspender una activa, una ya suspendida (error), reactivar una suspendida, una activa (error), la hora que se guarda
- [x] Mutaciones: `Suspend` sin comprobar el estado actual; `Reactivate` sin vaciar el campo; guardar la hora local en vez de la recibida **Hecho (2026-10-22):** 5 mutaciones sobre `Tenant.cs` (las tres anteriores, más `Reactivate` sin comprobar el estado e `IsSuspended` siempre falso) y la entrada del diccionario borrada: todas detectadas. Y la séptima, **el modelo con `SuspendedAt` sin migración ni snapshot**, también: `TheMigrations_MatchTheModel` falla con `PendingModelChangesWarning` («añade una migración»). Suite completa con SQL Server real en verde: **1205 casos** (Domain 206, Application 445, Infrastructure 223, Api 331).
- [x] [manual] Capítulo 3 (qué es ahora un `Tenant`) y capítulo 7 (nuevos códigos y nº de entradas del diccionario)

### Bloque 2 — La suspensión se hace cumplir a los miembros (≈ 6 h) — el bloque más delicado para las empresas
- [x] `ICurrentUser.VerifyMembershipAsync` incluye el estado de la empresa: una empresa suspendida **no** supera la comprobación (R15d); sigue siendo **una** consulta por petición **Hecho (2026-10-23):** la suspensión viaja en la MISMA consulta que la pertenencia (una subconsulta sobre la clave de la empresa); un test cuenta las consultas. `ICurrentUser` gana `TenantSuspended` para distinguir «ya no pertenece» de «empresa suspendida».
- [x] `LoginCommand`: tras credenciales correctas, una empresa suspendida da 403 `tenant.suspended` y **no cuenta como intento fallido**; decidir y documentar cómo se devuelve la reserva del intento (R15a) respecto al bloqueo de la spec 010 **Decidido y hecho:** no hace falta «devolver» ningún intento, porque `AuthenticateAsync` ya dejó el contador como en cualquier acierto; la comprobación va justo después de autenticar y ANTES de crear el desafío. Probado con 7 intentos seguidos con la contraseña correcta (que, si contaran, habrían bloqueado la cuenta al quinto).
- [x] Refresh del token y aceptar una invitación: rechazan una empresa suspendida con las respuestas ya existentes (R15b, R15c); registrarse **no** se ve afectado (R16) **Hecho:** el refresh no revoca el token (al reactivar vuelve a servir) y la invitación no se consume. También el canje del segundo factor.
- [x] Evento `1303` (acceso denegado por empresa suspendida: petición, login y canje), solo Ids. Refresh e invitación dejan `1004` y `1403` con el motivo `TenantSuspended`.
- [x] Tests con dobles: cada rama; dos empresas (suspender A no toca B) **Hecho:** +19 en Application (login, canje, invitación, comportamiento de autorización y el nuevo `TenantStatus`).
- [x] **Tests contra SQL Server real**: el miembro con un token válido recibe 403 en la **siguiente** petición tras suspender, y vuelve a operar tras reactivar con sus datos intactos **Hecho:** +8 de extremo a extremo por HTTP con dos empresas y SQL Server real y +8 en Infrastructure (pertenencia, una sola consulta y refresh).
- [x] Mutaciones: `VerifyMembershipAsync` sin mirar la suspensión; el login que cuenta el intento; el refresh que ignora la suspensión; la invitación que entra; quitar el evento **Hecho:** 14 mutaciones sobre el código real, todas detectadas; 3 repetidas ejecutando SOLO los tests de extremo a extremo, también detectadas; y una lectura de `Tenants` colada en el login, que detecta el test de la lista blanca (con su control positivo). El código quedó idéntico a la copia previa.
- [x] [manual] Capítulo 5 (la empresa suspendida y la comprobación por petición) y capítulo 8 (evento `1303`) **Hecho**, y corregidas las referencias de línea del manual a `LoginCommandHandler`, `TokenService` y `VerifyMfaChallengeCommandHandler` (los capítulos 5, 6 y 8 las citaban por número).

### Bloque 3 — La identidad del operador (≈ 5 h)
> **Estado (2026-10-25): implementado y verificado.** Código y tests en verde; 15 mutaciones sobre el código real, todas detectadas (las 13 del script `mutate3.sh`, el modelo cambiado sin migración y la clave primaria quitada de la propia migración); suite completa 1286 casos en verde (Domain 206, Application 464, Infrastructure 253, Api 363). En la primera suite completa falló una vez, de forma intermitente y sin explicar, `TwoSimultaneousAcceptances_OfTheSameToken_CreateExactlyOneAccount`; no se reprodujo en 380 iteraciones bajo carga (ver capítulo 9, límite 9). **Pendiente:** el commit.
- [x] Entidades `PlatformOperator` y `PlatformAuditEntry` con sus configuraciones (longitudes desde constantes, índice **único** por `UserId`, sin `RowVersion` en la auditoría), globales; **migración** `AddPlatformOperator` revisada a mano
- [x] Clasificación del modelo de la spec 007: las dos entidades como **globales** con su motivo en `TenantModelRules.GlobalEntities` (el test centinela lo exige)
- [x] `MfaChallenge.Audience` (`tenant` | `platform`) en la misma migración, con valor `tenant` para las filas existentes; el canje de empresa solo acepta `tenant` (R9)
- [x] `IPlatformOperators` (¿es operador activo?, alta, baja) y `IPlatformAudit` (escribe la fila **sin confirmarla**: la confirma el handler en su único `SaveChanges`)
- [x] Invariante R1: una cuenta con `TenantUser` no puede ser operador y un operador no puede tener `TenantUser`; test contra SQL Server real
- [x] Tests de integración: índice único; la fila de auditoría no se puede modificar desde la aplicación; migración coherente con el modelo
- [x] Mutaciones: quitar el índice único (también en la propia migración); el canje de empresa que acepta un desafío de plataforma; el alta sin comprobar la pertenencia **Hecho:** 15 mutaciones, todas detectadas. La del índice único es la clave primaria (la unicidad es la clave): quitarla de la migración la detectan `TheOperatorKey_IsUniquePerAccount_InTheDatabase` y otro test de atomicidad de la auditoría. Código idéntico a la copia previa tras cada una.
- [x] [manual] Capítulo 6 (qué es un operador y por qué no es un rol) y capítulo 5 (las dos entidades globales nuevas)

### Bloque 4 — Alta y baja del operador, fuera de la API (≈ 5 h)
> **Estado (2026-10-25): implementado y verificado.** Proyecto de consola aparte `OfiFlow.Admin` (la API no lo referencia; recibe `Mfa:EncryptionKey` pero no `Jwt:Secret`), con la lógica en `Application` (`PlatformOperatorAdministration`, no es una petición de MediatR). El primer código TOTP se comprueba en memoria antes de guardar nada y todo se confirma en un único `SaveChanges`. 25 mutaciones sobre el código real, todas detectadas (una, M20, hubo que repetirla: el test nuevo estaba mal escrito y la «detectaba» por el motivo equivocado). Suite: Domain 206, Application 488, Infrastructure 261, Api 369 (1324 casos), proyecto a proyecto; una pasada conjunta falló por contención de Docker (contenedor de SQL Server con código 191), no por el código. **Sin ejecutar por mí:** el tramo interactivo del programa (contraseña sin eco, reintentos del código) necesita una terminal real; se comprueba a mano al instalar y en el bloque 7. **Pendiente:** el commit.
- [x] **Decidir y documentar dónde vive el comando del servidor** (modo de arranque del propio host o proyecto de consola aparte) y cómo se lee la contraseña (por consola, sin eco, nunca por argumento)
- [x] Alta (R2): crea la cuenta, genera el secreto TOTP, **exige un primer código válido** y muestra los códigos de recuperación una vez; **todo o nada**: si no se confirma, no queda ninguna fila
- [x] Baja (R4): rellena `DisabledAt`; escribe la auditoría y el evento
- [x] Eventos `1606` (operador creado) y `1607` (operador dado de baja)
- [x] Tests: alta correcta; alta sin confirmar → nada creado; email ya registrado → falla sin modificar nada; baja de un operador inexistente
- [x] **Test de arquitectura:** ningún endpoint ni petición MediatR expuesta alcanza el alta ni la baja
- [x] Mutaciones: el alta que no exige el código; el alta que deja la cuenta si falla la confirmación; la baja sin auditoría
- [x] [manual] Capítulo 1 (cómo se crea el primer operador, con el procedimiento paso a paso) y capítulo 8 (eventos `1606`, `1607`)

### Bloque 5 — La sesión de plataforma (≈ 7 h) — el bloque más delicado para la seguridad

> **Estado (2026-10-26): implementado y verificado.** Esquema `PlatformBearer` con clave (`Jwt:PlatformSecret`) y audiencia propias, política `PlatformOperator`, sesión de 15 minutos sin refresh token, `POST /api/v1/platform/auth/login` y `/verify` (límites 5 y 10 por minuto y por IP), `[RequiresPlatformOperator]` como tercer tipo de declaración y revalidación en la base de datos en cada petición. 28 mutaciones sobre el código real, todas detectadas (la 27 se repitió: el script usaba `/` como delimitador). Suite, proyecto a proyecto: Domain 206, Application 515, Infrastructure 296, Api 415 (1432 casos). **Notas:** (1) la API de desarrollo ya no arranca sin `Jwt:PlatformSecret` en los user-secrets (README y capítulo 1); (2) como aún no hay rutas de plataforma con efecto, los tests montan una ruta y una operación de prueba que pasan por el pipeline real; (3) un campo vacío o enorme en `/verify` es un `400`, igual que en la puerta de empresa, no un `401`; (4) el segundo caso de R6, «operador sin segundo factor», solo se prueba con dobles (el alta lo hace inalcanzable por HTTP).

- [x] `Jwt:PlatformSecret` y audiencia propia: **sin valor por defecto**, la API no arranca si falta o no mide 32 bytes, con el mismo trato que `Jwt:Secret` (y el mismo mensaje útil)
- [x] Esquema de autenticación de plataforma separado del de empresa (otra clave, otra audiencia, 15 minutos, sin refresh token)
- [x] `IPlatformTokenService` (emitir la sesión de plataforma) y `PlatformLoginCommand`: **siempre** desafío; mismas respuestas y misma verificación de contraseña para todos los fallos (R6)
- [x] `VerifyPlatformChallengeCommand`: canjea solo desafíos de plataforma (R9) y es el **único** sitio que emite sesión de plataforma
- [x] `[RequiresPlatformOperator]`: tercer tipo de declaración, excluyente con permiso y anónima, **fail-closed**; el `AuthorizationBehavior` revalida en cada petición que el operador sigue activo (R10)
- [x] Eventos `1601`, `1602` y `1608`; la fila de auditoría de la sesión emitida
- [x] **Tests de arquitectura:** R8 (solo tres sitios emiten sesiones) y la declaración exclusiva de los tres tipos
- [x] Tests con dobles: cada rama; cuenta de **empresa** con su contraseña correcta → mismo 401; cuenta bloqueada; operador dado de baja
- [x] **Tests de no confusión de esquemas:** un token de empresa firmado correctamente en `/platform` → 401; uno de plataforma en cualquier otra ruta → 401; un token firmado con la clave equivocada o la audiencia equivocada → 401
- [x] Mutaciones: la misma clave o audiencia para los dos mundos; el login de plataforma que devuelve sesión sin desafío; la revalidación omitida; el desafío que cruza de mundo; un atributo de declaración ausente
- [x] Clave de **pruebas** de plataforma en los fixtures de la API y en el workflow de ZAP (`dast.yml`, efímera) y en la guía de arranque
- [x] [manual] Capítulo 6 (la sesión de plataforma frente a la de empresa) y capítulo 8 (eventos, el tercer tipo de declaración y el riesgo del operador comprometido)

### Bloque 6 — Las operaciones del operador (≈ 6 h)

> **Estado (2026-10-26): implementado y verificado.** `GET /api/v1/platform/tenants` (paginado, `pageSize` ≤ 100, alta descendente y Id, filtro por suspendida), `POST .../{id}/suspend` y `.../reactivate`, con la política de plataforma, 30 peticiones por minuto y por IP, eventos `1603`–`1605` y la fila de auditoría en el mismo `SaveChanges`. **Decisiones tomadas aquí:** (1) la empresa gana una **versión de fila** (migración `AddTenantRowVersion`) para que dos suspensiones simultáneas dejen una sola fila de auditoría; el perdedor recibe el mismo error de negocio que si hubiera llegado un instante después; (2) el recuento de miembros es una subconsulta por empresa **de la página** (no un agrupado de todas), con `IgnoreQueryFilters()` en la lista blanca; (3) `tenant.not_found` es un código nuevo (404); (4) `TenantId` entra por la ruta como **excepción nombrada** de dos operaciones; (5) la página tiene un tope (10 millones) para que (página − 1) × tamaño no desborde. 24 mutaciones, todas detectadas (N9, el desempate por Id, sobrevivió la primera vez y obligó a añadir el test del `ORDER BY`). Suite, proyecto a proyecto: Domain 206, Application 558, Infrastructure 309, Api 438 (1511 casos).

- [x] `ListTenantsQuery`: nombre, alta, nº de miembros y estado, **nada más**; paginado con tope y orden estable; el recuento agrupado con `IgnoreQueryFilters()` **añadido a la lista blanca** con su motivo; el handler entra en la lista de lecturas globales de `Tenants`
- [x] `SuspendTenantCommand` y `ReactivateTenantCommand`: suspender o reactivar y escribir la **fila de auditoría en la misma transacción**; `TenantId` como objetivo, **declarado como excepción nombrada** en el test «ningún comando acepta `TenantId`»
- [x] Eventos `1603`, `1604` y `1605`, solo Ids
- [x] Validators (`pageSize` ≤ 100, página ≥ 1), códigos de error y mensajes en el diccionario
- [x] Endpoints `/api/v1/platform/tenants` (listar, suspender, reactivar) con su política de rate limiting nueva (30 por minuto y por IP) y los del login y el canje (5 y 10); OpenAPI con su etiqueta
- [x] Sección nueva en la colección `OfiFlow.Api.http` (el alta del operador se explica allí, pero no se puede hacer por HTTP)
- [x] Tests con dobles y **contra SQL Server real**: suspender es todo o nada con la fila de auditoría; la lista **no contiene** ningún dato personal (se inspecciona el cuerpo entero)
- [x] Mutaciones: la lista con una columna de más; suspender sin escribir la auditoría; la auditoría fuera de la transacción; el `pageSize` sin tope; quitar la entrada de la lista blanca
- [x] [manual] Capítulo 4 (una petición de plataforma de principio a fin), capítulo 8 (mapa de amenazas y límites) y capítulo 3 (carpetas nuevas)

### Bloque 7 — Extremo a extremo y ataques (≈ 5 h)

> **Estado (2026-10-26): implementado y verificado.** `PlatformPanelAttacksEndToEndTests` (10 tests, API entera, SQL Server real, reloj controlado): el recorrido completo y cada ataque de la spec, incluidos los 50 inicios de sesión de plataforma simultáneos (cinco comprobaciones reales y cuenta bloqueada). **12 mutaciones** sobre el código compartido probadas solo contra esa clase, todas detectadas (bloqueo por cuenta, revalidación del operador, la suspensión en login, refresh, invitación y pertenencia, el canje, la auditoría y los eventos). **Verificación contra la API publicada** (`verify011.sh`, fuera del repositorio): 77 comprobaciones, todas correctas; el primer intento falló siete por un fallo del propio script (Git Bash reescribe la ruta de `sqlcmd` dentro de `docker exec`), ninguna por la API. **Para hacerla** hizo falta un programa de apoyo que da de alta al operador sin terminal (`OfiFlow.Admin` exige una interactiva): vive fuera del repositorio. Suite, proyecto a proyecto: Domain 206, Application 559, Infrastructure 309, Api 448 (1522 casos).

- [x] Ciclo completo por HTTP con el reloj controlado (`ControlledClock`): alta del operador, login, desafío, listar, suspender, el miembro bloqueado, reactivar
- [x] Cada ataque de la spec como test: un `Owner` en `/platform`, el operador en `/customers`, un desafío que cruza de mundo, un operador dado de baja con un token vivo, 50 logins de plataforma simultáneos contra el bloqueo
- [x] Dos empresas: suspender A no afecta a B en ninguna operación
- [x] Mutaciones sobre el código real (se anotan todas en este fichero, como en la spec 010)
- [x] Verificación contra la **API publicada de verdad** (como el `verify.sh` de la 010): login de plataforma con TOTP real, suspender y comprobar el 403 del miembro
- [x] [manual] Capítulo 9 (cifras y nuevos tests) y anexo de preguntas del tribunal

### Cierre
- [x] ADR-016 pasa a **Aceptado** con su nota de estado; ADR-009 (catálogo de eventos: 23 → 32, identidad con acceso global) y ADR-012 (tercer tipo de declaración) anotados; ADR-014 enlaza el uso del MFA obligatorio
- [x] Revisión final del manual **buscando también** frases de «obra en curso» y referencias de línea (lo que se escapó en la 010); `docs/README.md`, README raíz y cifras al día
- [x] Memoria del proyecto al día
- [ ] [externo] Push de la rama, PR y merge, cada uno con confirmación explícita
