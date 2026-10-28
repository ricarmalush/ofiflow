# Tasks — 014-logout-all

**Spec relacionada:** [./spec.md](./spec.md)
**ADR relacionadas:** [ADR-007](../../adr/ADR-007-identity-refresh-token.md) y [ADR-012](../../adr/ADR-012-autorizacion-rbac.md) (se amplían; sin ADR nueva)
**Estado general:** API implementada y verificada (2026-10-28): 1649 tests en verde (Domain 235, Application 587, Infrastructure 332, Api 495), 18 mutaciones detectadas y verificación contra la API publicada. **Pendiente:** commit, push, PR y merge (órdenes explícitas) y el bloque 5 (frontend).

---

## Convención

- Cada tarea completable y verificable en una sesión.
- Se marca `[x]` solo cuando cumple la Definición de «Done»: compila, tests en verde, mutaciones detectadas y manual al día.
- Las mutaciones se hacen a mano sobre el código real, con copia de seguridad y **dirigidas por número de línea** (lección de la spec 013: un patrón puede apuntar a otra coincidencia); el script avisa si la mutación no cambió el fichero y, desde esta spec, **hace `touch` al restaurar** (si no, MSBuild reutiliza el ensamblado compilado con la mutación anterior).

---

## Bloques

### Bloque 1 — El permiso y el caso de uso (Domain + Application)
- [x] `Permission.AccountCloseSessions` con su nombre estable `Account.CloseSessions` (`PermissionNames`, que ahora parte solo por la primera frontera de palabra) y su celda en la matriz de los cinco roles (13 permisos, 65 celdas)
- [x] `SecurityEventIds.AllSessionsClosed = 1008`
- [x] `ITokenService.RevokeAllRefreshTokensAsync(userId, ct)` → `int`
- [x] `CloseAllSessionsCommand` (`[RequiresPermission(Permission.AccountCloseSessions)]`, sin parámetros) y su handler (el `UserId` sale de `ICurrentUser`)
- [x] Entra en la tabla «operación → permiso» (38 operaciones, 27 con permiso)
- [x] Tests: la matriz y los nombres exactos (los trece), el handler entrega el `UserId` del token y no devuelve nada, el comando no tiene propiedades (no hay nada que falsificar)
- [x] [manual] Capítulo 3 (carpeta nueva) y capítulo 8 (la matriz)

### Bloque 2 — La revocación (Infrastructure)
- [x] `TokenService.RevokeAllRefreshTokensAsync`: los activos de ese `UserId` → `Revoke()`, un solo `SaveChanges`, evento `1008` (nivel `Warning`) con el recuento (cero incluido); no toca los revocados ni los caducados ni los de otra cuenta
- [x] Lista blanca de lectura de `RefreshTokens`: `TokenService.cs` ya la cubría (`RefreshTokens` es `internal`; los tests de la API no pueden leerlo, y está bien)
- [x] Tests: revoca todos los activos de la cuenta (tres dispositivos); revoca y no borra; no toca otra cuenta; alcanza todas las empresas de la cuenta; no pisa la fecha de los ya revocados ni los cuenta; no cuenta los caducados; sin sesiones activas da cero y registra igual; el recuento del evento (texto completo); el evento sin tokens ni hashes; usar después un token cerrado dispara `1003`
- [x] [manual] Capítulo 6 (identidad: ciclo de vida del refresh token)

### Bloque 3 — La ruta (Api)
- [x] `DELETE /api/v1/account/sessions` (autenticada, grupo `/account`), `204`, `Cache-Control: no-store`, política `account-sessions` 10/hora/IP
- [x] Tests con la API entera y SQL Server real (21): tres dispositivos y los tres dejan de renovar; dos cuentas (la otra sigue, también otro miembro de la misma empresa); cero sesiones da `204`; un `userId` ajeno en la ruta, la cadena de consulta o el cuerpo se ignora; sin token, basura y token de plataforma dan `401`; empresa suspendida `403`; miembro quitado `403`; los cinco roles pueden; el límite da `429` con `Retry-After` (entre 61 y 3600 s: la ventana es de una hora); `no-store`; el evento `1008` con Ids y recuento; usar un token cerrado da `401` y `1003`
- [x] Sección en la colección `OfiFlow.Api.http`
- [x] [manual] Capítulos 0 (tabla de rutas), 5 (lista blanca) y 8 (sesiones, evento `1008`, el límite del access token)

### Bloque 4 — Mutaciones, verificación real y cierre
- [x] Mutaciones sobre el código real: 18, todas detectadas (no revocar, borrar las filas, no filtrar por `UserId`, revocar también los ya revocados, contar los caducados, sin evento, recuento a cero, `UserId` equivocado en el handler, sin `[RequiresPermission]`, sin límite, con el cubo de otra política, sin `no-store`, permiso quitado al técnico, `200` en vez de `204`, nombre sin partir solo por la primera frontera, límite de 11, ventana de un minuto, ruta anónima). Dos sobrevivieron a la primera versión de los tests (recuento a cero por una aserción floja; ventana de un minuto) y se endurecieron
- [x] Verificación contra la API publicada de verdad (script fuera del repositorio, base desechable `OfiFlowVerify014`, ya borrada)
- [x] [manual] Capítulo 9 (cifras y tests nuevos), `docs/README.md`, ADR-007 y ADR-012 (relación), y estado de la spec y de las tareas
- [x] Suite completa, proyecto a proyecto
- [ ] [externo] Commit, push de la rama, PR y merge (con `--merge`, como los PR anteriores), cada uno con confirmación explícita

### Bloque 5 — Frontend (repositorio `ofiflow-frontend`)
- [ ] El permiso `Account.CloseSessions` en el tipo `Permission` del frontend (contrato con los nombres de la API)
- [ ] `AuthService.closeAllSessions()` (o servicio de cuenta) que llama a `DELETE /account/sessions` y, al terminar con éxito, cierra la sesión local con el aviso «Se cerraron todas tus sesiones»; con error NO cierra lo local y lo dice
- [ ] Botón «Cerrar todas mis sesiones» en la pantalla de Seguridad, con confirmación, visible solo con el permiso
- [ ] Tests: éxito, error del servidor, sin permiso (no hay botón), el botón sigue un cambio de rol, la confirmación cancelada no llama
- [ ] Comprobación en vivo contra la API y commit (con orden explícita)
