# Spec — 014-logout-all

**Estado:** Implementada (API, 2026-10-28; el frontend es el bloque 5). Decisiones D1–D8 aprobadas el 2026-10-28 tal como estaban escritas
**Fecha:** 2026-10-28
**Bounded Context:** Identity (sesiones)
**Depende de:** spec 013 (el cierre de una sesión y su detección de reutilización), ADR-007 (refresh token con rotación), ADR-009 (eventos de seguridad, límite de peticiones), ADR-012 (permisos), ADR-013 (la pertenencia y el rol se revalidan en cada petición)

---

## Objetivo

Que una persona pueda **cerrar todas sus sesiones a la vez**, en todos sus dispositivos, con una sola acción. Es el caso de **perder el móvil o el portátil**, o de sospechar que alguien tiene una copia de su sesión: cerrar solo la sesión de *este* dispositivo (spec 013) no ayuda, porque la que hay que cerrar es la de *otro*.

Hoy ese efecto solo existe como consecuencia de otras acciones (activar o desactivar el segundo factor, quitar a un miembro); no se puede pedir por sí solo.

## Alcance — Qué SÍ incluye

1. **`DELETE /api/v1/account/sessions`** (autenticada): revoca **todos los refresh tokens activos de la cuenta** de quien lo pide, incluido el del dispositivo desde el que se pide.
2. **`204` sin cuerpo**, también cuando no había ninguna sesión activa (idempotente).
3. Un **evento de seguridad** (`1008`) con quién lo hizo y **cuántas sesiones** cerró (solo Ids y un número).
4. Un **permiso nuevo** `Account.CloseSessions` para los cinco roles (la persona decide sobre sus propias credenciales).
5. **Límite de peticiones** propio.
6. **Frontend (segundo paso):** un botón «Cerrar todas mis sesiones» en la pantalla de Seguridad, con confirmación; al terminar, cierra la sesión local y lleva al login con un aviso.

## Alcance — Qué NO incluye

- **NEXT:** **invalidar los access tokens ya emitidos.** Son JWT sin estado y valen hasta 30 minutos. Tras cerrar todas las sesiones, quien tenga un access token copiado lo conserva hasta que caduque, pero **ya no puede renovarlo**. Es el mismo límite de la spec 013 y se resuelve con su propio NEXT (ver «Decisiones», D5).
- **NEXT:** **listar mis sesiones** («Chrome en Windows, hace 2 h») y cerrar **una concreta** que no sea la actual. Exige guardar datos de dispositivo en cada refresh token (agente, IP, fecha), con su propia decisión de privacidad.
- **NEXT:** purgar los refresh tokens caducados o revocados (hoy se acumulan filas).
- **NEVER:** que una persona pueda cerrar las sesiones **de otra** por esta ruta. No hay Id ni parámetro: la cuenta sale del token. (Cerrar las de otro miembro, si algún día hace falta, es una operación de gestión distinta, con la jerarquía de roles de la spec 009.)

## Reglas de negocio / invariantes

- **R1.** La operación afecta **solo a la cuenta autenticada**. La ruta no tiene Id, cuerpo ni parámetros: el `UserId` sale del token.
- **R2.** Se revocan (`RevokedAt = ahora`) **todos los refresh tokens activos** de esa cuenta, en **todas** sus empresas si tuviera varias. Los ya revocados o caducados no se tocan (no cambia su fecha).
- **R3.** Se **revoca, no se borra** (como en la spec 013 D3): si alguien usa después uno de esos tokens en `/auth/refresh`, se trata como reutilización de un token revocado (evento `1003`).
- **R4.** La respuesta es **`204`** haya o no sesiones activas. Quien pide cerrar ya sabe quién es; no hay nada que ocultar, pero tampoco hace falta un `404` para «no había ninguna».
- **R5.** Exige **sesión de empresa válida** y el permiso `Account.CloseSessions` (lo comprueba el `AuthorizationBehavior`, que además revalida en la base de datos la pertenencia, el rol y que la empresa no esté suspendida). Un token de plataforma recibe `401`. Una persona de una empresa suspendida recibe el `403 tenant.suspended` de siempre; para salir sigue teniendo `POST /auth/logout`.
- **R6.** Un evento **`1008`** por cada petición, con el `UserId` y el **número de sesiones cerradas** (cero incluido): es una acción explícita y rara de una persona identificada, y «alguien pidió cerrar todo y no había nada» es información útil. Nunca lleva tokens ni hashes.
- **R7.** Tiene **límite de peticiones por IP** propio (acción rara; 10 por hora).
- **R8.** La respuesta lleva **`Cache-Control: no-store`**, igual que el resto de operaciones de la cuenta.

## Modelo conceptual

```
Domain/Tenancy
- Permission.AccountCloseSessions  → nombre "Account.CloseSessions"   (los cinco roles)

Application/Common/Abstractions
- ITokenService   + RevokeAllRefreshTokensAsync(userId, ct)   → int   (cuántas sesiones activas cerró)

Application/Identity/Commands/CloseAllSessions
- CloseAllSessionsCommand        [RequiresPermission(Permission.AccountCloseSessions)]   → Unit   (sin parámetros)
- CloseAllSessionsCommandHandler  UserId de ICurrentUser → ITokenService; el evento lo registra el servicio

Infrastructure/Identity
- TokenService.RevokeAllRefreshTokensAsync   los activos del UserId → Revoke() + SaveChanges + evento 1008

Application/Common/Logging
- SecurityEventIds.AllSessionsClosed = 1008

Api
- DELETE /api/v1/account/sessions   (autenticada, límite "account-sessions" 10/hora/IP, 204, no-store)
```

**Dos detalles descubiertos al implementar** (no cambian ninguna decisión):
- **El nombre estable del permiso.** `PermissionNames` ponía un punto en *cada* frontera de palabra, así que `AccountCloseSessions` salía como `Account.Close.Sessions` y no como `Account.CloseSessions`. Ahora parte solo por la **primera** frontera (`Entidad.Acción`) y el test de formato admite una acción de varias palabras. Los doce nombres anteriores no cambian (un test los fija uno a uno).
- **El nivel del evento `1008` es `Warning`** (como `1506` o el reinicio del segundo factor): es una acción rara que un analista quiere ver, no ruido de `Information`.

**Sin migración**: `RefreshToken.RevokedAt` ya existe. Operaciones tras esta spec: **38** (27 con permiso, 8 anónimas y 3 de plataforma). Permisos: **13** (12 + `Account.CloseSessions`); la matriz pasa de 60 a 65 celdas.

## Criterios de aceptación

- Dada una cuenta con **tres sesiones** activas (tres dispositivos), cuando una de ellas pide cerrar todas, entonces `204` y **las tres** dejan de poder renovar (`/auth/refresh` da `401` con cada token).
- Dada una cuenta **sin ninguna sesión activa** (todas cerradas o caducadas), entonces `204` y el evento `1008` lleva un recuento de **cero**.
- Dadas **dos cuentas** distintas con sesiones, cuando la A cierra todas, entonces las de B **siguen renovando** (R1).
- Dada una cuenta con una sesión **ya revocada** (rotada o cerrada con la spec 013), entonces su `RevokedAt` **no cambia** y no cuenta en el recuento.
- Dado un token cerrado con esta operación, cuando alguien lo usa en `/auth/refresh`, entonces `401` y evento `1003` (R3).
- Dado un **parámetro o cuerpo con otro `userId`** (de otra cuenta), entonces se **ignora**: se cierran las de quien pide (R1).
- Dado **sin token**, con basura o con un token de **plataforma**, entonces `401`. Dada una empresa **suspendida**, `403 tenant.suspended`. Dado un miembro **quitado** con el token aún válido, `403` (revalidación de ADR-013).
- Dados los **cinco roles**, todos pueden cerrar **sus** sesiones (la matriz aprobada).
- Dadas **más de 10 peticiones por hora** desde la misma IP, entonces `429` con `Retry-After`.
- Dado un cierre, entonces se escribe **un** evento `1008` con `UserId` y recuento, y **ni tokens ni hashes ni emails**; y la respuesta lleva `Cache-Control: no-store`.

**Guardarraíles**
- Dada una operación autenticada nueva sin su `[RequiresPermission]`, entonces falla el test de arquitectura (ya existe; esta operación entra en él).
- Dado que se cambie `Revoke()` por un borrado, o que se revoquen los tokens de **otra** cuenta, entonces falla un test.
- Dado que se añada un permiso sin su celda en la matriz o sin su nombre estable, entonces falla el test del dominio (ya existe).

## Seguridad (ADR-009)

- **Autenticación/autorización:** exige sesión de empresa y `Account.CloseSessions` (declarado en el comando). Entra en la tabla «operación → permiso» (38). La ruta cuelga del grupo `/account`, que ya es `RequireAuthorization()`: a diferencia de `/auth`, **ese grupo no es anónimo**, así que no hay marca que la anule (lección de la spec 013: una ruta autenticada dentro de un grupo `AllowAnonymous` seguiría siendo anónima en HTTP).
- **Aislamiento entre empresas:** `RefreshTokens` es de lectura global (sin filtro de empresa). La consulta filtra **por el `UserId` del token**, nunca por un dato de la petición. Va a la lista blanca de lectura, que ya contiene `TokenService.cs`.
- **Entrada:** ninguna (ni cuerpo ni parámetros): nada que validar ni que falsificar.
- **Datos personales:** ninguno en la respuesta; el log lleva solo el `UserId` y un número.
- **Abuso:** límite por IP (R7). Es barata (una consulta por `UserId` y una escritura). Un atacante con un access token robado **podría** cerrar las sesiones de la víctima (denegación, no escalada: le quita acceso, no se lo da; la víctima vuelve a entrar con su contraseña). Es un coste aceptado y no se mitiga pidiendo contraseña (ver D4).
- **Lo que esta spec NO protege:** el **access token** ya emitido sigue valiendo hasta 30 min. Es el caso de «perdí el móvil»: el ladrón conserva ese margen, aunque ya no puede renovar. Ver D5.
- **Carrera conocida (riesgo aceptado):** si en el mismo instante hay un `/auth/refresh` en vuelo para esa cuenta, puede crear un token **nuevo** justo después de que esta operación revoque los que había, y ese sobrevive. La ventana es de milisegundos y repetir la operación lo cierra. No se añade bloqueo (coste y complejidad desproporcionados para el riesgo); queda escrito.

## Decisiones técnicas relevantes (aprobadas el 2026-10-28)

- **D1. Ruta:** **`DELETE /api/v1/account/sessions`**, en el grupo `/account` (autenticado). *Recomendado.* La spec 013 la anotó como `POST /auth/logout-all`; se cambia porque el grupo `/auth` es anónimo y una ruta autenticada ahí obligaría a distinguirla de las demás (y la marca `AllowAnonymous` del grupo gana a `RequireAuthorization()`). Además es REST honesto: borrar mis sesiones. Alternativa: `POST /auth/logout-all` con una excepción de grupo.
- **D2. Permiso:** uno **nuevo**, `Account.CloseSessions`, para los cinco roles. *Recomendado.* Reutilizar `Account.Read` o `Mfa.Manage` mezclaría «qué se hace» (la spec 012 ya razonó que un permiso dice qué se hace, no quién lo tiene). Coste: una celda más en la matriz (60 → 65) y un nombre más que el frontend debe conocer.
- **D3. Revocar o borrar:** **revocar** (`RevokedAt`), como en la spec 013. *Recomendado.* Conserva la señal: si un ladrón usa después su token copiado, `/auth/refresh` lo ve como reutilización (`1003`). **Coste a conocer:** esa reutilización también revoca los tokens activos de la cuenta, así que si la víctima ya volvió a entrar, el intento del ladrón **le cierra la sesión nueva** (falla hacia cerrar, el mismo efecto que la spec 013 D3). Alternativa: borrar las filas (como hacen hoy el segundo factor y la baja de miembros): el token del ladrón pasa a ser «desconocido» y la sesión nueva de la víctima no se toca, pero se pierde el evento `1003` y la señal de que había una copia.
- **D4. ¿Pide la contraseña?:** **no.** *Recomendado.* La operación solo **quita** acceso, nunca lo da, y se usa en el momento más urgente (móvil perdido) en el que añadir fricción perjudica; además, quien tenga la sesión robada solo podría molestar (cerrar la sesión de la víctima), no escalar. Contraseña y código sí se piden para **activar o desactivar** el segundo factor porque eso cambia las credenciales. Alternativa: pedirla, con su coste en el bloqueo de cuenta (un fallo suma) y un campo más en el cuerpo.
- **D5. Alcance respecto al access token:** **solo los refresh tokens** (esta spec) y dejar la invalidación del access token para una spec propia. *Recomendado.* Es el trabajo mayor: un instante «sesiones válidas desde» por cuenta (o un `sid` por sesión) que se compara con la emisión del token en la consulta de pertenencia que ya se hace en cada petición, con migración y cambio en la ruta caliente. Sirve igual a esta operación y a la spec 013. Mientras tanto el límite queda dicho (30 min). Alternativa: hacerlo ya aquí, con una spec más grande y una migración.
- **D6. Evento:** **uno solo, `1008 AllSessionsClosed`**, en **cada** petición, con el recuento (cero incluido). *Recomendado.* A diferencia del cierre de una sesión (spec 013, solo si cierra algo), esta es una acción explícita y rara de una persona identificada. Alternativa: solo si cierra algo.
- **D7. Límite de peticiones:** política propia **`account-sessions`, 10/hora/IP** (como el resto de la gestión de la cuenta). *Recomendado.*
- **D8. Orden de trabajo:** primero la API (esta spec), luego el frontend: botón en Seguridad, con confirmación, que llama a la API y después cierra la sesión local con un aviso («Se cerraron todas tus sesiones»). *Recomendado.* No necesita llamar a `/auth/logout` después: su token ya está revocado y el cierre local sigue siendo siempre.
- No requiere ADR nueva: **amplía ADR-007** (el ciclo de vida del refresh token gana el cierre de todos a petición de su dueño) y **ADR-012** (un permiso más); se anota en su «Relación con otros ADR».

## Fuera de alcance / Backlog relacionado

- Invalidar el access token (`sid` o «sesiones válidas desde»).
- Listar mis sesiones y cerrar una concreta.
- Purga periódica de refresh tokens caducados o revocados.
- «Desasignar» un trabajo: otro hueco de la API, aparte.

---

## Validación antes de aprobar

> ¿Esta funcionalidad ayuda a conseguir o mantener al primer cliente? (sección 84 del prompt maestro)

**Sí.** Un técnico que pierde el móvil de empresa (con la sesión abierta y datos de clientes) tiene que poder cortar el acceso en un minuto, sin esperar a que un administrador le quite de la empresa. Es pequeña (una ruta, una revocación, sin migración) y reutiliza lo de la spec 013; no abre superficie anónima y su peor abuso es cerrarle la sesión a su propio dueño.
