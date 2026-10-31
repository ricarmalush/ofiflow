# Tasks — 016-sessions-purge-and-list

**Spec relacionada:** [./spec.md](./spec.md)
**ADR relacionadas:** [ADR-007](../../adr/ADR-007-identity-refresh-token.md), [ADR-009](../../adr/ADR-009-seguridad-base.md) y [ADR-018](../../adr/ADR-018-sesion-ligada-al-access-token.md) (se anotan) y la **ADR-019** nueva (D11)
**Estado general:** En implementación (decisiones D1–D12 aprobadas el 2026-10-30).

---

## Convención

- Cada tarea completable y verificable en una sesión.
- Se marca `[x]` solo cuando cumple la Definición de «Done»: compila, tests en verde, mutaciones detectadas y manual al día.
- Las mutaciones se hacen a mano sobre el código real, con copia de seguridad, **dirigidas por número de línea**, con aviso si la mutación no cambió el fichero y con **`touch` al restaurar**.
- Verificación en vivo con la receta de siempre (base desechable, API real, script fuera del repositorio). Esta spec necesita además **filas con fechas antiguas** (para la purga): se siembran por SQL en la base desechable, con un `UPDATE` de `ExpiresAt` y `RevokedAt`, no esperando.
- **Los bloques 1, 2 y 3 se pueden entregar por separado.** El bloque 1 (purga) no depende de ningún otro: si hace falta, va solo en su propio PR.

---

## Bloques

### Bloque 1 — La purga (Infrastructure + Api)
- [x] Migración `AddRefreshTokenExpiresAtIndex`: índice en `RefreshTokens.ExpiresAt`. Plan comprobado: el `DELETE` usa `Index Seek` con `ExpiresAt < @cutoff`, no lee la tabla entera
- [x] `SecurityEventIds.RefreshTokensPurged = 1011`
- [x] El borrado de un lote: `IRefreshTokenPurger.PurgeBatchAsync(cutoff, batchSize, ct)` (interfaz **interna** de Infrastructure, no `ITokenService`: ningún caso de uso la necesita), implementada por `TokenService` (sigue siendo el único fichero que lee `RefreshTokens`). En **LINQ** (`OrderBy` + `Take` + `ExecuteDeleteAsync`): EF genera un solo `DELETE` con `TOP(@p)` parametrizado; no se amplía el SQL a mano permitido (solo `AccountLockout`). Un test captura el comando y lo fija
- [x] `RefreshTokenPurgeOptions` (`Enabled`, `Interval`, `StartupDelay`, `RetentionDays`, `BatchSize`, `PauseBetweenBatches`; los tiempos son `TimeSpan`) con validación al arrancar, y `RefreshTokenPurgeService` (`BackgroundService` con `PeriodicTimer`): espera el retraso inicial, ejecuta un ciclo cada intervalo, captura y registra cualquier excepción **sin salir del bucle**, se detiene con la API. El evento `1011` una vez por ciclo y solo si borra algo
- [x] Registrado en la API (`AddRefreshTokenPurge`, solo desde `Program.cs`; la consola de administración no lo recibe, con test) y **desactivado en los tests** de la API por configuración (`RefreshTokenPurge:Enabled = false` en las dos factorías)
- [x] Tests (31 de Infrastructure con SQL Server real + 2 de la API): borra caducadas hace más del margen (revocadas o no, de cualquier cuenta); no toca activas, revocadas sin caducar ni caducadas dentro del margen; el corte exacto se queda; respeta el lote y borra lo más antiguo primero; una sesión activa con renovaciones viejas sigue activa; el comando es un solo `DELETE` set-based con `TOP`; un ciclo borra todo en varios lotes (incluido el caso de un lote exactamente completo); el margen cuenta desde ahora y es configurable; un evento por ciclo con el total y sin datos sensibles; ninguno si no borra; dos instancias a la vez borran todo sin errores; un ciclo que falla no mata el servicio y el siguiente reintenta (el fallo no es un evento de seguridad); desactivado no hace nada; no purga antes del retraso inicial y se detiene limpio durante él; las opciones (valores por defecto aprobados, cada valor inválido rechazado, el registro falla al leer una sección inválida); la API registra el servicio (y un test lo detecta si se quita la línea de `Program.cs`)
- [x] Test de arquitectura: la lectura de `RefreshTokens` sigue limitada a `TokenService.cs` y el SQL a mano sigue limitado a `AccountLockout.cs` (los de siempre, en verde)
- [x] Mutaciones (15, todas detectadas): purgar también lo no caducado; margen `<=`; orden inverso; lote de más; un lote que no continúa; margen en horas y no en días; evento aunque no borre; evento con recuento cero; el servicio que muere con la excepción; ignorar `Enabled`; sin el retraso inicial; validación siempre verdadera; valores por defecto cambiados (margen, lote); y quitar el registro de `Program.cs`
- [x] Verificación contra la API publicada de verdad (base desechable, filas envejecidas por SQL): 3000 filas, las 4 que deben quedarse siguen (incluida la que caduca justo dentro del margen), la sesión real sigue activa y un solo evento `1011`; plan del `DELETE`; **100 000 filas en 32 s** con la configuración por defecto; efecto sobre las peticiones y su causa (estadísticas síncronas; ver la spec); sin escalado de bloqueos hasta 2000 filas por lote
- [x] [manual] Capítulo 6 (el final del ciclo de vida del refresh token, con las mediciones y la lección de método), capítulo 3 (el primer servicio en segundo plano) y capítulo 8 (el evento `1011`). La ADR-019 se escribe en el bloque 4, junto a la parte de privacidad

### Bloque 2 — Los datos de la sesión (Application + Infrastructure, con migración)
- [ ] Migración `AddRefreshTokenSessionInfo`: `CreatedAt` (relleno `DATEADD(DAY, -7, ExpiresAt)`), `SessionStartedAt` (= `CreatedAt`), `UserAgent` y `IpAddress` (anulables). Mismo patrón de la spec 015: anulable → relleno → `NOT NULL` en las que lo exigen, sin valor por defecto. Probar con filas de la migración anterior y con el `Down`
- [ ] `IClientInfo` (Application) y `ClientInfo` (Infrastructure): IP truncada (IPv4 sin el último octeto, IPv6 a 48 bits; sin IP si no hay conexión) y `User-Agent` truncado a 256, sin interpretarlo
- [ ] `TokenService.IssueTokensAsync` estampa `CreatedAt`, `SessionStartedAt`, el navegador y la IP truncada de la petición; `RotateRefreshTokenAsync` **hereda** `SessionStartedAt`, navegador e IP de la fila anterior y toma su propio `CreatedAt`
- [ ] Tests: el truncado de IPv4 e IPv6 (incluidas formas raras: IPv4 mapeada en IPv6, abreviaturas, sin IP); el truncado del User-Agent (ausente, vacío, enorme); renovar hereda y no sobrescribe; una renovación desde otro navegador **no** cambia el dispositivo de la sesión; las sesiones migradas quedan «desconocidas»
- [ ] [manual] Capítulo 6 y capítulo 8 (qué se guarda del cliente, por qué truncado, cuánto vive)

### Bloque 3 — Listar y cerrar una concreta (Application + Api)
- [ ] `ICurrentUser.SessionId` (el `sid` del token) y `SecurityEventIds.SessionClosedByOwner = 1010`
- [ ] `DeviceLabel`: parser sencillo del `User-Agent` («Chrome en Windows», «Safari en iPhone»…, «Dispositivo desconocido» ante cualquier otra cosa) con una tabla de casos (los navegadores y sistemas habituales, un User-Agent vacío, uno enorme, uno con intento de inyección de texto)
- [ ] `ITokenService.ListSessionsAsync(userId, currentSessionId, ct)` y `CloseSessionAsync(userId, sessionId, ct)` (revoca las filas **activas de esa sesión y de ese `UserId`**; un solo `SaveChanges`; devuelve si había una) en `TokenService`
- [ ] `ListMySessionsQuery` (`[RequiresPermission(Permission.AccountRead)]`) → `SessionDto` (`id`, `device`, `startedAt`, `lastActiveAt`, `approximateIp`, `current`) y `CloseSessionCommand` (`[RequiresPermission(Permission.AccountCloseSessions)]`, con el Id de la sesión; `404` con el mismo código y cuerpo para inexistente, ajena y ya cerrada). Entran en la tabla «operación → permiso» (40 operaciones, 29 con permiso)
- [ ] Rutas `GET /api/v1/account/sessions` (`no-store`) y `DELETE /api/v1/account/sessions/{id:guid}` (cubo `account-sessions`, compartido con «cerrar todas»), con su sección en `OfiFlow.Api.http`
- [ ] Tests (Application + API con SQL Server real): dos navegadores = dos sesiones y solo una `current`; renovar no duplica ni cambia `startedAt`; cada cuenta ve solo las suyas; cerradas y caducadas no aparecen; sin token, basura y token de plataforma `401`; empresa suspendida `403`; los cinco roles; `no-store`; la respuesta **sin** refresh token, hash, User-Agent crudo ni IP completa; cerrar una deja la otra en pie; Id de otra cuenta, inexistente y ya cerrada dan el **mismo `404`** (comparación de cabeceras y cuerpo, como en el test de los fallos del login); cerrar la actual da `204` y luego `401`; Id que no es Guid `404`; un `userId` ajeno en ruta, consulta o cuerpo se ignora; el evento `1010` sin Id de sesión ni tokens; el cubo compartido da `429` con `Retry-After`
- [ ] Ajustar el test de la spec 014 que pedía `DELETE /account/sessions/{guid}` y esperaba `404` (sigue dando `404`, pero ahora porque la sesión no existe y no porque la ruta no exista)
- [ ] [manual] Capítulos 0 (tabla de rutas), 5 (lista blanca), 6 y 8 (el catálogo de eventos `1010` y `1011`)

### Bloque 4 — Mutaciones, verificación real y cierre
- [ ] Mutaciones sobre el código real (todas deben detectarse): purgar también lo no caducado; no respetar el margen; purgar solo revocadas; no filtrar el lote (borrar toda la tabla); un lote sin bucle; el bucle que no termina; el servicio que muere con la excepción; el evento sin recuento o con cero; heredar sin copiar (renovar pierde el dispositivo); heredar el `CreatedAt` en vez de tomar el nuevo; guardar la IP completa; no truncar el User-Agent; listar sin filtrar por `UserId`; listar las cerradas o caducadas; `current` siempre verdadero o falso; devolver el User-Agent crudo; cerrar sin filtrar por `UserId`; `404` distinto según el caso; no revocar al cerrar; el evento con el Id de la sesión; un cubo de límite distinto
- [ ] Verificación contra la API publicada de verdad (script fuera del repositorio, base desechable): dos «dispositivos» con `User-Agent` distinto y la IP de prueba (`X-Test-Client-Ip` no existe en producción: se comprueba la IP del propio equipo, `::1`/`127.0.0.1`), la lista, cerrar una, cerrar la actual, **y la purga con filas envejecidas por SQL** (con el plan de ejecución del `DELETE`)
- [ ] Medir: el tiempo de una purga de, por ejemplo, 100 000 filas envejecidas, y que durante ella una petición autenticada no se bloquea
- [ ] [manual] Capítulo 9 (cifras y tests nuevos), `docs/README.md`, **ADR-019** nueva, relación en ADR-007, ADR-009 y ADR-018, y la spec 014 (la ruta `/{id}` deja de ser un `404` de ruta)
- [ ] Suite completa, proyecto a proyecto
- [ ] [externo] Commit, push de la rama, PR y merge (con `--merge`), cada uno con confirmación explícita

### Bloque 5 — Frontend (repositorio `ofiflow-frontend`)
- [ ] `SessionsService` (o ampliar `AuthService`): `list()` y `close(id)`; los modelos `SessionInfo` con los nombres exactos de la API
- [ ] La tarjeta «Sesiones» de la pantalla de Seguridad: la lista (dispositivo, «Esta sesión», empezó, última actividad, red aproximada) con «Cerrar» en cada una; cerrar otra refresca la lista; cerrar «Esta sesión» hace el cierre local y lleva al login con aviso; los errores se dicen y no cierran lo local. Se conserva «Cerrar todas mis sesiones»
- [ ] Tests: la lista y su orden; «Esta sesión» marcada y sin acción destructiva ambigua; cerrar otra; cerrar la actual; error del servidor; sin permiso no hay tarjeta (`Account.Read` y `Account.CloseSessions` por separado); un texto de dispositivo con HTML se muestra como texto
- [ ] Comprobación en vivo contra la API y commit (con orden explícita)
