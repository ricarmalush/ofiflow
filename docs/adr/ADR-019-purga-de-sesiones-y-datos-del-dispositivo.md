# ADR-019 — Purga de sesiones caducadas en segundo plano y datos del dispositivo en cada sesión

**Estado:** Aceptado (2026-11-01). Las decisiones D1–D12 de la [spec 016](../specs/016-sessions-purge-and-list/spec.md) se aprobaron tal como estaban escritas.
**Fecha:** 2026-11-01
**Proyecto:** OfiFlow

---

## Contexto

Esta ADR recoge **dos decisiones de arquitectura** que nacen de una misma tabla, `RefreshTokens`:

1. **El primer servicio en segundo plano de OfiFlow.** Hasta ahora todo lo que hacía la API ocurría dentro de una petición. Nada corría solo.
2. **Qué se guarda del cliente de cada sesión** (navegador y red) para que su dueño pueda reconocerla. Es, por primera vez, un dato personal de la persona guardado *por sesión*.

### La tabla que crece

Cada renovación de sesión revoca una fila y crea otra; **nunca se borra ninguna** (se revoca, no se borra: así se detecta la reutilización de un token, ADR-007). Una persona que trabaja ocho horas al día renueva unas 16 veces: unas 16 filas nuevas al día, para siempre. Hasta la spec 015 era una acumulación inofensiva. Pero desde la [ADR-018](ADR-018-sesion-ligada-al-access-token.md) esa tabla **se consulta en cada petición autenticada** (¿sigue activa esta sesión?), así que pasó a ser una tabla caliente que crece sin límite. La ADR-018 lo dejó escrito como pendiente: «la purga pasa de deseable a necesaria a medio plazo».

### Lo que la persona no puede ver

Con las specs 013 y 014 se puede cerrar la sesión actual o **todas**, pero no ver cuáles hay. Quien perdió el móvil, o ve algo raro, no puede decidir «cierro solo esa»: o las cierra todas o ninguna. Para listarlas hay que guardar algo que permita reconocerlas.

## Problema

- ¿Quién borra las filas viejas, cuándo, y cómo sin bloquear una tabla que se lee en cada petición?
- ¿Qué se guarda del navegador y de la red de cada sesión, durante cuánto tiempo y con qué garantías para la privacidad de la persona?

## Opciones consideradas

### A. Cómo se purga

**A1. Un `BackgroundService` dentro de la API** (un `PeriodicTimer`). *(elegida)* No depende de infraestructura que hoy no existe (no hay planificador ni servidor decidido), arranca con la API y es idempotente.

**A2. Una orden de `OfiFlow.Admin` lanzada por un planificador externo** (cron, tarea programada). Separa la purga de la API, pero obliga a configurar y vigilar ese planificador, y la consola de administración está pensada para operaciones interactivas (pide contraseña y código), no para tareas desatendidas.

**A3. Purgar «de paso» al renovar una sesión.** No cubre las sesiones **abandonadas** (las que nunca se vuelven a renovar), que son justo las que se acumulan.

**A4. Un job de SQL Server Agent.** Queda fuera del código, de las pruebas y de las migraciones, y ata el producto a una edición concreta de SQL Server.

### B. Qué se guarda del cliente

**B1. Nada**, solo el instante. Lo más privado, pero una lista de «sesión del martes, sesión del jueves» no permite reconocer ninguna.

**B2. Navegador y sistema, y la IP truncada.** *(elegida)* Lo mínimo que permite decir «ese Chrome en Windows desde una red que no es la mía».

**B3. Navegador y la IP completa.** Más detalle, pero un dato personal completo sin necesidad: para reconocer una sesión no hace falta saber el último octeto.

**B4. Ubicación geográfica de la IP.** Exige una base de datos de geolocalización y es un dato más sensible. Queda como NEXT.

## Decisión

### 1. El servicio de purga

1. **`RefreshTokenPurgeService`**, un `BackgroundService` de la API (opción A1) con un `PeriodicTimer`. Es el **primer servicio en segundo plano** del proyecto.
2. **Qué borra:** las filas cuyo `ExpiresAt` fue hace **más de `RetentionDays`** (7 por defecto), **estén o no revocadas**. Toda fila caduca como mucho 7 días después de crearse, así que una sola regla cubre las revocadas y las abandonadas. Siete días es «el tiempo que el token pudo usarse más una semana para investigar un incidente».
3. **Qué no toca jamás:** una fila no caducada, ni una caducada dentro del margen. Por tanto **nunca se borra una sesión activa**: una sesión activa tiene una fila sin revocar y sin caducar. Las filas viejas de una sesión activa (sus renovaciones anteriores) sí se borran y no cambia nada, porque la comprobación de la ADR-018 solo mira filas activas.
4. **Cómo borra:** en **lotes** (1000 por defecto) con una pausa de 200 ms entre ellos, hasta que un lote sale incompleto. Se escribe en LINQ con `ExecuteDeleteAsync` + `OrderBy` + `Take`, que EF traduce a **un solo `DELETE TOP (@p)`** parametrizado, ordenado por `ExpiresAt`. No es SQL a mano (el test de arquitectura lo limita a `AccountLockout.cs`) y un test captura el comando y lo fija.
5. **Índice `IX_RefreshTokens_ExpiresAt`** (migración `AddRefreshTokenExpiresAtIndex`): sin él, cada ciclo leería toda la tabla.
6. **Garantías del servicio:**
   - **No tumba la API.** Un ciclo que falla se registra con su excepción y se reintenta en el ciclo siguiente.
   - **No arranca de golpe.** Espera `StartupDelay` (2 minutos) para no competir con el arranque ni con las migraciones.
   - **Se detiene limpiamente** con la API.
   - **Seguro con varias instancias a la vez:** borrar lo que otra instancia ya borró no hace nada. No hace falta bloqueo distribuido ni elegir un «líder».
   - **Se puede apagar** (`RefreshTokenPurge:Enabled = false`); los tests de la API lo hacen.
   - **La configuración se valida al arrancar.** Un intervalo de cero haría girar el bucle sin descanso y un margen de cero borraría tokens aún utilizables; ambos impiden arrancar.
7. **Un ciclo, un ámbito.** Cada lote abre su propio ámbito de servicios, para que el contexto de base de datos no acumule rastreo ni conexiones.
8. **El borrador es interno** (`IRefreshTokenPurger`, implementado por `TokenService`): ningún caso de uso lo necesita. `TokenService.cs` sigue siendo el **único fichero** que lee `RefreshTokens` (lista blanca del test de arquitectura, ADR-009).
9. **Un evento de seguridad `1011`** (`Information`) por ciclo que borra algo, con el recuento y la fecha de corte. Un ciclo que no borra nada no deja evento (sería ruido cada 6 horas). Un fallo del ciclo no es un evento de seguridad: va al registro normal como excepción.
10. **No se registra en `OfiFlow.Admin`.** La consola es un proceso interactivo de vida corta; un servicio en segundo plano allí no tendría sentido. Un test lo fija.

### 2. Los datos de la sesión

1. **Se guardan cuatro datos nuevos en `RefreshToken`:** `CreatedAt` (cuándo nació esa fila), `SessionStartedAt` (cuándo empezó la sesión), `UserAgent` (el navegador, sin caracteres de control y truncado a 256) e `IpAddress` (**truncada**).
2. **La IP se trunca antes de guardarla**, en un único sitio (`ClientInfo`): IPv4 sin el último octeto (`203.0.113.77` → `203.0.113.0`), IPv6 a los primeros 48 bits, IPv4 dentro de IPv6 tratada como IPv4, y el identificador de zona descartado. La **dirección de loopback se conserva** porque es la propia máquina, no una persona. La IP completa **no se guarda en ninguna columna nueva**.
3. **Renovar hereda** `SessionStartedAt`, `UserAgent` e `IpAddress` de la fila anterior y toma su propio `CreatedAt`. La lista muestra *cómo empezó* la sesión (lo que su dueño reconoce) y la **última actividad** es el `CreatedAt` de la fila activa (la última renovación, como mucho cada 30 minutos de uso).
4. **El navegador crudo nunca sale de la API.** Un parser pequeño y propio (`DeviceLabel`) lo convierte en una etiqueta de un **conjunto cerrado** («Chrome en Windows», «Safari en iPhone»…, «Dispositivo desconocido»). Lo que llega a la pantalla no puede ser texto que escribió quien hizo la petición, y no hay dependencia nueva.
5. **El Id público de la sesión es el `SessionId` de la ADR-018** (el claim `sid`). No es un secreto: cualquiera puede leer su propio token, nadie puede firmar uno, y conocer el Id de otra persona no permite hacer nada, porque cerrar exige además el `UserId` del token.
6. **Las filas anteriores a la migración** no tienen datos del dispositivo: se listan como «Dispositivo desconocido» y sin red. La migración sigue el patrón de la ADR-018: columna anulable, relleno y solo entonces `NOT NULL` **sin valor por defecto** (con el que EF genera, toda sesión habría «empezado en el año 1»).

### 3. Ver y cerrar sesiones

- **`GET /api/v1/account/sessions`** (`Account.Read`, los cinco roles): las sesiones activas **de la cuenta del token**, de la más reciente a la más antigua, de todas sus empresas. Seis campos: `id`, `device`, `startedAt`, `lastActiveAt`, `approximateIp`, `current`. Nunca el refresh token, su hash, el navegador crudo ni la IP completa. `Cache-Control: no-store`.
- **`DELETE /api/v1/account/sessions/{id}`** (`Account.CloseSessions`): revoca (no borra) las filas activas **de esa sesión Y de esa cuenta**. La cuenta sale del token; el Id solo elige cuál de **las suyas**. **Una sesión inexistente, ajena o ya cerrada da el mismo `404`** con el mismo código y cuerpo, para que la ruta no sirva de oráculo. Se puede cerrar la sesión actual (su token deja de valer en la petición siguiente).
- **Sin permisos nuevos** (13 permisos, 65 celdas): «cerrar sesiones» es una sola cosa, en singular o en plural. Las operaciones pasan de 38 a **40**.
- **El cubo de límite `account-sessions` (10 por hora y por IP) se comparte** entre «cerrar una» y «cerrar todas»: el abuso posible es el mismo. Listar no tiene límite propio, como el resto de lecturas autenticadas.
- **Un evento `1010`** por cierre, con el `UserId` y si era la sesión actual.

## Consecuencias

### Positivas

- **La tabla caliente deja de crecer sin límite.** El tamaño de régimen es de unas 220 filas por persona que trabaja 8 horas al día (16 al día durante 14 días: los 7 de vida del token más los 7 de margen).
- **La persona puede ver y cerrar una sesión concreta**, que es lo que hace creíble la promesa de seguridad de la spec 014: «cerrar todas» deja de ser el único martillo.
- **Todo lo que se cierra corta el token al instante** (ADR-018), también lo cerrado desde la lista.
- **Una purga que no interfiere con la seguridad:** el margen de 7 días es posterior a la caducidad del token, es decir, cuando ya no podría usarse de todos modos.

### Negativas / riesgos aceptados

- **El servicio vive en la API.** Si se despliegan varias instancias, todas intentan purgar. Es seguro (borrar lo ya borrado no hace nada) pero redundante; si algún día importara, se mueve a una única instancia con la opción `Enabled`, o a un planificador (A2).
- **La purga no es instantánea.** Medido con la API real y **100 000 filas envejecidas por SQL**, lotes de 1000 y pausa de 200 ms: **32 segundos**. Con lotes de 500, 61 s; con 100, 131 s. El plan es un `Index Seek` sobre `IX_RefreshTokens_ExpiresAt` con `Top` y `Clustered Index Delete`. Cada lote cuesta unas **19 000 lecturas lógicas** (unas 19 por fila: hay que mantener los 5 índices de la tabla, varios con claves GUID) y toma 5 bloqueos de fila por fila (uno por índice); **no escala a bloqueo de tabla hasta 2000 filas por lote** (medido en `sys.dm_tran_locks`), con lo que 1000 deja margen. Con la purga rutinaria (miles de filas, no cien mil) el coste es mucho menor.
- **La cola de latencia durante una purga grande tiene una causa que no es la purga.** La mediana de las peticiones autenticadas no cambia (≈ 12 ms), pero entre el 1 % y el 5 % se quedan 500–700 ms mientras se borra un volumen grande, **sea cual sea el tamaño del lote**. No eran bloqueos (la base de la medición ya trabajaba con `READ_COMMITTED_SNAPSHOT`): las peticiones lentas aparecían cada unos 5 s, con el intervalo acortándose, y su duración bajaba con la tabla, que es la firma de la **actualización automática de estadísticas síncrona** de SQL Server (se dispara tras `√(1000 × filas)` modificaciones y la siguiente consulta espera a que termine). Mi primera hipótesis era contención de bloqueos y **era errónea**; se descartó midiendo. Con `AUTO_UPDATE_STATISTICS_ASYNC ON` solo 2 peticiones de 2771 pasaron de 150 ms (máximo 268 ms).
- **Borrar una fila revocada deja de servir para la detección de reutilización** (ADR-007): usar después ese token pasa de «reutilización de un token revocado» (`1003`, que revoca la cadena entera) a «token desconocido» (`1004`, que no revoca nada). Solo ocurre cuando el token ya caducó hace más del margen, es decir, cuando no podría usarse de todos modos. Y **evita un abuso**: sin la purga, presentar un token viejo, ya caducado y revocado, revoca la cadena activa de su dueño.
- **Usar el refresh token de una sesión cerrada desde la lista cuenta como reutilización** y revoca el resto de las sesiones de la persona. Es el coste ya conocido de la spec 014 (D3): se revoca, no se borra, para conservar la detección. Se aceptó y tiene su propio test.
- **Privacidad: dos almacenes distintos, no uno.** Truncar la IP en la base de datos **no hace desaparecer la IP completa de la API**:
  - **En la base de datos** (esta ADR): solo la IP truncada, por sesión, mientras la fila exista (7 días de vida + 7 de margen), y solo la ve su dueño.
  - **En los registros** (ADR-009 y ADR-015): el ámbito de registro `SourceIp` lleva la **IP de origen completa** de cada petición. Su retención es la que se decida para los ficheros de log, que es otra decisión y otra fecha de borrado.

  Quien responda al tribunal o a un auditor debe decir las dos cosas. Tampoco el Id de la sesión cerrada está ausente del registro: el ámbito `RequestPath` de ASP.NET Core lo incluye en `DELETE /account/sessions/{id}`. El **mensaje** del evento `1010` no lo lleva. No es un secreto (ya viaja en el claim `sid`, y no permite hacer nada sin el token de su dueño); si algún día importara, la salida sería no registrar `RequestPath` en esa ruta, no cambiar la ruta.
- **Los datos son lo que el cliente dice, no una prueba.** El navegador y la IP los manda quien hace la petición. Un atacante con una sesión robada puede mentir sobre su navegador. La lista sirve para que su dueño **reconozca las suyas**.
- **Detrás de un proxy inverso** todas las sesiones mostrarían la IP del proxy, porque la IP viene de `RemoteIpAddress` (la misma fuente que el límite de peticiones). Hasta configurar `UseForwardedHeaders` con proxies de confianza (ya anotado en ADR-009) es **informativo, no una decisión de seguridad**.
- **«Última actividad» es la última renovación** (como mucho cada 30 minutos de uso), no la última petición.
- **Un servicio más que probar, vigilar y apagar.** Por eso tiene opciones, validación al arrancar y sus propios tests de arranque y parada.

### Recomendación de despliegue (no es código)

Activar en la base de datos de producción:

```sql
ALTER DATABASE [OfiFlow] SET AUTO_UPDATE_STATISTICS_ASYNC ON;
```

Es un ajuste de la base y no del código: hace que la actualización de estadísticas no bloquee la consulta que la dispara. Se midió contra la purga de 100 000 filas, pero el mismo mecanismo se dispara ya con el uso normal (cada renovación modifica filas de esta tabla). En la base donde se midió ya estaba activa `READ_COMMITTED_SNAPSHOT`; conviene comprobar las dos opciones en producción. Cuando exista el despliegue (ADR-015 deja pendiente su destino), esta línea debe entrar en su lista de comprobaciones.

### Qué queda cerrado (no se debe reabrir sin una razón nueva y explícita)

- **Nunca se purga una fila no caducada ni una caducada dentro del margen.**
- **No se guarda la IP completa** en ninguna columna nueva.
- **El navegador crudo no sale de la API:** solo una etiqueta de un conjunto cerrado.
- **La cuenta sale siempre del token**, nunca de la ruta; el Id de la ruta solo elige entre las sesiones **de esa cuenta**; y los tres casos de «no encontrada» son indistinguibles.
- **El servicio de purga no tumba la API** y no corre en la consola de administración.
- **`TokenService.cs` sigue siendo el único fichero que lee `RefreshTokens`.**

### Qué queda abierto para revisar más adelante

- **Que un gestor vea o cierre las sesiones de otro miembro** (otra operación, con la jerarquía de la spec 009 y su propia decisión de privacidad).
- **Una notificación** de «se inició sesión en un dispositivo nuevo» (exige correo, que no existe).
- **La ubicación aproximada** de la IP.
- **Retención de los registros** con la IP completa (`SourceIp`): decisión pendiente de despliegue.
- **`UseForwardedHeaders`** con proxies de confianza, cuando haya un proxy inverso.
- **Mover la purga** a una sola instancia o a un planificador, si se despliegan varias instancias y la redundancia molesta.
- **Un Id de sesión opaco** distinto del `sid`.

---

## Relación con otros ADR

- Depende de: ADR-018 (el `SessionId` y que el access token solo vale mientras su sesión tenga un refresh token activo; la purga no puede romper eso, y la lista usa el mismo Id).
- Amplía: ADR-007 (el ciclo de vida del refresh token gana el **borrado** pasado el margen, y la fila guarda los datos del dispositivo).
- Amplía: ADR-009 (eventos `1010` y `1011`; la IP es un dato personal tratado con minimización; la lista blanca de lecturas de `RefreshTokens` no cambia; el matiz de `SourceIp` en los registros).
- Amplía: ADR-018 (cierra el pendiente de la purga y la lista de sesiones).
- Relacionada con: ADR-015 (los registros a fichero, donde sigue viviendo la IP completa), ADR-012 (los permisos `Account.Read` y `Account.CloseSessions`, sin ninguno nuevo).
- Resuelve: el pendiente de ADR-018 («purga de refresh tokens y listar y cerrar sesiones concretas») y el NEXT de la spec 014.
