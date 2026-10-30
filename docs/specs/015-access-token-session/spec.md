# Spec — 015-access-token-session

**Estado:** Implementada (API y frontend, 2026-10-30). Decisiones D1–D9 aprobadas el 2026-10-30 tal como estaban escritas
**Fecha:** 2026-10-28
**Bounded Context:** Identity (sesiones)
**Depende de:** spec 013 (cerrar una sesión) y spec 014 (cerrar todas), ADR-007 (refresh token con rotación), ADR-009 (eventos de seguridad), ADR-013 (la pertenencia se revalida en cada petición)

---

## Objetivo

Que **cerrar una sesión corte el acceso de verdad, ahora**, y no 30 minutos después.

Hoy, cuando alguien cierra su sesión (spec 013) o todas (spec 014), se revocan sus **refresh tokens**: nadie puede *renovar*. Pero el **access token** ya emitido es un JWT sin estado: la API solo mira su firma y su caducidad, así que sigue valiendo **hasta 30 minutos**. Es exactamente el caso de «perdí el móvil»: la persona pulsa «Cerrar todas mis sesiones» y el ladrón conserva media hora de acceso a los datos de los clientes.

Las specs 013 y 014 dejaron este límite escrito como NEXT. Esta spec lo cierra.

## Alcance — Qué SÍ incluye

1. **Identificador de sesión (`sid`).** Cada sesión (un inicio de sesión y todas sus renovaciones) tiene un Id estable. El access token lo lleva como claim `sid`.
2. **Validación en cada petición de empresa:** el access token solo vale si su sesión sigue activa, es decir, si existe un refresh token **no revocado y no caducado** de esa sesión y de esa cuenta.
3. **`401`** cuando no vale, para que el frontend haga lo que ya sabe hacer (intentar renovar, y si no puede, volver al login).
4. **Migración**: una columna `SessionId` en `RefreshTokens`, con los datos existentes rellenados, y un índice para la consulta.
5. Un **evento de seguridad** (`1009`) cuando se rechaza un token por su sesión.
6. **Efecto en todos los cierres existentes**, sin tocarlos: cerrar una sesión (013), cerrarlas todas (014), la detección de reutilización (ADR-007), activar o desactivar el segundo factor, quitar a un miembro o reiniciarle el segundo factor. Todos dejan de tener ventana de 30 minutos.

## Alcance — Qué NO incluye

- **NEXT:** **listar mis sesiones** («Chrome en Windows, hace 2 h») y cerrar **una concreta** que no sea la actual (spec 016). Esta spec pone la base (el Id de sesión); la 016 le añade datos de dispositivo, con su propia decisión de privacidad.
- **NEXT:** **purgar** los refresh tokens caducados o revocados (hoy se acumulan filas). Con esta spec importa algo más (la consulta por sesión crece con la tabla), y el índice la mantiene barata; la purga sigue siendo la 016.
- **NEXT:** las sesiones de **operador de la plataforma** (spec 011). No tienen refresh token (solo un access token corto), así que no hay una «sesión» que consultar. Se cierran por otra vía: desactivar al operador, que ya se comprueba en cada petición.
- **NEVER:** una lista negra de tokens (guardar cada token revocado hasta que caduque). Obliga a conocer todos los tokens emitidos y a guardar uno por cada uno.

## Reglas de negocio / invariantes

- **R1. Cada token de sesión de empresa lleva `sid`.** Un inicio de sesión (login, o login con segundo factor) crea una sesión **nueva**, con un Id nuevo. Renovar el token (`/auth/refresh`) **conserva** el Id: es la misma sesión.
- **R2. Una sesión está activa** si existe un refresh token con ese `SessionId` y el mismo `UserId` que el `sub` del token, **sin revocar y sin caducar**. Es la misma definición de «activo» que ya usan los cierres de las specs 013 y 014.
- **R3. Se comprueba en cada petición autenticada** con el esquema de las empresas, **antes** de la autorización (primero se autentica, después se decide qué puede hacer). Si la sesión no está activa, `401` sin cuerpo.
- **R4. Un token sin `sid`, o con un `sid` que no es un Guid, se rechaza** con `401` (fail-closed). Es lo que pasa con los tokens emitidos antes de desplegar esta spec: caducan en 30 minutos de todos modos y, al recibir el `401`, el frontend los renueva con su refresh token (D3).
- **R5. La renovación no deja hueco.** `/auth/refresh` revoca el token anterior y crea el nuevo **en la misma confirmación** a la base de datos: no hay instante en que la sesión no tenga un refresh token activo.
- **R6. Todo cierre existente pasa a ser inmediato**, porque todos dejan a la sesión sin refresh token activo: cerrar una sesión (013), todas (014), reutilización de un token revocado (ADR-007), activar/desactivar el segundo factor, quitar a un miembro, reiniciarle el segundo factor.
- **R7. Las demás sesiones no se tocan.** Cerrar la sesión de un dispositivo deja válidas las de los otros: el `sid` es por sesión, no por cuenta.
- **R8. El esquema de la plataforma no cambia** (spec 011): sus tokens no llevan `sid` ni se consultan.
- **R9. Las rutas anónimas no cambian.** No exigen token; si llega uno con una sesión cerrada, se ignora como cualquier credencial no válida y la ruta se atiende igual como anónima.
- **R10. Un evento `1009 SessionTokenRejected`** por cada rechazo por sesión, con el motivo y el `UserId` si se conoce. Nunca lleva el token, el `sid` ni hashes.

## Modelo conceptual

```
Application/Identity
- OfiFlowClaimTypes.SessionId = "sid"                 (el claim estándar «session id»)

Application/Common/Abstractions
- ITokenService  + IsSessionActiveAsync(userId, sessionId, ct)  → bool

Application/Common/Logging
- SecurityEventIds.SessionTokenRejected = 1009

Infrastructure/Identity
- RefreshToken.SessionId (Guid)           una sesión = la cadena de refresh tokens de un login
- TokenService.IssueTokensAsync           sesión NUEVA (Guid nuevo) → refresh token y access token con ese sid
- TokenService.RotateRefreshTokenAsync    la MISMA sesión (copia el SessionId del token anterior)
- TokenService.GenerateAccessToken        + claim sid
- TokenService.IsSessionActiveAsync       AnyAsync(SessionId, UserId, no revocado, no caducado)
- DependencyInjection: JwtBearer del esquema de empresas  OnTokenValidated → si no hay sid válido o la sesión no está
                       activa: context.Fail(...) → 401, y evento 1009

Infrastructure/Persistence
- RefreshTokenConfiguration: índice en SessionId (con UserId, RevokedAt, ExpiresAt incluidas: la consulta no toca la tabla)
- Migración AddRefreshTokenSessionId: columna NOT NULL; las filas existentes: SessionId = Id (cada una, su propia sesión)
```

**La lectura de `RefreshTokens` sigue en `TokenService.cs`**, la única ruta de la lista blanca del test de arquitectura: el evento de JwtBearer llama a `ITokenService.IsSessionActiveAsync`, no consulta la tabla por su cuenta.

**Operaciones y permisos no cambian** (38 operaciones, 13 permisos): esta spec no añade rutas. Es una condición previa a todas las de empresa.

**Cosas descubiertas al implementar** (no cambian ninguna decisión):
- **EF genera la migración con un Guid vacío por defecto**, lo que habría convertido todas las filas existentes en **una sola sesión** (cerrar una cerraría todas). Se rehízo a mano: columna anulable, `UPDATE ... SET SessionId = Id`, y solo entonces `NOT NULL` y sin valor por defecto. Se comprobó con sesiones reales de la versión anterior (D5).
- **Los tests que fabricaban tokens a mano** (`MintToken`) dejaron de valer: la API rechaza un token sin sesión activa, que es justo el comportamiento nuevo. Ahora abren una sesión de verdad por el servicio real y ponen su `sid`.
- **El «límite conocido» de 30 minutos estaba escrito en tests** de las specs 013 y 014 (con su nombre: `...KnownLimit`). Cambiaron a esperar `401` y se renombraron. Los de «miembro quitado» pasan de `403` a `401` (la baja borra los refresh tokens, así que el token se rechaza antes de la autorización); la cobertura del `403` de la revalidación se conserva con variantes que quitan la pertenencia sin tocar las sesiones.
- **El recuento cero de «cerrar todas» ya no se alcanza por HTTP** (spec 014 R4): quien llama siempre tiene al menos su propia sesión activa, y una segunda llamada con el mismo token da `401`. Sigue existiendo en el servicio y lo cubren sus tests.
- **Dos rechazos más de lo que parece en el registro:** una ruta anónima con un token de sesión cerrada se atiende como anónima (R9), pero el evento `1009` se escribe igualmente (el token **se presentó** y se rechazó como credencial).
- **Coste medido** (D6): 300 peticiones a `GET /account/me` contra la versión anterior y la nueva, en local: mediana 12,17 → 13,66 ms, p95 30,06 → 32,35 ms. Plan: `Index Seek` en `IX_RefreshTokens_SessionId`, 2 lecturas lógicas, sin `Key Lookup` ni `Scan`.

## Criterios de aceptación

- Dada una sesión, cuando se cierra con `POST /auth/logout` (spec 013), entonces su access token da **`401`** en la petición siguiente (antes valía 30 min).
- Dadas tres sesiones de una cuenta, cuando una pide `DELETE /account/sessions` (spec 014), entonces los **tres** access tokens dan `401`.
- Dadas **dos sesiones** de la misma cuenta, cuando se cierra **solo una** con `/auth/logout`, entonces el access token de la otra **sigue valiendo** (R7).
- Dados **dos inicios de sesión** de la misma cuenta, entonces tienen **`sid` distintos**.
- Dada una **renovación**, entonces el token nuevo lleva el **mismo `sid`**, y el access token anterior (de la misma sesión, aún sin caducar) sigue valiendo.
- Dado un refresh token **revocado que se reutiliza** (ADR-007), entonces los access tokens de las sesiones que esa reutilización cerró dan `401`.
- Dado activar o desactivar el segundo factor, quitar a un miembro o reiniciarle el segundo factor, entonces los access tokens de esa cuenta dan `401` en su siguiente petición.
- Dado un token **sin `sid`**, o con un `sid` que **no es un Guid**, o con un `sid` **que no existe**, o con el `sid` **de otra cuenta** (el `sub` no coincide), entonces `401`.
- Dado un token de **plataforma**, entonces su comportamiento **no cambia** (R8). Dada una **ruta anónima** con un token de sesión cerrada, entonces se atiende como anónima (R9).
- Dado un rechazo, entonces se escribe **un** evento `1009` con motivo y `UserId`, y **ni el token ni el `sid` ni hashes**.
- Dada una **base de datos con refresh tokens anteriores** a la migración, cuando se aplica, entonces cada fila queda con un `SessionId` válido, y una sesión que estaba activa **sigue pudiendo renovar**.

**Guardarraíles**
- Dado que se lea `RefreshTokens` fuera de `TokenService.cs`, entonces falla el test de arquitectura (ya existe).
- Dado que el esquema de las empresas se configure sin la comprobación de sesión, entonces falla un test que usa un token de una sesión cerrada.
- Dado que una renovación genere un `SessionId` nuevo en vez de conservar el anterior, entonces falla un test.

## Seguridad (ADR-009)

- **Qué cierra:** el robo de un access token **tras** cerrar sesión. Quien lo tenga copiado deja de poder usarlo en el instante en que su dueño (o la detección de reutilización, o un gestor) cierra la sesión. Es el caso «perdí el móvil» completo: antes se cortaba la renovación; ahora, también el uso.
- **Qué NO cierra:** un access token robado **mientras nadie ha cerrado la sesión**. Mientras la sesión esté activa, el token vale (como cualquier credencial hasta que se revoca). No se detecta que lo use otra persona.
- **Autenticación antes que autorización:** la comprobación va en la autenticación (JwtBearer), no en el `AuthorizationBehavior`. Allí un fallo se convierte en `403` (`ForbiddenException`), y el interceptor del frontend **solo renueva la sesión ante un `401`**; un `403` por sesión cerrada dejaría al usuario atascado sin volver al login (D2).
- **Aislamiento entre empresas:** `RefreshTokens` es de lectura global (sin filtro de empresa). La consulta filtra por el `UserId` **del token ya validado** y por el `SessionId` **del token**, nunca por un dato de la petición. Sin el `UserId`, el `sid` de una cuenta serviría en el token de otra; con él, no.
- **Entrada:** el `sid` sale de un token cuya firma ya se validó (no se puede inventar sin la clave). Aun así se trata como no fiable: si no es un Guid, `401`.
- **Datos personales:** ninguno nuevo; el `sid` es un Guid aleatorio sin relación con la persona.
- **Abuso / disponibilidad:** una consulta por petición autenticada más. Es por un índice que la cubre entera (D6). El coste es real y se mide en la verificación. Un atacante no puede forzarla más de lo que ya puede forzar cualquier petición autenticada, y esas ya tienen su propio límite de peticiones.
- **Estado perdido de la sesión:** si la base de datos no responde, la petición falla (como ya pasa con la comprobación de pertenencia). No se acepta un token «por si acaso» cuando no se puede comprobar.
- **Lo que esta spec cambia de ADR-007:** el access token deja de ser **puramente sin estado**: depende de una fila de la base de datos. Es el precio de poder revocarlo. Se documenta en una ADR nueva (D8).
- **Petición en vuelo:** una petición que ya pasó la comprobación y se está procesando cuando se cierra la sesión se completa. La siguiente es la que falla. Es inevitable y de milisegundos.

## Decisiones técnicas relevantes (pendientes de aprobar)

- **D1. Mecanismo:** un **`sid` por sesión** en el access token, comprobado contra los refresh tokens activos. *Recomendado.* Alternativas: **(a)** un instante «sesiones válidas desde» por cuenta, comparado con la fecha de emisión del token: más simple (una columna), pero solo cubre «cerrar todas»; **cerrar una sola sesión** (spec 013) no se podría invalidar sin cerrar las demás, y no sirve de base para listar y cerrar sesiones concretas (016). **(b)** acortar la vida del access token (p. ej. 5 minutos): no elimina la ventana, solo la reduce, y multiplica las renovaciones. **(c)** lista negra de tokens: ver «Qué NO incluye».
- **D2. Dónde se comprueba:** en **JwtBearer (`OnTokenValidated`) → `401`**. *Recomendado.* Alternativa: en el `AuthorizationBehavior` (junto a la pertenencia), que daría `403`: el interceptor del frontend no renueva ni redirige ante un `403`, y un token de una sesión cerrada dejaría a la persona ante errores sueltos en cada pantalla.
- **D3. Tokens sin `sid` (anteriores al despliegue):** **se rechazan** (`401`), y el frontend los renueva solo. *Recomendado.* Es seguro (fail-closed) y casi invisible: con la renovación automática, el usuario no nota nada salvo si su refresh token ya había caducado. Alternativa: aceptarlos hasta que caduquen (30 min): deja una ventana en la que no vale la protección nueva, justo lo que esta spec quiere cerrar.
- **D4. Qué es «sesión activa» y dónde vive:** **una columna `SessionId` en `RefreshTokens`** (no una tabla nueva de sesiones), y activa = existe un refresh token no revocado y no caducado con ese `SessionId` y ese `UserId`. *Recomendado.* Reutiliza lo que ya existe y la misma definición que los cierres. Alternativa: una tabla `Sessions` con su propio ciclo de vida: más limpia para la spec 016 (dispositivo, fecha de inicio, última actividad), pero obliga a mantener dos tablas coherentes ahora; esa tabla puede nacer en la 016 si hace falta, sin deshacer esto.
- **D5. Migración de los datos existentes:** `SessionId = Id` en cada fila existente, y la columna `NOT NULL`. *Recomendado.* Cada fila vieja pasa a ser su propia sesión: las revocadas ya no cuentan y la activa de cada dispositivo sigue pudiendo renovar (y la renovación copia ese `SessionId`). Alternativa: columna anulable: obliga a tratar «sin sesión» como caso especial para siempre.
- **D6. Coste por petición:** una consulta más, **por un índice que la cubre** (`SessionId` con `UserId`, `RevokedAt` y `ExpiresAt` incluidas), **sin caché**. *Recomendado.* La pertenencia ya se consulta en cada petición (ADR-013); esta es la segunda. Se mide en la verificación en vivo. Alternativa: una caché de unos segundos: reintroduce una ventana (que es lo que se quiere cerrar) y complica los tests; se reconsidera solo si la medición lo justifica. Fundir las dos consultas en una no es posible mientras la sesión dé `401` y la pertenencia `403` (D2).
- **D7. Evento:** **`1009 SessionTokenRejected`**, nivel `Information`, con el motivo (`MissingSessionId` o `SessionNotActive`) y el `UserId` cuando se conoce. *Recomendado.* Que un token de una sesión cerrada llegue a la API es **normal** (una petición en vuelo, una pestaña que no se enteró): no merece `Warning`. Alternativa: sin evento: se pierde la visibilidad de «alguien está usando una sesión que se cerró», útil si se investiga un robo.
- **D8. Documentación:** una **ADR nueva (ADR-018)** que registra el cambio de «access token sin estado» a «access token ligado a una sesión», con sus alternativas (D1) y su coste (D6); y se anotan en la «Relación con otros ADR» de ADR-007 y ADR-013. *Recomendado:* cambia una decisión de arquitectura, no es un detalle. Y las specs 013 y 014 dejan de decir «límite conocido de 30 min».
- **D9. Frontend:** **sin cambios de comportamiento obligatorios** (el interceptor ya trata `401` como «renovar o volver al login»). Se verifica en vivo que los flujos que cierran sesión (activar o desactivar el segundo factor, cerrar todas) siguen funcionando, y se añade **un aviso** al volver al login cuando la sesión se cerró en otro sitio: «Tu sesión se cerró. Vuelve a entrar.» *Recomendado.* Alternativa: no tocar el frontend en esta spec; el usuario volvería al login sin explicación (como ahora cuando caduca el refresh token).

## Fuera de alcance / Backlog relacionado

- Listar mis sesiones y cerrar una concreta (spec 016): usa el `SessionId` de esta spec.
- Purga periódica de refresh tokens caducados o revocados (spec 016).
- Sesiones de operador de la plataforma (otro modelo de sesión).

---

## Validación antes de aprobar

> ¿Esta funcionalidad ayuda a conseguir o mantener al primer cliente? (sección 84 del prompt maestro)

**Sí, aunque no se ve.** El valor de «Cerrar todas mis sesiones» (spec 014) depende de que **funcione de verdad**: sin esto, el botón promete cortar el acceso y no lo hace durante media hora. Una empresa con datos de sus clientes en el móvil de un técnico no puede aceptar ese margen. Es la diferencia entre una función de seguridad y una que lo parece. Es una spec mediana (una columna, una migración, un cambio en la ruta caliente) pero se apoya en lo que ya existe y deja lista la base de la spec 016.
