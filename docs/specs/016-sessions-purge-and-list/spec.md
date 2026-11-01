# Spec — 016-sessions-purge-and-list

**Estado:** En implementación. Decisiones D1–D12 aprobadas el 2026-10-30 tal como estaban escritas
**Fecha:** 2026-10-30
**Bounded Context:** Identity (sesiones)
**Depende de:** spec 015 (el Id de sesión, `SessionId`, y que el access token solo vale mientras su sesión esté activa), specs 013 y 014 (cerrar una sesión y todas), ADR-007 (refresh token con rotación), ADR-009 (eventos de seguridad, límite de peticiones, datos personales), ADR-018 (el access token ligado a su sesión)

---

## Objetivo

Tres cosas que comparten una misma tabla (`RefreshTokens`) y una misma pregunta: *¿qué sesiones hay y cuáles se quedan?*

1. **Que la tabla no crezca sin límite (purga).** Cada renovación de sesión revoca una fila y crea otra, y nunca se borra ninguna. Con la spec 015 esa tabla pasó a **consultarse en cada petición**: lo que antes era acumulación inofensiva es ahora una tabla caliente que crece sin parar. Una persona que trabaja ocho horas al día renueva unas 16 veces: unas 16 filas nuevas al día, para siempre.
2. **Que la persona vea desde dónde está conectada.** Hoy puede cerrar *todas* sus sesiones, pero no ver cuáles son: no puede reconocer «esa no soy yo» ni cerrar solo la sospechosa.
3. **Que pueda cerrar una sesión concreta**, no solo todas (spec 014) ni solo la actual (spec 013).

## Alcance — Qué SÍ incluye

Tres bloques que se pueden entregar por separado (el primero es independiente de los otros dos):

1. **Purga:** un servicio en segundo plano que borra los refresh tokens cuyo `ExpiresAt` caducó hace más de un margen (7 días por defecto), en lotes, con un evento de seguridad cuando borra algo.
2. **Datos de la sesión** (migración): cuándo empezó, desde qué navegador y desde qué red aproximada. Se guardan al iniciar sesión y se **heredan** al renovar.
3. **`GET /api/v1/account/sessions`:** mis sesiones activas, con su dispositivo, cuándo empezaron, su última actividad y cuál es **esta**.
4. **`DELETE /api/v1/account/sessions/{id}`:** cierra una sesión concreta mía (también la actual). `404` si no existe, no es mía o ya está cerrada.
5. **Frontend (segundo paso):** la tarjeta «Sesiones» de la pantalla de Seguridad lista las sesiones, marca «Esta sesión» y ofrece «Cerrar» en cada una.

## Alcance — Qué NO incluye

- **NEXT:** que un **gestor** vea o cierre las sesiones de **otro miembro** de la empresa. Es otra operación, con la jerarquía de roles de la spec 009 y su propia decisión de privacidad.
- **NEXT:** una **notificación** («se inició sesión en un dispositivo nuevo»). Exige correo (no existe) y decidir qué es «nuevo».
- **NEXT:** la **ubicación geográfica** (ciudad) de la IP. Exige una base de datos de geolocalización y es un dato más sensible; esta spec guarda solo una IP truncada.
- **NEVER:** guardar la IP completa o el valor completo de las cabeceras del cliente «por si acaso». Se guarda lo mínimo para reconocer una sesión.
- **NEVER:** que por esta ruta una persona vea o cierre las sesiones de **otra**. El `UserId` sale siempre del token, nunca de la ruta ni del cuerpo.

## Reglas de negocio / invariantes

**Purga**
- **R1.** Se borra una fila si su `ExpiresAt` fue hace **más de `RetentionDays`** días (7 por defecto), **esté o no revocada**. Toda fila tiene `ExpiresAt` como mucho 7 días después de crearse, así que una sola regla cubre las revocadas y las abandonadas.
- **R2.** **Nunca** se toca una fila no caducada, ni una caducada hace menos del margen. Por tanto **nunca se borra una sesión activa**: una sesión activa tiene una fila sin revocar y sin caducar, y esa no cumple R1. Las filas viejas de una sesión activa (sus renovaciones anteriores) sí se borran, y no cambian nada: `IsSessionActiveAsync` solo mira filas activas.
- **R3.** Se borra **en lotes** (por defecto 1000 filas) hasta que no queda nada que borrar, para no bloquear la tabla que se consulta en cada petición.
- **R4.** El servicio **no puede tumbar la API**: si un ciclo falla, se registra el error y se reintenta en el ciclo siguiente. No arranca justo al iniciar (espera unos minutos, para no competir con el arranque ni con las migraciones) y se detiene limpiamente con la API.
- **R5.** Cada ejecución que borra algo deja **un** evento (`1011`) con el recuento y la fecha de corte. Una ejecución que no borra nada no deja evento.
- **R6.** Es **seguro con varias instancias** de la API a la vez: el borrado es idempotente (dos instancias que borran lo mismo no se estorban).

**Datos de la sesión**
- **R7.** Cada refresh token guarda **cuándo se creó** (`CreatedAt`). Cada sesión guarda **cuándo empezó** (`SessionStartedAt`), el **navegador** (`UserAgent`, truncado a 256 caracteres) y una **IP truncada** (`IpAddress`: IPv4 sin el último octeto, IPv6 a los 48 bits).
- **R8.** Al **renovar**, la fila nueva **hereda** `SessionStartedAt`, `UserAgent` e `IpAddress` de la anterior (se muestra *cómo empezó* la sesión, que es lo que su dueño reconoce) y toma su propio `CreatedAt` (la **última actividad** es el `CreatedAt` de la fila activa: se actualiza con cada renovación, es decir, como mucho cada 30 minutos de uso).
- **R9.** La IP completa **nunca** se guarda ni se devuelve; solo la truncada. Los datos de la sesión se borran con su fila (R1): viven, como mucho, 7 días + el margen.

**Listar**
- **R10.** Lista las sesiones **activas** (fila sin revocar y sin caducar) **de la cuenta del token**, de la más reciente a la más antigua por última actividad, **en todas sus empresas** si tuviera varias.
- **R11.** Cada sesión devuelve: `id`, `device` (navegador y sistema, p. ej. «Chrome en Windows», o «Dispositivo desconocido»), `startedAt`, `lastActiveAt`, `approximateIp` (o nulo) y `current` (es la sesión del token de esta petición). **Nunca** el refresh token, su hash, el `UserAgent` crudo ni la IP completa.
- **R12.** Exige sesión de **empresa** y el permiso **`Account.Read`** (los cinco roles). `Cache-Control: no-store`.

**Cerrar una concreta**
- **R13.** `DELETE /account/sessions/{id}` revoca (no borra, como en las specs 013 y 014) las filas **activas de esa sesión y de esa cuenta**. La cuenta sale del token; el `{id}` solo elige **cuál** de las suyas.
- **R14.** **`404`** si la sesión no existe, es de otra cuenta, o ya estaba cerrada: **no se distingue**, para que la ruta no sirva de oráculo («¿existe esta sesión de otra persona?»).
- **R15.** Se puede cerrar **la propia sesión actual**: `204`, y el token de esta petición deja de valer desde la siguiente (spec 015).
- **R16.** Exige el permiso **`Account.CloseSessions`** (el de la spec 014: es «cerrar sesiones», una o todas). Comparte el **cubo de límite** `account-sessions` (10 por hora por IP): el abuso posible es el mismo.
- **R17.** Cada cierre deja **un** evento (`1010`) con el `UserId` y si era la sesión actual; **nunca** el Id de la sesión cerrada ni tokens.

## Modelo conceptual

```
Application/Common/Abstractions
- IClientInfo                        IpAddress (truncada) + UserAgent de la petición actual      ← NUEVO
- ICurrentUser   + SessionId         el «sid» del token                                          ← NUEVO
- ITokenService  + ListSessionsAsync(userId, currentSessionId, ct)  → lista de sesiones activas
                 + CloseSessionAsync(userId, sessionId, ct)         → bool (¿había una activa?)
                 + PurgeExpiredAsync(cutoff, batchSize, ct)         → int (cuántas filas borró)
                 ~ IssueTokensAsync / RotateRefreshTokenAsync       estampan/heredan los datos de la sesión

Application/Identity
- Queries/ListMySessions       [RequiresPermission(Account.Read)]            → IReadOnlyList<SessionDto>
- Commands/CloseSession        [RequiresPermission(Account.CloseSessions)]   (Id de la sesión) → Unit
- SessionDto                   id, device, startedAt, lastActiveAt, approximateIp, current
- DeviceLabel (parser sencillo del User-Agent → «Chrome en Windows»), probado con una tabla de casos

Application/Common/Logging
- SecurityEventIds.SessionClosedByOwner  = 1010
- SecurityEventIds.RefreshTokensPurged   = 1011

Infrastructure
- RefreshToken + CreatedAt, SessionStartedAt, UserAgent, IpAddress
- ClientInfo                    (lee HttpContext; trunca la IP)
- TokenService                  (lista, cierra una, purga: sigue siendo el ÚNICO fichero que lee RefreshTokens)
- RefreshTokenPurgeService      BackgroundService con PeriodicTimer      ← el primer servicio en segundo plano de OfiFlow
- RefreshTokenPurgeOptions      Enabled, IntervalHours (6), RetentionDays (7), BatchSize (1000), StartupDelayMinutes (2)
- Migración AddRefreshTokenSessionInfo: las 4 columnas + índice en ExpiresAt

Api
- GET    /api/v1/account/sessions         (autenticada, Account.Read, no-store)
- DELETE /api/v1/account/sessions/{id:guid}   (autenticada, Account.CloseSessions, cubo "account-sessions")
```

**Operaciones y permisos:** 38 → **40** (29 con permiso, 8 anónimas y 3 de plataforma). **Sin permisos nuevos**: 13 y 65 celdas de la matriz, igual que hoy (D7).

**Cosas descubiertas al implementar el bloque 1 (la purga)** (no cambian ninguna decisión; los desvíos de implementación se señalan):
- **El borrado va en LINQ, no en SQL a mano** (desvío de D3, que decía `DELETE TOP` parametrizado). Un test de arquitectura limita el SQL fuera de LINQ a `AccountLockout.cs`. `ExecuteDeleteAsync` con `OrderBy` y `Take` sí se traduce: EF genera **un solo `DELETE`** con `TOP(@p)` parametrizado, ordenado por `ExpiresAt` (el índice nuevo). Un test captura el comando y lo fija. El objetivo de D3 (por lotes, set-based, con índice) se cumple igual y sin ampliar esa superficie.
- **La purga vive en una interfaz interna** (`IRefreshTokenPurger`), no en `ITokenService`: ningún caso de uso la necesita. La implementa el mismo `TokenService`, que sigue siendo el único fichero que lee la tabla.
- **`Interval` y `StartupDelay` son `TimeSpan`** (no horas y minutos enteros): se configuran como `"06:00:00"` y los tests pueden usar milisegundos.
- **Medido contra la API real** (100 000 filas envejecidas por SQL, lotes de 1000 y pausa de 200 ms): la purga tarda **32 s**; el plan es un `Index Seek` en `IX_RefreshTokens_ExpiresAt` con `Top` y `Clustered Index Delete`; un lote cuesta unas **19 000 lecturas lógicas** (unas 19 por fila, el mantenimiento de los 5 índices con claves GUID); el borrado de un lote toma 5 bloqueos de fila por fila (uno por índice) y **no escala a bloqueo de tabla hasta 2000 filas por lote** (medido en `sys.dm_tran_locks`). Con lotes de 500 tarda 61 s y con 100, 131 s.
- **El efecto sobre las peticiones autenticadas, y su causa.** La mediana no cambia (≈ 12 ms), pero entre el 1 % y el 5 % de las peticiones se quedan 500-700 ms mientras se purga un volumen grande, **sea cual sea el tamaño del lote**; sin purga (control) ninguna pasa de 200 ms. No eran bloqueos (la base ya tenía `READ_COMMITTED_SNAPSHOT`): las peticiones lentas aparecían cada ~5 s con el intervalo acortándose (7,8 → 2,7 s) y su duración bajando con la tabla, que es la firma de la **actualización automática de estadísticas síncrona** de SQL Server (se dispara cada `√(1000 × filas)` modificaciones y la siguiente consulta espera). Se confirmó: con **`AUTO_UPDATE_STATISTICS_ASYNC ON`** solo 2 peticiones de 2771 pasaron de 150 ms (máximo 268 ms). Es un ajuste de la base de datos, no del código: va a la ADR-019 como recomendación de despliegue. Con la purga rutinaria (miles de filas, no cien mil) el efecto es mucho menor, y el mismo mecanismo se dispara ya con el uso normal (cada renovación modifica filas).

**Cosas descubiertas al implementar el bloque 2 (los datos de la sesión)** (no cambian ninguna decisión):
- **Las dos fechas nuevas tenían la misma trampa de la spec 015.** EF las generaba con `0001-01-01` por defecto, así que toda sesión anterior habría «empezado en el año 1». Se creó la columna anulable, se rellenó (`CreatedAt = ExpiresAt − 7 días`, la duración por defecto del refresh token) y solo entonces pasó a `NOT NULL` sin valor por defecto. Un test fija el esquema resultante y otro comprueba que una fila sin datos nunca llega con una fecha del año 1.
- **Un fallo mío, cazado por un test:** al iniciar sesión calculaba la hora dos veces (la del inicio de la sesión y, microsegundos después, la de la creación de la fila), así que «la fila y la sesión nacen a la vez» no era cierto. Ahora es un único instante, pasado explícitamente a `CreateRefreshToken`.
- **Pasar la suite completa antes de commitear sirvió:** 8 tests de la purga (bloque 1) fallaban porque montan su propio contenedor de servicios y `TokenService` ahora exige `IClientInfo`.
- **`IClientInfo` es obligatorio en `TokenService`**, mientras que los campos nuevos de `RefreshToken` son parámetros **opcionales** del constructor (para no tocar los tests que crean filas a mano). La herencia al renovar no es opcional: es explícita.
- **Un test planeado se quitó por ser un falso consuelo:** «un navegador con un salto de línea inyectado». Una cabecera HTTP no puede llevar CR/LF (Kestrel la rechaza), así que ese test habría pasado siempre sin probar nada. Los caracteres de control se prueban en el saneador, de forma unitaria.
- **Lo que la verificación en vivo no puede probar:** desde la propia máquina todas las peticiones vienen de loopback (`::1`), que se guarda tal cual por no ser un dato personal, así que **no se vio la truncación de una IP real**. Esa parte la cubren los tests unitarios (500 direcciones aleatorias de cada familia) y los de la API (que fijan la IP de origen con la cabecera de pruebas).
- **Matiz de privacidad para la ADR-019:** la IP truncada es lo que se guarda **por sesión**, pero el contexto de registro de ADR-009 (`SourceIp`) ya lleva la **IP de origen completa** de cada petición en los registros de la API. Son dos almacenes distintos, con retenciones distintas (el de sesiones: 7 días + el margen de la purga; el de los registros: lo que se decida para los ficheros de log). Truncar en la base de datos no hace que la IP completa desaparezca de los registros, y la ADR debe decirlo.
- **La IP viene de `RemoteIpAddress`**, igual que el límite de peticiones. Detrás de un proxy inverso todas las sesiones mostrarían la IP del proxy hasta configurar `UseForwardedHeaders` con proxies de confianza (ya anotado en ADR-009).

## Criterios de aceptación

**Purga**
- Dadas filas **caducadas hace más del margen**, revocadas o no, entonces se borran; dadas filas **caducadas hace menos del margen**, **activas** y **revocadas aún sin caducar**, entonces **no se tocan**.
- Dada una sesión activa con muchas renovaciones viejas, entonces tras la purga la sesión **sigue activa** y su access token sigue valiendo.
- Dadas más filas que el tamaño del lote, entonces se borran **todas**, en varios lotes.
- Dado un fallo de la base de datos durante un ciclo, entonces la API **sigue viva**, se registra el error y el ciclo siguiente lo reintenta.
- Dada la purga desactivada (`Enabled = false`), entonces el servicio no borra nada.
- Dado un ciclo que borra filas, entonces **un** evento `1011` con el recuento; dado uno que no borra, **ninguno**.

**Datos de la sesión y lista**
- Dados dos inicios de sesión de la misma cuenta con navegadores distintos, entonces `GET /account/sessions` devuelve **dos** sesiones con su dispositivo, y **solo una** con `current = true` (la del token).
- Dada una renovación, entonces la lista sigue mostrando **una** sesión (no dos), con el **mismo** `startedAt` y el **mismo** dispositivo, y una `lastActiveAt` posterior.
- Dada la IP `203.0.113.77`, entonces la lista muestra `203.0.113.0`; dada una IPv6, solo su prefijo de 48 bits. **Nunca** la completa, ni en la respuesta ni en el registro.
- Dado un `User-Agent` **ausente**, **enorme** o **raro**, entonces la sesión se crea igualmente («Dispositivo desconocido»; truncado a 256).
- Dadas **dos cuentas**, entonces cada una ve **solo las suyas**.
- Dadas sesiones **cerradas** o **caducadas**, entonces **no aparecen**.
- Dado que se llame **sin token**, con basura o con un token de **plataforma**, entonces `401`. Dada una empresa **suspendida**, `403 tenant.suspended`.
- Dados los **cinco roles**, todos pueden listar las suyas. La respuesta lleva `Cache-Control: no-store` y **no contiene** `refreshToken`, `tokenHash` ni el `User-Agent` crudo.
- Dadas sesiones anteriores a la migración, entonces se listan como «Dispositivo desconocido» y sin IP.

**Cerrar una concreta**
- Dadas dos sesiones de la cuenta, cuando se cierra **una** por su Id, entonces `204`, el access token y el refresh token de **esa** dejan de valer y los de la **otra siguen**.
- Dado el Id de **otra cuenta**, un Id **inexistente** o el de una sesión **ya cerrada**, entonces **`404` idéntico en los tres casos** (mismo código, mismo cuerpo), y no se cierra nada.
- Dado el Id de la **sesión actual**, entonces `204` y el token de esta petición da `401` en la siguiente.
- Dado un Id que **no es un Guid** en la ruta, entonces `404` (la ruta no casa), sin llegar al caso de uso.
- Dado el cierre, entonces **un** evento `1010` con el `UserId` y si era la actual, **sin** el Id de la sesión ni tokens.
- Dadas **más de 10 peticiones por hora** entre «cerrar una» y «cerrar todas» desde la misma IP, entonces `429` con `Retry-After` (cubo compartido).
- Dado un cierre, el `userId` de la **ruta, la consulta o el cuerpo** que no sea el del token **se ignora**: no hay otro `userId` que pueda venir de fuera.

**Guardarraíles**
- Dado que se lea `RefreshTokens` fuera de `TokenService.cs`, entonces falla el test de arquitectura (ya existe).
- Dada una operación autenticada nueva sin su `[RequiresPermission]`, entonces falla el test de arquitectura (ya existe; entran las dos nuevas).
- Dado que la purga borre una fila no caducada, o una caducada dentro del margen, entonces falla un test.
- Dado que la lista incluya el `UserAgent` crudo, la IP completa o un hash, entonces falla un test.

## Seguridad (ADR-009)

- **Autenticación/autorización:** exigen sesión de empresa. Listar: `Account.Read`; cerrar una: `Account.CloseSessions`. Las dos entran en la tabla «operación → permiso» (40). Cuelgan del grupo `/account`, autenticado (no hay `AllowAnonymous` que las anule, lección de la spec 013).
- **Aislamiento entre cuentas:** `RefreshTokens` es de lectura global (sin filtro de empresa). Las dos consultas filtran por el `UserId` **del token ya validado**; en «cerrar una», el `{id}` solo elige entre las sesiones **de esa cuenta** (`WHERE SessionId = @id AND UserId = @userId`). Sin ese `UserId`, cualquier persona podría cerrar la sesión de otra adivinando su Id. Mismo `404` para «no existe», «no es tuya» y «ya cerrada» (R14).
- **Entrada:** el `{id}` es un Guid (la ruta lo restringe). El `User-Agent` lo controla el cliente: se **trunca**, se guarda como dato (nunca se interpreta ni se escribe en el registro) y el frontend lo muestra escapado (Angular lo hace por defecto). El parser de «Chrome en Windows» solo reconoce familias conocidas; cualquier otra cosa es «Dispositivo desconocido», sin reflejar el texto.
- **Datos personales (RGPD):** la IP es un dato personal. Se guarda **truncada** (no identifica a una persona, sí una red aproximada), **solo mientras la fila exista** (7 días + margen) y **solo la ve su dueño**. No se guarda la IP completa en ningún sitio nuevo. Se documenta en la ADR de esta spec (D11).
- **La IP viene de `RemoteIpAddress`**, la misma fuente que el límite de peticiones. Detrás de un proxy inverso valdrá lo mismo que valga allí: hará falta `UseForwardedHeaders` con proxies de confianza (ya anotado en ADR-009), una sola vez para las dos cosas. Mientras tanto, todas las sesiones mostrarían la IP del proxy: es **informativo, no una decisión de seguridad**.
- **Lo que esta spec NO protege:** la lista es lo que la API **sabe**, y el `User-Agent` y la IP los manda el cliente: un atacante con una sesión robada puede mentir sobre su navegador. Sirve para que su dueño **reconozca las suyas**, no como prueba de nada. «Última actividad» es la de la última **renovación** (como mucho cada 30 minutos de uso), no la de la última petición.
- **Abuso:** listar es una lectura barata, por el índice de `UserId`, tras la autenticación (sin límite propio, como el resto de lecturas autenticadas). Cerrar una comparte el cubo de la spec 014 (R16).
- **Purga: lo que hay que no romper.** Una sesión **activa** nunca desaparece (R2). Y una fila revocada que se borra deja de servir para la detección de reutilización (ADR-007): usar después ese token pasa de «reutilización de un token revocado» (`1003`, revoca la cadena) a «token desconocido» (`1004`, no revoca nada). Eso solo ocurre cuando el token ya caducó hace más del margen, es decir, cuando no podría usarse de todos modos; y **evita** un abuso: hoy, presentar un token viejo ya caducado y revocado revoca la cadena activa de su dueño.
- **Disponibilidad:** el servicio en segundo plano no puede tumbar la API (R4) ni bloquear la tabla (R3). Se desactiva con una opción de configuración.

## Decisiones técnicas relevantes (pendientes de aprobar)

- **D1. Cómo se purga:** un **`BackgroundService` dentro de la API** (un `PeriodicTimer` cada 6 horas, que empieza tras un retraso). *Recomendado.* No depende de infraestructura que hoy no existe (no hay planificador ni servidor decidido), arranca con la API y es idempotente (R6). Alternativas: **(a)** una orden de `OfiFlow.Admin` lanzada por un planificador externo (cron, tarea programada): separa la purga de la API, pero obliga a configurar y vigilar ese planificador, y esa consola está pensada para operaciones interactivas (pide contraseña y código); **(b)** purgar «de paso» al renovar una sesión: no cubre las sesiones **abandonadas** (nunca se vuelven a renovar), que son justo las que se acumulan; **(c)** un job de SQL Server Agent: fuera del código y de las pruebas.
- **D2. Qué se purga y cuándo:** filas con `ExpiresAt` **anterior a ahora − `RetentionDays`**, **7 días** por defecto, revocadas o no (R1). *Recomendado.* Es «el tiempo que el token pudo usarse (su caducidad) más una semana de margen para investigar un incidente». Cuesta, por persona que trabaja 8 h/día, unas **16 filas al día × 14 días ≈ 220 filas**. Alternativa: 30 días de margen (≈ 600 filas por persona): conserva más pasado, pero los eventos de seguridad ya viven en el registro y esta tabla es la que se consulta en cada petición. Es configurable (`RetentionDays`).
- **D3. Cómo se borra:** **por lotes de 1000** con un `DELETE TOP (@n) ... WHERE ExpiresAt < @cutoff` parametrizado, en bucle hasta que no quede nada, con una pausa corta entre lotes. *Recomendado.* Un solo `DELETE` gigante bloquearía una tabla que se consulta en cada petición. Hace falta un **índice en `ExpiresAt`** (la migración), si no cada ciclo lee toda la tabla. Alternativa: `ExecuteDeleteAsync` de EF, que no admite `TOP`.
- **D4. Evento de la purga:** **`1011 RefreshTokensPurged`**, nivel `Information`, solo si borra algo, con el recuento y el corte. *Recomendado.* Un error del ciclo no es un evento de seguridad: va al registro normal como excepción. Alternativa: un evento en cada ciclo, también con cero: ruido cada 6 horas.
- **D5. Qué se guarda de la sesión:** `CreatedAt`, `SessionStartedAt`, **`UserAgent`** (truncado a 256) y **`IpAddress` truncada**, con herencia al renovar (R7–R9). *Recomendado.* La IP **truncada** (IPv4 sin el último octeto, IPv6 a 48 bits) es lo mínimo que permite reconocer «esa red no es la mía» sin identificar a la persona, y es un dato personal con vida limitada (la de su fila). Alternativas: **sin IP**, solo dispositivo y hora: lo más privado, pero «Chrome en Windows» no distingue la sesión del portátil de la de un ladrón con el mismo navegador; **IP completa**: más detalle, pero un dato personal completo sin necesidad.
- **D6. Quién ve qué:** cada persona, **solo las suyas** (R10, R13). *Recomendado.* Que un gestor vea o cierre las de su equipo es otra operación con otras reglas (jerarquía de roles, privacidad del miembro): NEXT.
- **D7. Permisos:** **sin permisos nuevos**: listar usa `Account.Read` (es leer mi propia cuenta) y cerrar una usa `Account.CloseSessions` (el mismo que cerrar todas). *Recomendado.* Un permiso dice *qué se hace*, y «cerrar sesiones» es una sola cosa, en singular o en plural; la matriz no cambia (13 permisos, 65 celdas). Alternativa: `Account.ListSessions` aparte: una celda más y un nombre más que el frontend debe conocer, sin ninguna diferencia de quién lo tiene.
- **D8. Qué es «la misma sesión»:** el **`SessionId`** de la spec 015, que es también el Id público de la sesión en la API. *Recomendado.* No es un secreto: ya viaja en el claim `sid` del token, que cualquiera puede leer pero no firmar, y conocer el de otra persona no da nada (cerrar exige el `UserId` del token, R13). Alternativa: un Id opaco distinto del `sid`: otra columna y otro mapeo, sin ganar seguridad.
- **D9. Qué pasa al cerrar la sesión actual desde la lista:** `204` y el access token de esta petición deja de valer (R15). *Recomendado.* Es coherente con la spec 015 (cerrar una sesión corta su token). El frontend, al cerrar «Esta sesión», hace el cierre local y lleva al login, como el botón «Cerrar todas». Alternativa: prohibirlo (`409`) y obligar a usar «Cerrar sesión»: una ruta más estricta para un caso inofensivo.
- **D10. De dónde sale el dispositivo:** un **parser pequeño y propio** del `User-Agent` en Application (navegador y sistema, familias conocidas, probado con una tabla de casos), que devuelve «Dispositivo desconocido» ante cualquier otra cosa. *Recomendado.* Sin dependencias nuevas y sin reflejar el texto del cliente. Alternativa: una librería de parseo de User-Agent: más precisa, pero una dependencia más (y su mantenimiento) para un dato informativo.
- **D11. Documentación:** una **ADR nueva (ADR-019)** que registra **el primer servicio en segundo plano de OfiFlow** (por qué dentro de la API y no fuera, sus garantías R3–R6, qué hace falta si hay varias instancias) y **la decisión de privacidad** (qué se guarda del cliente, truncado y con vida limitada); se anota en la «Relación con otros ADR» de ADR-007, ADR-009 y ADR-018. *Recomendado:* son dos decisiones de arquitectura. Alternativa: solo anotar ADR-007.
- **D12. Orden de trabajo:** **purga primero** (bloque 1, no necesita ni el frontend ni los datos de la sesión y es lo urgente), luego los datos de la sesión (bloque 2), luego listar y cerrar una (bloque 3), y el frontend al final (bloque 5). *Recomendado.* La purga se puede subir sola. Alternativa: dividir en dos specs (016 purga, 017 sesiones): más ligero de revisar, pero tres bloques que comparten tabla y migración se entienden mejor juntos.

## Fuera de alcance / Backlog relacionado

- Ver y cerrar las sesiones de otro miembro de la empresa (jerarquía de roles).
- Notificación de «sesión nueva» por correo.
- Ubicación aproximada de la IP.
- Un Id de sesión opaco distinto del `sid`.
- `UseForwardedHeaders` con proxies de confianza cuando haya un proxy inverso (ADR-009), válido a la vez para el límite de peticiones y para esta IP.

---

## Validación antes de aprobar

> ¿Esta funcionalidad ayuda a conseguir o mantener al primer cliente? (sección 84 del prompt maestro)

**Sí, por dos motivos distintos.** **La purga** no se ve, pero es salud de la base de datos: la tabla que se consulta en cada petición no puede crecer sin límite, y es mucho más barato arreglarlo antes del primer cliente que con millones de filas. **Listar y cerrar una sesión** es lo que hace creíble la promesa de seguridad de la spec 014: «Cerrar todas» es un martillo, y quien perdió el móvil o ve algo raro quiere ver *cuál* sesión es la sospechosa antes de tirar todas las suyas. Es una funcionalidad que una empresa que guarda datos de sus clientes espera encontrar en cualquier producto serio.
