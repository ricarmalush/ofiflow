# Capítulo 8 — Seguridad

## Qué vas a aprender

- Cómo pensar la seguridad de una API: qué se protege y de qué.
- La idea de **defensa en profundidad** y las capas que la forman en OfiFlow.
- Qué hace cada defensa (cabeceras, HTTPS, límite de peticiones, permisos, aislamiento, validación, registro).
- Cómo el propio código **se impide romperse**: analizadores, tests de arquitectura y el pipeline.
- Un **mapa completo de amenazas**: cada riesgo, su control, dónde está y qué test lo vigila.
- Qué *no* cubre todavía.

Los capítulos 4 al 7 han explicado piezas sueltas. Este las junta y te da la vista de conjunto, la que necesitas para explicar la seguridad del proyecto de principio a fin.

## Cómo pensar la seguridad

Empieza por la pregunta correcta: **¿qué hay que proteger?** En OfiFlow, lo valioso son los **datos de los clientes de cada empresa** (nombres, teléfonos, direcciones, trabajos), y que **nadie pueda hacer lo que no le toca**. Todo lo demás es en servicio de eso.

### Una lista de amenazas: STRIDE

**STRIDE** es una lista de seis familias de amenazas, muy usada para no olvidarse de ninguna. La verás en CyberOps. Así se reparten en OfiFlow:

| Letra | Amenaza | Ejemplo en OfiFlow | Qué la frena |
|---|---|---|---|
| **S** | *Spoofing* (suplantar) | Hacerse pasar por otro usuario | Contraseña con hash, token firmado (cap. 6) |
| **T** | *Tampering* (alterar) | Modificar el token o los datos enviados | Firma del token, validación, permisos |
| **R** | *Repudiation* (negar lo hecho) | "Yo no borré ese cliente" | Eventos de seguridad (parcial: aún no hay registro de auditoría) |
| **I** | *Information disclosure* (fuga) | Ver datos de otra empresa; errores que cuentan demasiado | Filtro por empresa (cap. 5), errores genéricos (cap. 7), registro sin datos personales |
| **D** | *Denial of service* (negar el servicio) | Inundar el login; enviar contraseñas enormes | Límite de peticiones, longitudes máximas |
| **E** | *Elevation of privilege* (subir de nivel) | Un técnico que borra clientes | Permisos por rol, protección contra *mass assignment* |

### Prevenir, detectar, responder

Una defensa puede hacer tres cosas distintas, y conviene no confundirlas:

| | Qué es | En OfiFlow |
|---|---|---|
| **Prevenir** | Impedir que ocurra | La gran mayoría: validación, permisos, filtro por empresa, límites |
| **Detectar** | Enterarse de que ocurre | Los eventos de seguridad del registro |
| **Responder** | Actuar | Una sola respuesta automática: si se reutiliza un refresh token revocado, se revocan todos (cap. 6) |

La API **se defiende sola** frenando casi todo, pero no tiene a nadie mirando el registro ni bloquea atacantes de forma activa. Es una puerta blindada con cámara, sin vigilante.

## Defensa en profundidad

Ninguna defensa es perfecta. Por eso se ponen **varias, una detrás de otra**: si una falla, la siguiente aún protege. Una petición atraviesa estas capas:

```
 Petición
    │
    ▼
 ① La puerta          HTTPS · cabeceras de seguridad · límite de peticiones
    │
    ▼
 ② Quién eres         el token firmado: ¿es auténtico y no ha caducado?        (cap. 6)
    │
    ▼
 ③ Qué puedes         ¿tu rol tiene el permiso de esta operación?             (cap. 4 y esta)
    │
    ▼
 ④ Qué datos          ¿son válidos? (validadores y reglas de Domain)           (cap. 7)
    │
    ▼
 ⑤ De quién son       filtro automático por empresa                           (cap. 5)
    │
    ▼
 ⑥ Cómo se guardan    SQL parametrizado, sin SQL a mano
    │
    ▼
 ⑦ Qué se cuenta      errores genéricos hacia fuera, detalle solo en el registro

   ── y antes de que el código llegue a producción ──
 ⑧ Guardarraíles      analizadores, tests de arquitectura, escáneres en el pipeline
```

Si un descuido dejara pasar un handler sin filtrar por empresa (capa ⑤), la capa ③ ya habría comprobado quién puede ejecutarlo, y la ② quién eres. Por eso un solo fallo casi nunca es una fuga.

---

## Parte 1 — La puerta

### Las cabeceras de seguridad

Cada respuesta lleva cinco **cabeceras HTTP** que son instrucciones para el navegador sobre cómo tratar lo que recibe. Las añade [`SecurityHeadersMiddleware`](../../src/OfiFlow.Api/Common/SecurityHeadersMiddleware.cs):

| Cabecera | Qué le dice al navegador | Ataque que frena |
|---|---|---|
| `X-Content-Type-Options: nosniff` | "Cree solo el tipo que declaro, no lo adivines" | Un fichero que parece inofensivo pero es un script y el navegador lo ejecuta |
| `X-Frame-Options: DENY` | "Nadie puede meterme dentro de un `iframe`" | *Clickjacking*: una web maliciosa te superpone la página invisible y haces clic sin saberlo |
| `Referrer-Policy: no-referrer` | "No cuentes de qué URL vienes" | Que las URL, que pueden llevar identificadores, se filtren a otros sitios |
| `Content-Security-Policy: default-src 'none'; frame-ancestors 'none'` | "No cargues ni ejecutes nada de esta respuesta" | Si alguien lograra inyectar un script (XSS), no se ejecutaría |
| `Cross-Origin-Resource-Policy: same-origin` | "Solo páginas de mi mismo origen pueden usarme" | Que otras webs lean o incrusten la respuesta |

Una **aclaración honesta**: OfiFlow hoy es una API que solo devuelve JSON, sin pantallas (salvo la de Swagger, que solo existe en desarrollo y se explica abajo), así que estas cabeceras son **defensa en profundidad**: no frenan un ataque concreto *ahora*, pero valen si un navegador abre una respuesta de la API, y valdrán cuando exista un frontend. Con una API se ponen lo más restrictivas posible, porque no hay nada legítimo que permitir. La última (`Cross-Origin-Resource-Policy`) la añadió el **primer escaneo automático real** del pipeline, que la echó en falta: no fue una suposición.

Tres detalles de diseño:

- Se registran con **`OnStarting`** (línea 28): "justo antes de enviar la respuesta, añade esto". Es necesario porque, cuando ocurre un error, el manejador de excepciones **borra** las cabeceras para escribir la respuesta de error; con `OnStarting` se añaden después de eso, y los errores también salen protegidos. Lo comprueba un test sobre una respuesta 401.
- El middleware es **el primero** del pipeline ([`Program.cs:33`](../../src/OfiFlow.Api/Program.cs)), para cubrir también lo que falle más adentro.
- No hay `CORS` configurado: ningún frontend consume la API todavía.

**Una excepción, y por qué.** La pantalla de Swagger (capítulo 1) es HTML con scripts y estilos, y la CSP estricta (`default-src 'none'`) la dejaba **en blanco**: el navegador se negaba a cargar sus propios ficheros. Para poder usarla, el middleware aplica una CSP más laxa (`default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; frame-ancestors 'none'`) **solo si se cumplen las dos cosas a la vez**: el entorno es `Development` y la ruta empieza por `/swagger`. Cualquier otra ruta, y todo `Production`, conserva la estricta; `X-Frame-Options: DENY` y `frame-ancestors 'none'` siguen impidiendo que nadie la meta en un `iframe`. Es un **cambio consciente de una regla** que este capítulo daba por absoluta ("lo más restrictivo posible"), y por eso tiene sus propios tests ([`SwaggerUiTests`](../../tests/OfiFlow.Api.Tests/Security/SwaggerUiTests.cs)): que la relajada no se escape a otras rutas (ni a `/swaggerfoo`, que empieza igual), que en `Production` ni exista y que la política laxa nunca incluya `unsafe-eval`, comodines ni orígenes externos. Se comprobó rompiéndolos a propósito: relajarla en todas las rutas, fuera de `Development` o con `unsafe-eval` pone tests en rojo.

### HTTPS y HSTS

La petición debe viajar cifrada. `UseHttpsRedirection` (línea 42) redirige de HTTP a HTTPS. Y **HSTS** (*HTTP Strict Transport Security*) añade una cabecera que le dice al navegador "durante un año (365 días), contacta conmigo **siempre** por HTTPS, aunque te digan lo contrario". Frena los ataques que intentan degradar la conexión a HTTP. Solo se activa **fuera de desarrollo** (líneas 37-40), porque en tu máquina no hay certificado de producción.

### El límite de peticiones

Para frenar la **fuerza bruta** (probar contraseñas sin parar) y la saturación, el [limitador](../../src/OfiFlow.Api/Common/RateLimiting.cs) permite un número de peticiones por **ventana de tiempo y por IP**:

| Ruta | Límite |
|---|---|
| `register` | 3 por hora |
| `login` | 5 por minuto |
| `refresh` | 20 por minuto |

Al pasarse, responde **429** (*demasiadas peticiones*) con la cabecera `Retry-After` (cuántos segundos esperar), en el mismo formato de error del capítulo 7. Cada rechazo genera el evento de seguridad `1101`.

---

## Parte 2 — Quién eres y qué puedes

Esto ya lo conoces, y aquí queda en su sitio dentro del conjunto:

- **Quién eres** (capa ②): el token firmado, con su caducidad y su firma (capítulo 6).
- **Qué puedes** (capa ③): los **permisos por rol**. Cada operación declara el permiso que exige, y una tabla única dice qué puede cada rol ([`RolePermissions`](../../src/OfiFlow.Domain/Tenancy/RolePermissions.cs)). Si el rol no lo tiene, **403**.

| Permiso | Owner | Admin | Manager | Employee | Technician |
|---|:-:|:-:|:-:|:-:|:-:|
| Ver clientes / trabajos | ✅ | ✅ | ✅ | ✅ | ✅ |
| Crear y modificar clientes | ✅ | ✅ | ✅ | ✅ | ❌ |
| Eliminar clientes | ✅ | ✅ | ❌ | ❌ | ❌ |
| Crear y modificar trabajos | ✅ | ✅ | ✅ | ✅ | ❌ |
| Asignar / cancelar trabajos | ✅ | ✅ | ✅ | ❌ | ❌ |
| Iniciar y completar trabajos | ✅ | ✅ | ✅ | ❌ | ✅ |

Tres propiedades de diseño que merece la pena saber explicar:

1. **Mínimo privilegio:** cada rol recibe solo lo que necesita. Lo destructivo e irreversible (borrar clientes) se reserva a `Owner` y `Admin`.
2. **Se comprueba en Application, no en los endpoints**, para que valga igual venga la petición de HTTP, de la IA o de otro cliente.
3. **Fail-closed:** una operación sin su permiso declarado se *deniega*, y un test de arquitectura impide que exista una. Olvidarse nunca deja algo abierto.

- **De quién son los datos** (capa ⑤): el filtro automático por empresa (capítulo 5).

---

## Parte 3 — Los datos

### Inyección SQL

El ataque clásico: colar código SQL dentro de un dato (`'; DROP TABLE...`) para que la base de datos lo ejecute. OfiFlow lo evita porque **todo el acceso a datos pasa por EF Core**, que envía los valores como **parámetros**, nunca pegados al texto de la consulta (lo viste en el capítulo 4, paso 11).

Y no depende de que nadie se acuerde: escribir SQL a mano con `FromSqlRaw` o `ExecuteSqlRaw` está **prohibido**, con doble candado:

- **Al compilar:** el aviso `EF1002` (SQL con interpolación) es un **error**, no un aviso ([`Directory.Build.props:17`](../../Directory.Build.props)).
- **En los tests:** `SourceCode_DoesNotUseRawSqlApis` falla si esas funciones aparecen en cualquier fichero de `src`.

### *Mass assignment*

Es cuando el cliente envía campos que no debería poder decidir, como `"tenantId": "<otra empresa>"` o `"role": "Owner"`. Se evita por diseño: **ningún Command ni Query tiene propiedad `TenantId`**, y el JSON sobrante se descarta. Un test, `NoCommandOrQuery_AcceptsTenantIdFromTheClient`, lo impone.

### Límites de tamaño

Cada campo de texto tiene una longitud máxima, definida **una sola vez** y compartida entre el validador y la base de datos (capítulo 7). La contraseña tiene un máximo de 128 caracteres por una razón de seguridad (capítulo 6). Sin esto, enviar textos gigantes tumbaría el servidor o acabaría en un 500.

### Secretos

- **Nunca en el repositorio.** El secreto del token vive en `user-secrets` en local y en variables de entorno en producción.
- Si falta, **la aplicación no arranca**: prefiere fallar antes que funcionar con una clave débil.
- Un escáner (**gitleaks**) revisa **todo el historial** de commits en cada cambio, y GitHub bloquea el *push* si detecta un secreto con formato real. Una excepción, como un secreto antiguo ya rotado, se documenta con su huella exacta en `.gitleaksignore`, con la justificación al lado.

### Qué se registra

La regla es estricta: **ni contraseñas, ni tokens, ni cuerpos de petición, ni datos personales** en el registro (ni emails, teléfonos o direcciones de clientes). Se registran **identificadores** (de usuario, de empresa) y la IP de origen. Hay tests que envían un dato reconocible y comprueban que no aparece en ningún registro.

---

## Parte 4 — Ver lo que pasa: los eventos de seguridad

No se puede reaccionar a lo que no se ve. OfiFlow registra **siete eventos de seguridad** con un identificador numérico estable ([`SecurityEventIds.cs`](../../src/OfiFlow.Application/Common/Logging/SecurityEventIds.cs)), para que alguien pueda filtrar o crear alertas por número sin depender del texto del mensaje, que puede cambiar:

| Id | Evento | Cuándo ocurre |
|---|---|---|
| `1001` | Login fallido | Email desconocido o contraseña incorrecta (con el motivo, sin el email) |
| `1002` | Login sin empresa | Credenciales válidas, pero el usuario no pertenece a ninguna empresa (una anomalía) |
| `1003` | **Refresh token reutilizado** | Posible robo: se revocan todos los tokens del usuario |
| `1004` | Refresh token rechazado | Desconocido, caducado o sin pertenencia a la empresa |
| `1101` | Límite de peticiones superado | Alguien se pasa en `login`, `register` o `refresh` |
| `1201` | Excepción no controlada | Un 500; se registra con el detalle interno completo |
| `1301` | **Acceso denegado** | Un rol intenta algo para lo que no tiene permiso |

Se agrupan por familia (10xx autenticación, 11xx abuso, 12xx errores, 13xx autorización), de modo que un analista puede mirar una familia entera.

Además, un pequeño middleware ([`RequestLogScopeMiddleware`](../../src/OfiFlow.Api/Common/RequestLogScopeMiddleware.cs)) apunta la **IP de origen** en cada petición, de modo que todos los eventos la incluyan sin que cada pieza del código tenga que pasarla de mano en mano. Y el `traceId` de los errores une la respuesta con la entrada exacta del registro.

> **Límite importante:** hoy esto es **registro, no vigilancia**. Los eventos se escriben, pero nadie recibe una alerta ni hay un sistema (un SIEM) que los analice. Y no existe un **registro de auditoría persistente** ("quién creó o borró qué y cuándo"): solo queda `CreatedAt` en las filas. Está planificado para cuando existan facturas y pagos.

---

## Parte 5 — Los guardarraíles: el código que se impide romperse

Una regla que depende de que alguien se acuerde acaba olvidándose. En OfiFlow las reglas de seguridad se convierten en cosas que **rompen el build**. Hay tres niveles.

### Al compilar

- `AnalysisModeSecurity=All` y [`.editorconfig`](../../.editorconfig): **toda** la categoría "Security" de los analizadores de .NET (criptografía débil, SQL peligroso, deserialización insegura...) es un **error de compilación**, no un aviso.
- Paquetes vulnerables: `NU1903` y `NU1904` (vulnerabilidad alta o crítica en una dependencia, incluidas las transitivas) también son errores.

### En los tests de arquitectura

Estos tests no prueban una funcionalidad, sino que **la estructura del código respeta una regla**:

| Test | Regla que impone |
|---|---|
| `SourceCode_DoesNotUseRawSqlApis` | Nada de SQL escrito a mano |
| `IgnoreQueryFilters_IsOnlyUsedInTheAllowList` | Solo dos ficheros pueden saltarse el filtro de empresa, por ruta completa |
| `GlobalEntities_AreOnlyReadInTheAllowList` | Las tablas sin filtro de empresa solo se leen donde está permitido |
| `NoCommandOrQuery_AcceptsTenantIdFromTheClient` | Ninguna operación recibe el `TenantId` del cliente |
| `EveryMappedEntity_IsTenantOwnedOrDeclaredGlobal` | Toda entidad es de una empresa (con filtro) o está declarada global |
| `EveryOperationOfTheApplication_DeclaresItsAuthorization` | Toda operación declara su permiso (o que es anónima) |

### En el pipeline de GitHub

Cada cambio pasa por herramientas, **una por tipo de problema**, sin solaparse:

| Qué busca | Herramienta |
|---|---|
| Fallos de seguridad en el código | Los analizadores de .NET (al compilar) y **CodeQL** (sigue el flujo de datos de la entrada a un punto peligroso) |
| Dependencias con vulnerabilidades | NuGetAudit al compilar, y **Dependabot** que propone las actualizaciones, agrupadas, cada semana |
| Secretos en el repositorio | GitHub *secret scanning* con *push protection*, y **gitleaks** sobre todo el historial |
| Fallos en la aplicación en ejecución | **OWASP ZAP**, que arranca la API de verdad contra un SQL Server y la ataca por su descripción OpenAPI |
| Que una empresa no vea los datos de otra | El test de aislamiento, con SQL Server real |

Y la rama `master` está **protegida**: no se puede cambiar sin un *pull request* con los cuatro comprobantes obligatorios en verde (`build-and-test`, `gitleaks`, `analyze` y `zap-scan`), y la regla vale también para el dueño del repositorio. Así un check en rojo no se puede saltar con un botón. Las acciones de terceros del pipeline van fijadas por el **hash exacto del commit**, no por una etiqueta de versión que podría moverse.

### Cómo se sabe que un guardarraíl funciona

Un guardarraíl que nunca ha fallado no demuestra que funcione. Por eso cada uno se verifica **rompiendo a propósito el código real** y comprobando que el test se pone en rojo (lo viste en el capítulo 5). Es la mejor respuesta a la pregunta "¿cómo sabes que tus controles de seguridad funcionan?".

---

## El mapa de amenazas completo

Esta es la tabla que reúne todo: cada riesgo, el control que lo frena, dónde está y qué test lo vigila. Las filas con "**sin test propio**" son controles que existen y funcionan, pero que ningún test comprueba directamente.

| Amenaza | Control | Dónde | Test |
|---|---|---|---|
| **S** · Fuerza bruta de contraseñas | 5 logins por minuto y por IP | `RateLimiting.cs:28` | `Login_SixthAttemptWithinAMinute_IsRejectedWith429` |
| **S** · Suplantar con un token falso | Firma HMAC-SHA256 | `DependencyInjection.cs:71` | `AnAccessTokenWithAnAlteredTenantId_IsRejected` |
| **S** · Robar un refresh token | Rotación y detección de reutilización | `TokenService.cs:46-63` | `RotateRefreshTokenAsync_WithAlreadyRevokedToken_...` |
| **S** · Usar la API sin sesión | `RequireAuthorization()` en cada grupo | `JobEndpoints.cs:21` | `WithoutAToken_EveryRoute_Returns401` |
| **T** · Alterar el token | La firma deja de coincidir | `DependencyInjection.cs:71` | `AnAccessTokenWithAnAlteredTenantId_IsRejected` |
| **T** · Datos mal formados o enormes | Validadores y longitudes máximas | `*CommandValidator.cs` | `Register_WithPasswordLongerThanMax_Returns400_Not500` |
| **R** · Negar una acción | Eventos de seguridad con usuario y empresa | `SecurityEventIds.cs` | `ADenial_IsLoggedAsASecurityEvent_...`, `ValidateCredentialsAsync_WhenFails_LogsSecurityEvent...` |
| **I** · Ver datos de otra empresa | Filtro global por empresa | `ApplicationDbContext.cs:63` | `TenantIsolationEndToEndTests`, `CrossTenantWriteIsolationTests` |
| **I** · Entidad nueva sin filtro | Clasificación obligatoria | `TenantModelRules.cs` (tests) | `EveryMappedEntity_IsTenantOwnedOrDeclaredGlobal` |
| **I** · Un 500 que cuenta detalles internos | Mensaje genérico y detalle solo al registro | `GlobalExceptionHandler.cs` | `UnhandledException_Returns500WithoutInternalDetails_AndLogsIt` |
| **I** · Datos personales en los logs | El registro solo lleva identificadores | `IdentityService.cs`, `AuthorizationBehavior.cs` | `ValidateCredentialsAsync_WhenFails_LogsSecurityEvent...`, `ADenial_IsLogged...` |
| **I** · Contenido leído o incrustado por otra web | Cabeceras de seguridad | `SecurityHeadersMiddleware.cs:13-22` | `EveryResponse_IncludesSecurityHeaders` |
| **I** · Secretos en el repositorio | gitleaks y *push protection* | `ci.yml`, `.gitleaksignore` | El propio job `gitleaks` en cada PR |
| **D** · Inundar el login o el registro | Límite de peticiones por IP | `RateLimiting.cs:28-30` | `Login_SixthAttemptWithinAMinute_IsRejectedWith429` |
| **D** · Contraseñas o textos gigantes | Máximos de longitud | `PasswordRules.cs`, validadores | `Register_WithPasswordLongerThanMax_Returns400_Not500` |
| **E** · Un rol hace lo que no le toca | Permisos por rol, antes de la validación | `AuthorizationBehavior.cs` | `EachRole_CanOrCannotPerformEachOperation` (65 casos) |
| **E** · Una operación sin permiso declarado | *Fail-closed* y test de arquitectura | `AuthorizationBehavior.cs` | `EveryOperationOfTheApplication_DeclaresItsAuthorization` |
| **E** · El cliente fija su empresa o su rol | Ningún comando acepta `TenantId` | Los `*Command.cs` | `NoCommandOrQuery_AcceptsTenantIdFromTheClient` |
| **E** · Inyección SQL | EF Core parametrizado y SQL a mano prohibido | `Directory.Build.props:17` | `SourceCode_DoesNotUseRawSqlApis` |
| Bajar la conexión a HTTP | HTTPS y HSTS (365 días) | `Program.cs:37-42` | **Sin test propio** |
| Dependencia con vulnerabilidad conocida | NuGetAudit como error y Dependabot | `Directory.Build.props`, `dependabot.yml` | El propio build y el pipeline |
| Vulnerabilidad en el código | Analizadores de seguridad como error y CodeQL | `.editorconfig`, `codeql.yml` | El propio build y el pipeline |
| Vulnerabilidad en la API en ejecución | Escaneo ZAP en cada cambio | `dast.yml` | El propio job `zap-scan` |

---

## Dónde están hoy los límites

Una lista honesta de lo que **no** está cubierto, de más a menos preocupante:

**Autenticación**
1. **No hay bloqueo de cuenta.** El límite es por IP: un ataque repartido entre miles de IPs, con un intento por IP, no lo ve.
2. **El tiempo de respuesta del login delata si un email existe.** Y el registro dice "ese email ya está registrado".
3. **No hay cierre de sesión en el servidor**, y un token de acceso no se puede anular antes de caducar (hasta 30 minutos).

**Autorización**
4. **El rol y la pertenencia a la empresa viajan en el token:** un cambio no se nota hasta renovar la sesión. No se revalidan en cada petición.
5. **Un `Technician` puede ver y ejecutar cualquier trabajo de su empresa**, no solo los asignados a él.
6. **No existen las invitaciones:** todo usuario es `Owner`. Los permisos se prueban con tests, pero no se pueden ejercer a mano.

**Abuso y red**
7. **El límite de peticiones solo cubre `auth`.** Clientes y trabajos no tienen límite: alguien con un token válido puede crear datos sin freno.
8. **Los límites viven en memoria y por IP.** Se reinician al desplegar, no se comparten entre instancias, y detrás de un proxy inverso todos los clientes compartirían la IP del proxy hasta que se configure `UseForwardedHeaders`.
9. **No hay `CORS` configurado**: se hará cuando exista el frontend.

**Observabilidad**
10. **Hay registro, pero no vigilancia**: nadie recibe alertas, ni hay un registro de auditoría persistente.

**El propio pipeline**
11. **El escaneo ZAP no está autenticado.** Ataca la API por su descripción OpenAPI sin enviar un token, así que prueba lo anónimo (registro, login, renovar) y comprueba que lo demás responde 401, pero **no recorre las operaciones protegidas**. Se compensa con los tests de extremo a extremo, que sí lo hacen.
12. **Hay cuatro reglas de ZAP ignoradas**, todas informativas, cada una con su motivo escrito en `.zap/rules.tsv`.

**Herramientas de desarrollo**
13. **Swagger UI relaja la política CSP** en su ruta (solo en `Development`, con tests que impiden que se extienda). Si algún día se registrara por error en producción, esa ruta expondría una pantalla para probar la API; lo impide la condición de entorno y un test, pero es una excepción a la regla general.

**Controles sin test propio** (existen, pero ningún test los comprueba directamente)
14. La aplicación **no arranca sin el secreto** del token.
15. El token **caduca** a los 30 minutos.
16. El hash guardado **no es la contraseña** en claro.
17. **HSTS** se envía fuera de desarrollo.

Los cuatro últimos son los candidatos más baratos para una próxima ronda de endurecimiento, con el mismo método de siempre: escribir el test y verlo fallar rompiendo el código a propósito.

## Para comprobar que lo has entendido

1. ¿Qué es la defensa en profundidad y por qué un solo fallo casi nunca es una fuga en OfiFlow?
2. Explica con tus palabras qué hace `X-Frame-Options: DENY` y por qué las cabeceras se añaden con `OnStarting`.
3. ¿Qué diferencia hay entre prevenir, detectar y responder? Pon un ejemplo de OfiFlow de cada uno.
4. ¿Cómo evita OfiFlow la inyección SQL, y qué impide que alguien la reintroduzca por descuido?
5. ¿Qué es el *mass assignment* y qué lo impide en OfiFlow?
6. Un analista quiere saber cuántos intentos de acceso denegado hubo ayer. ¿Qué evento busca y por qué un número y no el texto del mensaje?
7. ¿Cómo sabes que tus guardarraíles de seguridad funcionan de verdad, y no pasan siempre?
8. Nombra tres límites de la seguridad actual y di cuál cerrarías primero, y por qué.

*(Respuestas: 1 = poner varias defensas una tras otra para que, si una falla, la siguiente aún proteja; un handler que olvidara filtrar por empresa seguiría estando tras la autenticación, los permisos y la validación. 2 = impide que otra web meta la página en un `iframe` (clickjacking); `OnStarting` las añade en el último instante, después de que el manejador de errores borre las cabeceras, para que los errores también salgan protegidos. 3 = prevenir es impedir (un permiso que deniega un 403); detectar es enterarse (el evento 1301 en el registro); responder es actuar (revocar todos los refresh tokens si se reutiliza uno revocado). 4 = todo el acceso a datos pasa por EF Core, que usa parámetros; el aviso `EF1002` es un error de compilación y un test falla si aparece `FromSqlRaw` o `ExecuteSqlRaw`. 5 = que el cliente fije campos que no debería (`tenantId`, `role`); ningún comando tiene propiedad `TenantId`, el JSON sobrante se descarta y un test lo impone. 6 = el `1301` (acceso denegado); un número estable permite filtrar y crear alertas aunque se cambie la redacción del mensaje. 7 = porque cada uno se verifica rompiendo a propósito el código real y comprobando que el test se pone en rojo, y los de extremo a extremo llevan controles positivos. 8 = por ejemplo, no hay bloqueo de cuenta, el límite de peticiones solo cubre `auth`, y el rol puede estar desfasado hasta 30 minutos; yo cerraría primero el límite de peticiones en clientes y trabajos, porque es barato y hoy un token válido permite crear datos sin freno.)*

## Siguiente capítulo

El capítulo 9 explica los **tests**: por qué hay cuatro proyectos, cómo se prueba contra un SQL Server real, qué son los controles positivos y las pruebas de mutación, y cómo se mide si una prueba de seguridad está vacía.
