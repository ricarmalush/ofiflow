# Tasks — 013-server-logout

**Spec relacionada:** [./spec.md](./spec.md)
**ADR relacionada:** [ADR-007](../../adr/ADR-007-identity-refresh-token.md) (se amplía con el cierre voluntario; sin ADR nueva)
**Estado general:** Implementada (2026-10-28). Bloques 1–5 hechos; el cierre externo del backend es el PR #48 (el frontend ya está publicado en `ofiflow-frontend`, 53fe055).

---

## Convención

- Cada tarea completable y verificable en una sesión.
- Se marca `[x]` solo cuando cumple la Definición de «Done»: compila, tests en verde, mutaciones detectadas y manual al día.
- Las mutaciones se hacen a mano sobre el código real, con copia de seguridad, y se anotan aquí.

---

## Bloques

### Bloque 1 — El caso de uso (Application)
- [x] `SecurityEventIds.SessionClosed = 1007`
- [x] `ITokenService.RevokeRefreshTokenAsync(refreshToken, ct)` → `bool`
- [x] `LogoutCommand` (`[AllowAnonymousRequest]`), su validador (mismas reglas de forma que `RefreshTokenCommand`) y su handler
- [x] Entra en la tabla «operación → permiso» como anónima (37 operaciones, 8 anónimas)
- [x] Tests con dobles: el handler delega y no devuelve nada; el validador (vacío, nulo, 200 y 201 caracteres)
- [x] [manual] Capítulo 3 (carpeta nueva)

### Bloque 2 — La revocación (Infrastructure)
- [x] `TokenService.RevokeRefreshTokenAsync`: busca por hash; si está activo lo revoca (una sola fila), guarda y registra `1007`; si no, no hace nada ni escribe nada
- [x] Lista blanca de lectura de `RefreshTokens`: no hizo falta tocarla, `TokenService.cs` ya estaba (se busca por el hash del token, que es secreto y único)
- [x] Tests (desviación anotada): los de `TokenServiceTests` usan la base en memoria, como los que ya había; el SQL Server real lo ejercen los 13 de extremo a extremo del bloque 3. Cubren: revoca solo ese token; dos sesiones de la misma cuenta (la otra sigue); desconocido, caducado y ya revocado no cambian la base de datos y no revocan la cadena; no se guarda ni se registra el token ni su hash; el evento solo cuando cierra de verdad
- [x] [manual] Capítulo 6 (identidad: ciclo de vida del refresh token)

### Bloque 3 — La ruta (Api)
- [x] `POST /api/v1/auth/logout` (anónimo), `204`, política `auth-logout` 20/min/IP (cubo propio)
- [x] Tests con la API entera: cierra y `/auth/refresh` con ese token da `401` (y dispara `1003`, R6); repetir el cierre y el token inventado dan `204`; cuerpo vacío o largo da `400`; empresa suspendida y miembro quitado cierran bien (R5); dos dispositivos; el límite da `429` y no gasta el de renovar; sin cuerpo con pistas
- [x] Sección en la colección `OfiFlow.Api.http`
- [x] [manual] Capítulos 0 (tabla de rutas) y 8 (evento 1007, límite de peticiones y límite 3 de sesiones); el 5 no cambia porque no hay lista blanca nueva

### Bloque 4 — Mutaciones, verificación real y cierre
- [x] Mutaciones sobre el código real (sin revocar, revocando todos los tokens de la cuenta, borrando la fila, disparando la cadena con un token ya revocado, sin comprobar `IsActive`, registrando el hash, sin límite, compartiendo el cubo con renovar, exigiendo sesión, `401` para el token inválido...)
- [x] Verificación contra la API publicada de verdad (script fuera del repositorio)
- [x] [manual] Capítulo 9 (cifras y tests nuevos), `docs/README.md`, ADR-007 (relación) y estado de la spec y de las tareas
- [x] Suite completa, proyecto a proyecto
- [ ] [externo] Commit (b3a6111) y push hechos, PR #48 abierto; falta el merge, que se hace con confirmación explícita

### Bloque 5 — Frontend (repositorio `ofiflow-frontend`)
- [x] `AuthService.logout()` avisa al servidor (sin esperar, sin reintentar) y borra lo local **siempre**; no se llama con la sesión que ya se cerró; el interceptor no entra en bucle con esta ruta
- [x] Tests: con red, sin red y con error del servidor, siempre se limpia la sesión; el cuerpo lleva el refresh token; sin refresh token no hay llamada
- [x] Comprobación en vivo contra la API y commit (53fe055): con el navegador integrado, al pulsar «Salir» sale `POST /auth/logout` → 204, el token copiado da `401` en `/auth/refresh` (con evento `1003`) y sin red la sesión local también se limpia

---

## Resultado de las mutaciones (bloque 4)

14 mutaciones sobre el código real, con copia de seguridad: 13 detectadas y 1 equivalente. La equivalente (`RequireAuthorization()` en la ruta) no cambia el comportamiento porque el grupo `/auth` es `AllowAnonymous` y esa marca gana; el «exigir sesión» real (quitar `[AllowAnonymousRequest]`) rompe 13 tests. La mutación de registrar el hash la cazó solo Infrastructure la primera vez: se reforzó el test de la API para que revise todo el registro y se repitió (detectada). Una primera tanda apuntó sin querer a la rotación (el script cambió la primera coincidencia); se repitió dirigiendo por número de línea.

Cifras tras el bloque: Domain 229, Application 584, Infrastructure 323, Api 474 = **1610 casos**, todos en verde.

## Frontend (bloque 5): resultado

`AuthService.logout()` lee el refresh token, borra lo local primero y siempre, y avisa a `POST /auth/logout` sin esperar ni reintentar; sin refresh token no hay llamada. 152 tests (8 nuevos), 7 mutaciones a mano, todas detectadas (la de quitar el manejador de error la caza Karma como error no capturado).
