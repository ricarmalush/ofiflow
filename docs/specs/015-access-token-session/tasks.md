# Tasks — 015-access-token-session

**Spec relacionada:** [./spec.md](./spec.md)
**ADR relacionadas:** [ADR-007](../../adr/ADR-007-identity-refresh-token.md), [ADR-013](../../adr/ADR-013-gestion-de-usuarios.md) y [ADR-014](../../adr/ADR-014-mfa-y-bloqueo-de-cuenta.md) (se anotan) y la **[ADR-018](../../adr/ADR-018-sesion-ligada-al-access-token.md)** nueva (D8)
**Estado general:** API implementada y verificada (2026-10-30): 1686 tests en verde (Domain 235, Application 587, Infrastructure 347, Api 517), 14 mutaciones detectadas y verificación contra la API publicada, incluida la migración sobre sesiones reales de la versión anterior. **Pendiente:** commit, push, PR y merge (órdenes explícitas) y el bloque 4 (frontend).

---

## Convención

- Cada tarea completable y verificable en una sesión.
- Se marca `[x]` solo cuando cumple la Definición de «Done»: compila, tests en verde, mutaciones detectadas y manual al día.
- Las mutaciones se hacen a mano sobre el código real, con copia de seguridad, **dirigidas por número de línea**, con aviso si la mutación no cambió el fichero y con **`touch` al restaurar** (si no, MSBuild reutiliza el ensamblado compilado con la mutación anterior).
- Verificación en vivo con la receta de siempre (base desechable, API real, script fuera del repositorio). Esta spec añadió **una prueba con datos antiguos**: sesiones creadas por la versión anterior de la API (exportada con `git archive` a una ruta corta; un `git worktree` falla en Windows por la longitud de las rutas de `obj/`) y la migración aplicada encima.

---

## Bloques

### Bloque 1 — El Id de sesión (Application + Infrastructure, con migración)
- [x] `OfiFlowClaimTypes.SessionId = "sid"` y `SecurityEventIds.SessionTokenRejected = 1009`
- [x] `RefreshToken.SessionId` (Guid): lo recibe el constructor; configuración con índice en `SessionId` (incluyendo `UserId`, `RevokedAt` y `ExpiresAt`)
- [x] Migración `AddRefreshTokenSessionId`: columna anulable → `SessionId = Id` en las filas existentes → `NOT NULL` sin valor por defecto, e índice. EF la generaba con un Guid vacío por defecto (todas las filas habrían sido una sola sesión): corregida a mano. Probada con filas de la migración anterior y con el `Down`
- [x] `TokenService.IssueTokensAsync` crea una sesión **nueva**; `RotateRefreshTokenAsync` **conserva** el `SessionId` del token anterior; el access token lleva `sid`
- [x] Tests (`TokenServiceTests`, 5): cada login da un `sid` distinto; el token trae el claim con el `SessionId` del refresh token; renovar conserva el `sid` (una y tres veces) en el token y en la fila nueva; renovar un dispositivo no cambia el del otro
- [x] [manual] Capítulo 6 (qué es una sesión, «El token muere con su sesión»)

### Bloque 2 — La comprobación (Infrastructure + Api)
- [x] `ITokenService.IsSessionActiveAsync(userId, sessionId, ct)` y su implementación en `TokenService` (la lectura de `RefreshTokens` sigue en el único fichero de la lista blanca)
- [x] `SessionTokenValidation` conectado al `OnTokenValidated` del esquema de las empresas: sin `sid`, `sid` que no es un Guid, o sesión no activa ⇒ `context.Fail` ⇒ `401` y evento `1009` (motivo y `UserId`; nunca el token ni el `sid`)
- [x] El esquema de la plataforma queda **intacto**
- [x] Tests de `IsSessionActiveAsync` (Infrastructure, 10): recién abierta; tras logout; tras «cerrar todas»; tras borrar los tokens; tras una rotación; con la fila caducada; con el Id de sesión de otra cuenta; desconocida o vacía; cerrar una deja la otra; solo lee
- [x] Tests con la API entera y SQL Server real (`SessionTokenEndToEndTests`, 19): ver capítulo 9
- [x] Ajustar los tests y verificaciones anteriores que daban por bueno el «límite de 30 minutos» (specs 013 y 014): los dos `...KnownLimit` esperan `401`; «miembro quitado» pasa de `403` a `401` con variantes que conservan la cobertura del `403`; los ayudantes `MintToken` abren una sesión de verdad; el límite de peticiones y el segundo factor se reescribieron
- [x] [manual] Capítulos 6 y 8 (autenticación antes que autorización; el límite del access token deja de existir; evento `1009`)

### Bloque 3 — Mutaciones, verificación real y cierre
- [x] Mutaciones sobre el código real: 14, todas detectadas (no comprobar la sesión; no comprobar el `UserId`; aceptar un token sin `sid`; renovar con un `sid` nuevo en el token y en la fila; que todos los logins compartan sesión; contar los revocados y los caducados como activos; comprobar también el esquema de la plataforma; no registrar el evento; ignorar el Id de sesión en la consulta; no borrar los refresh tokens; emitir el token sin `sid`; quitar la comprobación del esquema de las empresas). Cazadas por Infrastructure (la regla de «activa» y la emisión) y por la Api (el cableado)
- [x] Verificación contra la API publicada de verdad (scripts fuera del repositorio, base desechable, ya borrada): migración sobre dos sesiones reales anteriores, tokens anteriores `401`, refresh anterior renueva con el `sid` de la fila antigua, login nuevo es otra sesión, cerrar una no toca la otra (al instante), cerrar todas, ruta anónima con token cerrado, `401` con solo `invalid_token`, eventos `1008` y `1009` sin tokens ni `sid`
- [x] Medir el coste: mediana 12,17 → 13,66 ms y p95 30,06 → 32,35 ms (300 peticiones, local); plan `Index Seek` en `IX_RefreshTokens_SessionId`, 2 lecturas lógicas, sin `Key Lookup` ni `Scan`
- [x] [manual] Capítulo 9 (cifras y tests nuevos), capítulos 1, 3, 6, 8 y el anexo del tribunal, `docs/README.md`, **ADR-018** nueva, relación en ADR-007, ADR-013 y ADR-014, y las specs 010, 013 y 014 (el límite deja de ser conocido)
- [x] Suite completa, proyecto a proyecto
- [ ] [externo] Commit, push de la rama, PR y merge (con `--merge`), cada uno con confirmación explícita

### Bloque 4 — Frontend (repositorio `ofiflow-frontend`)
- [ ] Verificar en vivo que los flujos que cierran sesión siguen bien: «Cerrar todas mis sesiones», activar y desactivar el segundo factor (con la API real: ahora la petición siguiente da `401`)
- [ ] El aviso «Tu sesión se cerró. Vuelve a entrar.» cuando la renovación falla tras un `401` (hoy se vuelve al login sin explicación)
- [ ] Tests: el aviso aparece cuando la sesión se cerró en otro sitio y no aparece en un `logout` normal (que ya lleva el suyo o ninguno)
- [ ] Comprobación en vivo y commit (con orden explícita)
