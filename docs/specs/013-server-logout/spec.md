# Spec — 013-server-logout

**Estado:** Implementada (2026-10-28): API (PR #48) y frontend (`ofiflow-frontend`, 53fe055). Decisiones D1–D7 aprobadas
**Fecha:** 2026-10-27
**Bounded Context:** Identity (sesiones)
**Depende de:** ADR-007 (refresh token con rotación y detección de reutilización), ADR-009 (seguridad base: eventos de seguridad, límite de peticiones), ADR-013 (la pertenencia y el rol se revalidan en cada petición), ADR-016 (la empresa suspendida)

---

## Objetivo

Que **cerrar la sesión** deje de ser solo «el navegador se olvida de sus tokens» y pase a **invalidar la sesión en el servidor**. Hoy el frontend borra los tokens de `sessionStorage`, pero el refresh token sigue vivo en la base de datos hasta 7 días: si alguien lo copió antes (un portátil compartido, una extensión maliciosa, un volcado de memoria), puede seguir renovando sesión **después** de que la persona haya pulsado «Cerrar sesión». Es el fallo clásico «el logout es de mentira» (OWASP Session Management: la sesión debe invalidarse en el servidor al cerrarla).

## Alcance — Qué SÍ incluye

1. **`POST /api/v1/auth/logout`** con el refresh token en el cuerpo: lo **revoca** en el servidor. Cierra **esa** sesión (ese dispositivo), no las demás.
2. Que el endpoint sea **idempotente y sin oráculo**: responde siempre `204`, exista o no el token, esté activo, caducado o ya revocado.
3. Un **evento de seguridad** cuando se cierra una sesión de verdad (quién: solo Ids).
4. Que un token cerrado **no vuelva a servir**: usarlo después en `/auth/refresh` se trata como reutilización de un token revocado (ADR-007), con su evento y la revocación de la cadena.
5. **Frontend (segundo paso, repositorio `ofiflow-frontend`):** `AuthService.logout()` avisa al servidor y borra lo local **siempre**, haya o no red.

## Alcance — Qué NO incluye

- **NEXT:** **«cerrar todas mis sesiones»** (`POST /auth/logout-all`, autenticada): útil tras perder un móvil. Hoy ya existe el efecto por otras vías (activar o desactivar el segundo factor, quitar un miembro).
- **NEXT:** **invalidar el access token** al cerrar sesión. Es un JWT sin estado y vive hasta 30 minutos; revocarlo exige llevar un identificador de sesión (`sid`) en el token y comprobarlo en cada petición (la consulta de pertenencia de ADR-013 ya está ahí y podría hacerlo). **Esta spec no lo hace y lo dice:** tras cerrar sesión, un access token copiado sigue sirviendo hasta que caduque, pero **ya no se puede renovar**.
- **NEXT:** purgar los refresh tokens caducados o revocados (hoy se acumulan filas; no hay tarea de limpieza).
- **NEVER:** que el logout necesite que el access token siga vivo (cerrar sesión con la sesión caducada tiene que poder hacerse), ni que revele si un token existía.

## Reglas de negocio / invariantes

- **R1.** El logout recibe el **refresh token** (es la credencial de la sesión: quien lo tiene puede renovarla, así que también puede cerrarla). **No exige access token**: la sesión puede haber caducado y la persona sigue teniendo derecho a cerrarla.
- **R2.** Un refresh token **activo** se **revoca** (`RevokedAt = ahora`) y se registra el evento. Es **una sola fila**: la de esa sesión. La rotación garantiza que una sesión tiene un único token activo.
- **R3.** Un token **desconocido, caducado o ya revocado** no cambia nada y responde igual (`204`). En particular, cerrar con un token **ya revocado NO dispara la revocación de la cadena** (eso es para `/auth/refresh`: ahí presentarlo es un uso; aquí es un cierre).
- **R4.** Respuesta **idéntica** en todos los casos válidos de forma (`204`, sin cuerpo, sin pistas): quien no tiene el token no aprende nada. Un cuerpo mal formado (vacío o demasiado largo) sí es `400`, igual que en `/auth/refresh`: esa respuesta no depende de ningún estado del servidor.
- **R5.** Cerrar sesión **no se bloquea por la empresa**: una empresa suspendida o un miembro ya quitado pueden cerrar la que tuvieran (no hay motivo para negar que se invalide una credencial).
- **R6.** Tras el logout, **`/auth/refresh` con ese token falla** y, por ser un token revocado, se trata como reutilización (evento `1003` y revocación de los activos de la cuenta): un token que el cliente legítimo ya destruyó y alguien usa es, por definición, una copia.
- **R7.** El evento de seguridad lleva **solo Ids** (`UserId`, `TenantId`) y la IP del scope de la API: nunca el token ni su hash.
- **R8.** El endpoint tiene **límite de peticiones por IP** propio (no comparte cubo con `/auth/refresh`: cerrar sesión no debe agotar el presupuesto de renovar).

## Modelo conceptual

```
Application/Common/Abstractions
- ITokenService   + RevokeRefreshTokenAsync(refreshToken, ct)   → bool   (true si cerró una sesión activa)

Application/Identity/Commands/Logout
- LogoutCommand(RefreshToken)   [AllowAnonymousRequest]   → Unit
- LogoutCommandValidator        (mismas reglas de forma que RefreshTokenCommand)
- LogoutCommandHandler          llama a ITokenService; el evento lo registra el servicio (como los de refresh)

Infrastructure/Identity
- TokenService.RevokeRefreshTokenAsync   busca por hash; si está activo → Revoke() + SaveChanges + evento SessionClosed

Application/Common/Logging
- SecurityEventIds.SessionClosed = 1007

Api
- POST /api/v1/auth/logout   (anónimo, límite "auth-logout" 20/min/IP, 204)
```

**Sin migración**: `RefreshToken.RevokedAt` ya existe. Operaciones tras esta spec: **37** (26 con permiso, 8 anónimas y 3 de plataforma).

## Criterios de aceptación

- Dado un refresh token activo, cuando se cierra sesión con él, entonces `204`, y a partir de ahí `/auth/refresh` con el mismo token responde `401`.
- Dado el **mismo** cierre repetido, o un token inventado, caducado o ya rotado, entonces `204` otra vez y no cambia nada en la base de datos.
- Dado un cierre con un token ya **rotado** (revocado por `/auth/refresh`), entonces `204` y **no se revocan** los demás tokens de la cuenta (R3).
- Dadas **dos sesiones** de la misma cuenta (dos dispositivos), cuando se cierra una, entonces la otra **sigue pudiendo renovar** (R2).
- Dado un cierre de sesión con un token de **otra empresa** o de otra cuenta, entonces solo se cierra la sesión de **ese token**: no hay ningún dato en la petición que escoja a quién se cierra.
- Dada una empresa **suspendida** o un miembro **quitado**, cuando cierran con su token, entonces `204` (R5).
- Dado un cuerpo vacío, nulo o de más de 200 caracteres, entonces `400` con el código de validación de siempre.
- Dadas más de 20 peticiones de cierre en un minuto desde la misma IP, entonces la siguiente recibe `429` con `Retry-After`, y el límite **no** gasta el de `/auth/refresh` (R8).
- Dado un cierre que cierra de verdad, entonces se escribe **un** evento `1007` con `UserId` y `TenantId` y **ni el token ni su hash**; dado uno que no cierra nada, entonces **no** se escribe.

**Guardarraíles**
- Dada una operación anónima nueva sin límite de peticiones, entonces falla el test de arquitectura (ya existe para las rutas de `/auth`; el logout entra en él).
- Dado que se cambie `Revoke()` por un borrado o que el logout revoque más de una fila, entonces falla un test.

## Seguridad (ADR-009)

- **Autenticación/autorización:** anónimo (`[AllowAnonymousRequest]`): la credencial es el propio refresh token, 64 bytes aleatorios (512 bits): no se puede adivinar ni enumerar. Entra en la tabla «operación → permiso» como anónima (8).
- **Aislamiento entre empresas:** `RefreshTokens` es de lectura global (sin filtro de empresa, como en `/auth/refresh`) y la búsqueda es **por hash del token**: no hay Id, empresa ni cuenta en la entrada. Va a la lista blanca de lectura de `RefreshTokens`, con su motivo.
- **Entrada:** solo el token, con la misma validación de longitud que `/auth/refresh` (≤ 200 caracteres).
- **Datos personales:** ninguno en la respuesta; el log lleva solo Ids.
- **Abuso:** límite por IP propio (R8). Es una operación barata (una consulta por índice y, como mucho, una escritura).
- **Sin oráculo (R4):** el atacante que prueba tokens no distingue «inexistente» de «cerrado» de «caducado».
- **Lo que esta spec NO protege:** el **access token** ya emitido sigue valiendo hasta 30 min (ver «Qué NO incluye»). Un atacante que haya copiado el access token (no solo el refresh) mantiene ese margen. Es la razón del NEXT del `sid`.

## Decisiones técnicas relevantes (aprobadas el 2026-10-27, tal como se recomendaban)

- **D1. Ruta y credencial:** `POST /auth/logout` **anónimo con el refresh token en el cuerpo**. *Recomendado.* Alternativa: autenticado con el access token, que impediría cerrar sesión cuando el access token ya caducó (el caso más frecuente en que la persona vuelve y pulsa «Salir») y obligaría al frontend a renovar para poder salir.
- **D2. Alcance del cierre:** **solo la sesión del token** (ese dispositivo). *Recomendado.* «Cerrar en todos» se deja como NEXT con su propia ruta autenticada (lleva su propia decisión: ¿pide la contraseña?).
- **D3. Revocar o borrar la fila:** **revocar** (`RevokedAt`). *Recomendado.* Así el token cerrado, si alguien lo usa después, **dispara la detección de reutilización de ADR-007** (R6): es la señal de que hay una copia. Borrarla lo haría indistinguible de un token inventado y perderíamos esa señal. Coste: la fila se queda (como las rotadas); no empeora la acumulación que ya existe. Un efecto a conocer: si el cliente legítimo hace un `/auth/refresh` en vuelo mientras cierra sesión, ese refresh con el token ya revocado dispara la revocación de la cadena (cierra también los otros dispositivos). Es raro, es seguro (falla hacia cerrar) y el frontend no lo provoca porque cierra sin renovar.
- **D4. Respuesta:** **`204` siempre** para entradas bien formadas (R4). *Recomendado.* Alternativa: `401` para tokens inválidos, que haría de la ruta un comprobador de tokens robados.
- **D5. Evento:** **uno solo, `1007 SessionClosed`**, solo cuando se cierra algo. *Recomendado.* No se registra el intento sin efecto (ruido: el frontend cierra siempre, aunque ya no haya sesión).
- **D6. Límite de peticiones:** **política propia `auth-logout`, 20/min/IP** (mismo número que renovar, otro cubo). *Recomendado.*
- **D7. Orden de trabajo:** primero la API (esta spec), luego el frontend en su repositorio (`AuthService.logout()` llama al servidor sin esperar y **siempre** borra lo local, con o sin red; no reintenta). *Recomendado.*
- No requiere ADR nueva: **amplía ADR-007** (el ciclo de vida del refresh token gana el cierre voluntario) y se anota en su «Relación con otros ADR».

## Fuera de alcance / Backlog relacionado

- `POST /auth/logout-all` (cerrar todas mis sesiones).
- `sid` en el access token y revocación inmediata de la sesión.
- Limpieza periódica de refresh tokens caducados o revocados.
- «Desasignar» un trabajo: otro hueco de la API, aparte.

---

## Validación antes de aprobar

> ¿Esta funcionalidad ayuda a conseguir o mantener al primer cliente? (sección 84 del prompt maestro)

**Sí.** Una oficina con ordenadores compartidos espera que «Cerrar sesión» cierre de verdad; un cliente (o un auditor) que lo pruebe y vea que el token sigue renovando lo cuenta como un fallo de seguridad básico. Es pequeña (una ruta, una revocación, sin migración) y no abre superficie nueva más allá de una ruta anónima con límite y sin oráculo.
