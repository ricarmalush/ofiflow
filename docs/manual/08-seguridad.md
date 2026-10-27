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
- El middleware es **el primero** del pipeline ([`Program.cs:78`](../../src/OfiFlow.Api/Program.cs)), para cubrir también lo que falle más adentro.
- El CORS (qué webs de otro origen pueden leer las respuestas) se explica en la sección siguiente.

**Una excepción, y por qué.** La pantalla de Swagger (capítulo 1) es HTML con scripts y estilos, y la CSP estricta (`default-src 'none'`) la dejaba **en blanco**: el navegador se negaba a cargar sus propios ficheros. Para poder usarla, el middleware aplica una CSP más laxa (`default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; frame-ancestors 'none'`) **solo si se cumplen las dos cosas a la vez**: el entorno es `Development` y la ruta empieza por `/swagger`. Cualquier otra ruta, y todo `Production`, conserva la estricta; `X-Frame-Options: DENY` y `frame-ancestors 'none'` siguen impidiendo que nadie la meta en un `iframe`. Es un **cambio consciente de una regla** que este capítulo daba por absoluta ("lo más restrictivo posible"), y por eso tiene sus propios tests ([`SwaggerUiTests`](../../tests/OfiFlow.Api.Tests/Security/SwaggerUiTests.cs)): que la relajada no se escape a otras rutas (ni a `/swaggerfoo`, que empieza igual), que en `Production` ni exista y que la política laxa nunca incluya `unsafe-eval`, comodines ni orígenes externos. Se comprobó rompiéndolos a propósito: relajarla en todas las rutas, fuera de `Development` o con `unsafe-eval` pone tests en rojo.

### CORS: quién puede leer las respuestas (ADR-017)

**El problema.** Un navegador aplica la regla del *mismo origen*: una página de `https://app.ejemplo.com` no puede leer las respuestas de `https://api.ejemplo.com`, porque son orígenes distintos (cambia el host, el puerto o el esquema). Sin esa regla, cualquier web que visitases podría usar tu navegador, donde estás identificado, para leer datos de otro sitio. **CORS** (*Cross-Origin Resource Sharing*) es la forma en que la API le dice al navegador "a esta web sí la dejo leer".

**Cómo funciona.** Para peticiones que pueden tener efectos (las que llevan `Authorization` o cuerpo JSON), el navegador antes pregunta con una petición `OPTIONS` (la *preflight*): "¿puede `https://app.ejemplo.com` hacer un `POST` con estas cabeceras?". La API contesta con `Access-Control-Allow-Origin`, `Access-Control-Allow-Methods` y `Access-Control-Allow-Headers`. Si no coinciden, el navegador **no envía la petición real**. Lo importante: es el *navegador* quien hace cumplir esto. Quien llama con `curl` o desde un servidor lo ignora. CORS no protege la API: protege **al usuario**.

**Qué se configuró** ([`Program.cs:33-64`](../../src/OfiFlow.Api/Program.cs) y `:91`):

| Decisión | Por qué |
|---|---|
| **Lista cerrada** de orígenes en la configuración (`Cors:AllowedOrigins`); sin lista, nadie | El valor por defecto es el seguro. Nunca `AllowAnyOrigin` (`*`) |
| Solo `GET`, `POST`, `PUT` y `DELETE` | Los verbos que usa la API. Nada de `AllowAnyMethod` |
| Solo `Authorization`, `Content-Type` y `Accept-Language` | Lo que el cliente envía de verdad. Nada de `AllowAnyHeader` |
| **Sin credenciales de navegador** (`AllowCredentials`) | La sesión viaja en la cabecera `Authorization`, que una web ajena no puede añadir; permitir cookies abriría un riesgo que hoy no existe |
| Expone `Retry-After` | Sin esto el navegador se lo esconde al JavaScript y el frontend no sabría cuánto esperar tras un `429` |
| El *preflight* se cachea 10 minutos | Menos peticiones `OPTIONS`, sin dejar una autorización vieja durante horas |

**Un origen mal escrito impide arrancar la API**, igual que una clave que falta. Debe ser exactamente lo que envía el navegador (esquema, host y puerto, sin barra final ni ruta), con `https` salvo en `localhost`. Rechaza `*`, `https://app.ejemplo.com/`, `http://app.ejemplo.com` y `https://usuario@app.ejemplo.com`. Esta última se aceptaba en el primer borrador: la pillaron los tests.

**Dónde va en el pipeline.** `UseCors` está **antes** del rate limiter y de la autenticación (paso 4 a 6 del capítulo 4): la petición `OPTIONS` no lleva token y debe contestarse sin pasar por ellos, y un `429` también debe llevar las cabeceras CORS; si no, el navegador lo convertiría en un error de red sin código ni `Retry-After`. Lo mismo para un `500`. Los dos casos tienen test.

**En desarrollo** se permite `http://localhost:4200` (el puerto por defecto de Angular) en `appsettings.Development.json`. **En producción** la lista no está en el repositorio: se pasa por configuración de entorno (`Cors__AllowedOrigins__0=https://...`).

**Cómo se comprueba.** [`CorsTests`](../../tests/OfiFlow.Api.Tests/Security/CorsTests.cs) hace lo que hace un navegador (la petición previa y la real con `Origin`) y comprueba: el origen permitido puede leer, uno ajeno no, no se piden credenciales, no se autoriza un verbo ni una cabecera que la API no usa, los `429` y `500` llevan las cabeceras, y los orígenes mal escritos impiden el arranque. Se comprobó rompiéndolo a propósito 12 veces (comodín, credenciales, cualquier verbo o cabecera, quitar `UseCors`, ponerlo después del rate limiter, `http` fuera de `localhost`, no validar el origen...) y los tests fallaron en todas.

**Un detalle que desconcierta.** La cabecera `Cross-Origin-Resource-Policy: same-origin` (más arriba) **no choca** con CORS: solo afecta a cargas que no son CORS (una imagen o un script de otra web). Las peticiones `fetch` de un frontend se autorizan con `Access-Control-Allow-Origin`.

### HTTPS y HSTS

La petición debe viajar cifrada. `UseHttpsRedirection` (línea 87) redirige de HTTP a HTTPS. Y **HSTS** (*HTTP Strict Transport Security*) añade una cabecera que le dice al navegador "durante un año (365 días), contacta conmigo **siempre** por HTTPS, aunque te digan lo contrario". Frena los ataques que intentan degradar la conexión a HTTP. Solo se activa **fuera de desarrollo** (líneas 37-40), porque en tu máquina no hay certificado de producción.

### El límite de peticiones

Para frenar la **fuerza bruta** (probar contraseñas sin parar) y la saturación, el [limitador](../../src/OfiFlow.Api/Common/RateLimiting.cs) permite un número de peticiones por **ventana de tiempo y por IP**:

| Ruta | Límite |
|---|---|
| `register` | 3 por hora |
| `login` | 5 por minuto |
| `refresh` | 20 por minuto |
| `accept-invitation` | 10 por hora (es anónima y cara: hashea una contraseña) |
| `mfa/verify` | 10 por minuto (es anónima, toca la base de datos y es la vía obvia para probar códigos de 6 dígitos) |
| Gestión del segundo factor (`setup`, `enable`, `disable`, `recovery-codes` y reiniciar el de un miembro) | 10 por hora (son operaciones raras) |

Al pasarse, responde **429** (*demasiadas peticiones*) con la cabecera `Retry-After` (cuántos segundos esperar), en el mismo formato de error del capítulo 7. Cada rechazo genera el evento de seguridad `1101`.

### El bloqueo de cuenta (spec 010)

Este límite es **por IP**. Su complemento **por cuenta** es el bloqueo de cuenta, explicado paso a paso en el capítulo 6 (Parte 3). Aquí, lo que importa desde el punto de vista de la seguridad:

| Aspecto | Valor | Por qué |
|---|---|---|
| Umbral y duración | **5 fallos → 15 minutos** (en [`AuthenticationPolicy`](../../src/OfiFlow.Application/Identity/AuthenticationPolicy.cs)) | Frena el reparto de intentos entre muchas IP. Valores iniciales, a revisar con datos reales |
| Qué cuenta como fallo | Contraseña errónea **y código del segundo factor erróneo** (mismo contador). No cuentan el email desconocido (no hay cuenta que bloquear), un desafío inventado o caducado, ni los intentos durante el bloqueo | Para que no se pueda alargar el bloqueo de otra persona sin acertar su email |
| Cómo se cuenta | El intento se **reserva** con una sentencia atómica **antes** de comprobar la contraseña | Con "leer, comprobar y sumar", 50 peticiones simultáneas darían 50 intentos y no 5 |
| Qué ve quien ataca | Bloqueada, desconocida y errónea: **misma respuesta, misma verificación completa** | Para no revelar qué cuentas existen ni cuáles están bloqueadas |
| Qué ve el equipo | Eventos `1005` (cuenta bloqueada, una vez por bloqueo) y `1006` (intento con la cuenta bloqueada) | Una ráfaga de `1006` sobre un mismo `UserId` es un ataque en curso |
| Cuándo vuelve a cero | Solo con un **acceso completo**: contraseña correcta en una cuenta sin MFA, o contraseña y segundo factor en una con MFA. Con MFA, la contraseña correcta **devuelve** su intento y no reinicia nada (si no, quien la conoce probaría 4 códigos, reiniciaría y seguiría sin límite) |
| La columna `LockoutEnabled` | **Se ignora**: el bloqueo vale para todos | Las cuentas se crean con ese valor a `false` (el de `IdentityUser`); respetarlo dejaría a todas sin bloqueo: un valor por defecto inseguro |

Tiene un coste que conviene decir en voz alta: **quien conozca un email puede mantener esa cuenta bloqueada** fallando a propósito cada 15 minutos. Es el precio de bloquear por cuenta, y por eso el bloqueo es temporal (uno permanente sería peor: cualquiera podría dejar sin acceso a otro). Se mitiga con el límite por IP y los eventos; el bloqueo progresivo y el desbloqueo manual quedan como siguientes pasos.

También hay dos guardarraíles de arquitectura nuevos: el SQL fuera de LINQ solo puede estar en `AccountLockout.cs` (con un test que comprueba que sigue estando ahí, para no pasar en vacío), y el método que comprueba una contraseña **sin** contar intentos no puede usarse fuera de `IdentityService`.

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
| Ver los miembros de la empresa (`Users.Read`) | ✅ | ✅ | ✅ | ❌ | ❌ |
| Invitar, cambiar el rol y quitar usuarios (`Users.Manage`) | ✅ | ✅ | ❌ | ❌ | ❌ |
| Gestionar el segundo factor **de la propia cuenta** (`Mfa.Manage`; spec 010) | ✅ | ✅ | ✅ | ✅ | ✅ |
| Ver los datos de la **propia** cuenta, su empresa y su rol (`Account.Read`; spec 012) | ✅ | ✅ | ✅ | ✅ | ✅ |

**«Quién soy» (spec 012).** `GET /api/v1/account/me` responde a la persona con su nombre y email, su empresa, el Id de su pertenencia, su rol **efectivo** y los permisos de ese rol (`Customers.Read`, `Jobs.Assign`...). Es una lectura de **uno mismo**, y tiene tres rasgos que conviene saber defender: **(1)** no lleva ningún parámetro: la cuenta sale del token, así que nadie puede preguntar por otra persona (un `userId` o un `tenantId` en la consulta se ignora y hay un test que lo prueba); **(2)** el rol sale de la **base de datos**, no del token, como el resto de decisiones de la API: tras un cambio de rol, el mismo token ya recibe el rol nuevo; **(3)** la persona y la empresa se leen **a través de la pertenencia filtrada por empresa** (una sola sentencia), de modo que nada de otra empresa puede salir por aquí. La respuesta lleva `Cache-Control: no-store` y un test comprueba que tiene exactamente ocho campos y que ningún secreto (hash, secreto del segundo factor, códigos de recuperación, tokens) puede colarse. **Límite honesto:** los permisos que devuelve son **informativos, para pintar la interfaz**; quien decide es siempre la API en cada operación, así que un cliente que los falsifique solo consigue botones que dan 403.

**Las rutas (spec 009).** Cada una exige sesión (salvo aceptar), y quién puede lo decide el permiso más la jerarquía, no el endpoint:

| Ruta | Permiso | Éxito | Notas |
|---|---|---|---|
| `POST /api/v1/invitations` | `Users.Manage` | 201 con el token | La respuesta lleva `Cache-Control: no-store`: contiene un secreto que no se vuelve a mostrar |
| `GET /api/v1/invitations` | `Users.Manage` | 200 | Sin el token ni su hash |
| `DELETE /api/v1/invitations/{id}` | `Users.Manage` | 204 | |
| `GET /api/v1/users` | `Users.Read` | 200 | El Id es el de la pertenencia, nunca el de la cuenta |
| `PUT /api/v1/users/{id}/role` | `Users.Manage` | 204 | Cuerpo: `{ "role": <número> }` |
| `DELETE /api/v1/users/{id}` | `Users.Manage` | 204 | Baja completa: cuenta, ficha, sesiones y trabajos |
| `POST /api/v1/auth/accept-invitation` | anónima | 204 sin cuerpo | **10 por hora y por IP**; no devuelve tokens |

**Las rutas del segundo factor (spec 010).** Las de la propia cuenta no llevan ningún Id: la cuenta sale del token, así que nadie puede tocar el segundo factor de otra persona por aquí.

| Ruta | Permiso | Éxito | Notas |
|---|---|---|---|
| `GET /api/v1/account/mfa` | `Mfa.Manage` | 200 | `{ enabled, setupPending, recoveryCodesRemaining }`: nunca un secreto ni un código |
| `POST /api/v1/account/mfa/setup` | `Mfa.Manage` | 200 con el secreto | Cuerpo: `{ password }`. **`no-store`**: el secreto y la URI `otpauth://` se muestran una sola vez. 10 por hora y por IP |
| `POST /api/v1/account/mfa/enable` | `Mfa.Manage` | 200 con 10 códigos | Cuerpo: `{ code }` (6 dígitos). **`no-store`**; cierra todas las sesiones de la cuenta |
| `POST /api/v1/account/mfa/disable` | `Mfa.Manage` | 204 | Cuerpo: `{ password, code }` (código de la app o de recuperación); cierra las sesiones |
| `POST /api/v1/account/mfa/recovery-codes` | `Mfa.Manage` | 200 con 10 códigos | Cuerpo: `{ password, code }`. **`no-store`**; los anteriores dejan de valer |
| `DELETE /api/v1/users/{id}/mfa` | `Users.Manage` + jerarquía | 204 | Reinicia el MFA de un miembro (el Id es el de la pertenencia); idempotente; nunca sobre el `Owner` ni sobre uno mismo |
| `POST /api/v1/auth/mfa/verify` | anónima | 200 con la sesión | Cuerpo: `{ challenge, code }`. **10 por minuto y por IP**. Todos los fallos dan el **mismo 401** |

El **login** (`POST /api/v1/auth/login`) cambia de forma solo para las cuentas con segundo factor: en vez de los tokens devuelve `200 { mfaRequired: true, challenge, expiresAt }`, con **`no-store`** y **sin ninguna sesión**. Los errores del segundo factor llevan su código estable (`mfa.invalid_password`, `mfa.invalid_code`, `mfa.already_enabled`, `mfa.not_enabled`, `mfa.setup_required`), todos con estado 400.

Las denegaciones por jerarquía o por intentar gestionarse a uno mismo son **403** con su propio código (`tenant_user.role_not_manageable`, `tenant_user.cannot_manage_self`); un miembro o una invitación que no existe en tu empresa, **404**. Quitar o degradar a un miembro ya surte efecto **en su siguiente petición** (ver abajo: el rol sale de la base de datos). Puedes probarlas en Swagger o en la sección 6 de la colección `OfiFlow.Api.http`.


**Quién puede gestionar a quién.** Tener `Users.Manage` no basta: sería una vía de escalada de privilegios (un `Admin` comprometido podría ascender a cómplices). Por eso hay una **segunda tabla** en Domain, [`ManageableRoles`](../../src/OfiFlow.Domain/Tenancy/ManageableRoles.cs), que dice a qué roles puede conceder, cambiar y quitar cada rol:

| El que actúa ↓ / el rol objetivo → | Owner | Admin | Manager | Technician | Employee |
|---|:-:|:-:|:-:|:-:|:-:|
| **Owner** | ❌ | ✅ | ✅ | ✅ | ✅ |
| **Admin** | ❌ | ❌ | ✅ | ✅ | ✅ |
| Manager, Technician, Employee | ❌ | ❌ | ❌ | ❌ | ❌ |

Tres consecuencias: **nadie puede gestionar al `Owner`** (es único por empresa y no se concede), **un `Admin` no toca a otros `Admin`** (solo el `Owner` nombra o quita administradores) y, para **cambiar** un rol, hacen falta las dos cosas: poder gestionar el rol actual **y** el nuevo (así un `Admin` no asciende a un `Manager` a `Admin`). Como la tabla de permisos, va compilada e inmutable y tiene un test por cada celda, escrito aparte de la tabla de producción. Verificado rompiéndola a propósito: dar al `Admin` poder sobre otro `Admin`, hacer gestionable al `Owner` o cambiar el "y" por "o" en el cambio de rol pone tests en rojo.

La misma tabla gobierna **reiniciar el segundo factor de otro miembro** (spec 010): quien perdió el móvil y los códigos de recuperación puede pedir a un gestor que se lo reinicie, y el gestor solo puede hacerlo sobre los roles que puede gestionar. El `Owner` reinicia a cualquiera, el `Admin` a `Manager`, `Technician` y `Employee`, y **nadie reinicia al `Owner`** ni a sí mismo: para uno mismo está *desactivar*, que exige contraseña y un código. Así, un `Admin` comprometido no puede quitarle el segundo factor al dueño. Un `Owner` que lo pierde todo no tiene a nadie por encima en su empresa: ese caso lo resolverá el operador de la plataforma (riesgo aceptado, ADR-014).

**El rol sale de la base de datos, no del token.** Un token JWT se firma una vez y vale 30 minutos. Si el rol viajara dentro y se leyera de ahí, quitar a alguien o degradarlo no surtiría efecto hasta que caducara el token: un empleado despedido seguiría con acceso a los datos de los clientes. Por eso el [`AuthorizationBehavior`](../../src/OfiFlow.Application/Common/Behaviors/AuthorizationBehavior.cs) hace esto en **cada** petición autenticada, antes de comprobar el permiso:

1. **Declaración.** Si la operación está mal declarada (sin permiso, o anónima y con permiso a la vez) se deniega **sin consultar nada**.
2. **Pertenencia.** [`CurrentUser`](../../src/OfiFlow.Infrastructure/Identity/CurrentUser.cs) lee en la base de datos la pertenencia de esa persona a esa empresa (**una sola consulta**, por el índice único `(empresa, usuario)`, usando solo lo que dice el token ya validado, nunca nada que mande el cliente). Si no existe, **403** y un evento `1302`. En esa **misma consulta** comprueba que la empresa no esté **suspendida** (spec 011): si lo está, **403** con el código `tenant.suspended` y un evento `1303`. Es la empresa del token y no hay ninguna lectura de más: la suspensión viaja en la consulta de la pertenencia (un test cuenta las consultas).
3. **Permiso.** Se comprueba con el rol **de la base de datos**. El rol del token se sigue leyendo (`TokenRole`) pero es solo informativo: no autoriza nada.

Consecuencias que conviene saber explicar:

- **Quitar o degradar surte efecto en la siguiente petición**, con el mismo token sin renovar. Y al revés: ascender a alguien le da el poder nuevo sin que renueve la sesión.
- **Un token que dice "Owner" no escala.** Si lo firmara alguien (porque conociera el secreto) pero su dueño es `Technician` en la base de datos, sigue siendo `Technician`. El claim del rol dejó de ser algo que alguien pueda falsificar para ganar poder.
- **Fail-closed también aquí:** hasta que se verifica la pertenencia, `CurrentUser.Role` es nulo, así que quien lo lea antes de tiempo no ve ningún rol. Un token sin usuario o sin empresa se deniega sin tocar la base de datos.
- **El coste es real y está medido:** una consulta indexada más por petición autenticada, que se hace **una sola vez** aunque se pregunte varias veces (hay un test que cuenta las consultas). Si algún día se mide como cuello de botella, se podría cachear unos segundos, aceptando ese retraso.
- Cambia una propiedad que el ADR-012 daba por buena ("la autorización no consulta la base de datos"); sus tests se actualizaron de forma explícita. Entre ellos, los cinco que decían "un token sin un rol válido se deniega": ahora el claim ausente o raro **no cambia nada**, y lo que se comprueba es que no pueda dar ni quitar poderes.

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

**Una excepción, escrita y vigilada (spec 010).** Hay **una** sentencia SQL fuera de LINQ: la que reserva el intento del bloqueo de cuenta ([`AccountLockout.cs`](../../src/OfiFlow.Infrastructure/Identity/AccountLockout.cs)), porque EF no puede expresar «comprobar y sumar a la vez devolviendo el nuevo contador». Es la variante **segura**: `SqlQuery` con interpolación, donde cada valor viaja como parámetro SQL y nunca como texto pegado, solo toca una fila por su Id y no recibe nada del cliente. Un test de arquitectura limita esas APIs (`SqlQuery`, `FromSql`, `ExecuteSql` y sus variantes) a ese único fichero, y otro comprueba que el fichero sigue usándolas, para que la lista blanca no pase en vacío.

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

No se puede reaccionar a lo que no se ve. OfiFlow registra **treinta y dos eventos de seguridad** con un identificador numérico estable ([`SecurityEventIds.cs`](../../src/OfiFlow.Application/Common/Logging/SecurityEventIds.cs)), para que alguien pueda filtrar o crear alertas por número sin depender del texto del mensaje, que puede cambiar:

| Id | Evento | Cuándo ocurre |
|---|---|---|
| `1001` | Login fallido | Email desconocido o contraseña incorrecta (con el motivo, sin el email) |
| `1002` | Login sin empresa | Credenciales válidas, pero el usuario no pertenece a ninguna empresa (una anomalía) |
| `1003` | **Refresh token reutilizado** | Posible robo: se revocan todos los tokens del usuario |
| `1004` | Refresh token rechazado | Desconocido, caducado o sin pertenencia a la empresa |
| `1005` | **Cuenta bloqueada** | La cuenta llegó a 5 intentos fallidos y queda bloqueada 15 minutos (con el recuento y la duración, sin el email). Se registra una vez por bloqueo |
| `1006` | **Intento con la cuenta bloqueada** | Alguien intenta entrar en una cuenta bloqueada. Una ráfaga de estos sobre el mismo `UserId` es un ataque en curso |
| `1101` | Límite de peticiones superado | Alguien se pasa en `login`, `register` o `refresh` |
| `1201` | Excepción no controlada | Un 500; se registra con el detalle interno completo |
| `1301` | **Acceso denegado** | Un rol intenta algo para lo que no tiene permiso, o intenta gestionar a un rol que no le corresponde (la jerarquía de roles) |
| `1302` | **Pertenencia revocada** | Un token todavía válido de alguien que ya no pertenece a la empresa (se le quitó mientras el token seguía vivo). La revalidación en cada petición lo detecta |
| `1303` | **Empresa suspendida** | Alguien de una empresa suspendida intenta entrar, canjear el segundo factor o seguir usando una sesión todavía válida (con la operación, el `UserId` y el `TenantId`). Una ráfaga sobre la misma empresa es normal (su equipo reintentando); lo que merece mirarse es una suspensión que nadie esperaba |
| `1401` | Invitación creada | Alguien de la empresa invita a una persona (con el rol, sin el email ni el token) |
| `1402` | Invitación aceptada | Nace una cuenta nueva a partir de una invitación |
| `1403` | **Invitación rechazada** | Alguien intenta aceptar un token que no sirve (desconocido, caducado, revocado, ya usado o perdido en una carrera). El log guarda el **motivo real**; quien lo intenta recibe siempre la misma respuesta |
| `1404` | Invitación revocada | Se cancela una invitación pendiente |
| `1405` | **Rol cambiado** | Se cambia el rol de un miembro (con el rol anterior y el nuevo, sin su nombre ni su email) |
| `1406` | **Miembro quitado** | Se da de baja a un miembro: se borran su cuenta y sus datos (con el recuento de trabajos que quedaron sin asignar) |
| `1501` | Secreto del segundo factor pedido | Alguien empieza a activar el MFA de su cuenta (sin el secreto) |
| `1502` | **MFA activado** | Se confirmó el secreto con un código; se cierran las sesiones de la cuenta |
| `1503` | **MFA desactivado** | Se desactivó con contraseña y código; se cierran las sesiones |
| `1504` | **Código del segundo factor rechazado** | Con el **motivo** en el log (código erróneo, ya usado, de recuperación erróneo, cuenta bloqueada...); quien lo intenta no lo ve. Una ráfaga sobre un mismo `UserId` es un ataque a su segundo factor |
| `1505` | **Código de recuperación usado** | Con cuántos quedan. Suele significar que se perdió el móvil; conviene mirarlo |
| `1506` | Códigos de recuperación regenerados | Los anteriores dejan de valer |
| `1507` | **MFA reiniciado por un gestor** | Quién lo hizo y a quién (solo Ids). Un `1507` inesperado seguido de un login desde una IP nueva es lo que querría ver un analista |
| `1601` | **Sesión de operador emitida** | Un operador completó contraseña y código y recibió una sesión de plataforma (solo el `UserId`). Cada una debe corresponder a alguien que conozcas; una a deshoras o desde una IP nueva es lo que hay que mirar |
| `1602` | **Inicio de sesión de plataforma rechazado** | Con el **motivo real** en el log (contraseña errónea, cuenta que no es de operador, sin segundo factor...); quien lo intenta recibe siempre el mismo `401`. Una ráfaga de `1602` con el motivo «cuenta que no es de operador» y una contraseña **correcta** es alguien con credenciales de empresa probando la puerta de la plataforma |
| `1603` | Empresas listadas | Un operador listó las empresas (su `UserId`, la página y cuántas vio; nunca un nombre de empresa). Es una **lectura**: no deja fila de auditoría, solo este evento |
| `1604` | **Empresa suspendida** | Un operador suspendió una empresa (solo el `UserId` y el `TenantId`). Deja además una fila de auditoría que no caduca. Un `1604` sin un `1601` reciente del mismo operador es lo primero que mira un analista |
| `1605` | **Empresa reactivada** | Un operador reactivó una empresa (solo los dos Ids), con su fila de auditoría |
| `1606` | **Operador de la plataforma creado** | Alguien con acceso al servidor creó una cuenta con acceso a todas las empresas (solo el `UserId`, nunca el email). Debe coincidir con una intervención que conozcas: un `1606` inesperado es el evento más grave de la lista |
| `1607` | **Operador de la plataforma dado de baja** | Se desactivó a un operador (solo el `UserId`) |
| `1608` | **Sesión de operador revocada** | Un token de plataforma todavía válido de una cuenta que ya no es un operador activo (con la operación y el `UserId`). Es la baja surtiendo efecto en la siguiente petición; un `1608` sin una baja reciente que lo explique es una anomalía |

Se agrupan por familia (10xx autenticación y bloqueo, 11xx abuso, 12xx errores, 13xx autorización, 14xx usuarios e invitaciones, 15xx segundo factor, 16xx panel de la plataforma), de modo que un analista puede mirar una familia entera. Una ráfaga de `1403` desde una misma IP es exactamente lo que querría ver alguien que vigile intentos de adivinar tokens, y un `1405` de alguien que se ascendió a sí mismo sería la señal de una escalada (la regla lo impide, pero el intento queda como `1301`).

Además, un pequeño middleware ([`RequestLogScopeMiddleware`](../../src/OfiFlow.Api/Common/RequestLogScopeMiddleware.cs)) apunta la **IP de origen** en cada petición, de modo que todos los eventos la incluyan sin que cada pieza del código tenga que pasarla de mano en mano. Y el `traceId` de los errores une la respuesta con la entrada exacta del registro.

### El alta del operador: lo más delicado, fuera de la API

Una cuenta de operador ve los metadatos de **todas** las empresas y puede suspenderlas, así que su creación es lo más peligroso que puede hacer alguien con acceso. Por eso **no existe ninguna ruta HTTP** que la cree: se hace con el programa [`OfiFlow.Admin`](../../src/OfiFlow.Admin/Program.cs) (capítulo 1 explica el procedimiento y el 3, por qué es un proyecto aparte). La seguridad está en capas:

| Qué se evita | Cómo |
|---|---|
| Que alguien lo alcance por HTTP | La API no referencia el programa ni registra el servicio; tests de arquitectura fijan que ningún otro fichero nombra [`PlatformOperatorAdministration`](../../src/OfiFlow.Application/Platform/PlatformOperatorAdministration.cs) y que no es una petición de MediatR |
| Un operador sin segundo factor | El primer código TOTP se comprueba **en memoria, antes de tocar la base de datos** (`PlatformOperatorAdministration.cs:89`). Sin código válido no se guarda ni la cuenta |
| Filas a medias si algo falla | La cuenta, el segundo factor ya confirmado, los 10 códigos, el operador y la auditoría se guardan en **un único `SaveChanges`** (`:116`). Si falla, no queda nada; lo prueba una carrera de dos altas con el mismo email contra SQL Server real |
| Que el primer código se reutilice | Se anota como último paso aceptado; el mismo código no vale en el primer inicio de sesión |
| Una contraseña a la vista | Se pide por la terminal sin eco, nunca por argumento, y el programa se niega a funcionar con la entrada redirigida |
| Una clave de más en el servidor de administración | El programa no recibe `Jwt:Secret` (`AddAdminInfrastructure`); un test lo comprueba |
| Un alta sin rastro | Auditoría persistente (`platform.operator_created`, `platform.operator_disabled`) y los eventos `1606` y `1607` |

**Un matiz honesto.** La auditoría de estas dos acciones guarda como `OperatorUserId` el **operador afectado**, no quien ejecutó el programa: el actor es «alguien con acceso al servidor», que no tiene cuenta en OfiFlow. Quién fue exactamente lo dirá el registro del servidor (sesión, historial), no OfiFlow. Y el programa en sí no tiene autenticación: **quien pueda ejecutarlo con la cadena de conexión y la clave del segundo factor puede crear un operador**. Esa frontera es el acceso al servidor, y por eso el programa no debe estar en el equipo que atiende la web pública.

### La sesión del operador: lo que impide confundir los dos mundos

La sesión de plataforma (capítulo 6) es el otro lado de la misma moneda: una vez dentro, un operador ve todas las empresas, así que lo importante es que **nada de una empresa valga allí ni al revés**. Las defensas, de fuera adentro:

| Qué se evita | Cómo |
|---|---|
| Que un token de empresa sirva en una ruta de plataforma (o al revés) | Dos esquemas de autenticación, con **clave y audiencia distintas**; la política de las rutas de plataforma fija su esquema. El token de empresa falla en la firma y el de plataforma, en la audiencia |
| Que una mala configuración las junte | La API **no arranca** si `Jwt:PlatformSecret` falta, no es Base64, no mide 32 bytes o es igual que `Jwt:Secret`, ni si la audiencia coincide; el mensaje nunca reproduce una clave |
| Que la contraseña dé una sesión | El primer paso devuelve siempre un desafío (o un `401`); solo hay **tres** sitios que emiten sesiones y el de plataforma es uno solo; un test de arquitectura fija la lista |
| Que un desafío de empresa se canjee en plataforma (o al revés) | El desafío nace con una audiencia; el canje de otro mundo se trata como un desafío inventado, antes de mirar el código y sin gastar ningún intento |
| Que saber si un email es de operador sea posible | Un único `401`, con el mismo coste de contraseña, para seis causas distintas; el motivo real solo va al log (`1602`) |
| Que un operador dado de baja siga mandando 15 minutos | La operación consulta la base de datos en **cada** petición (una consulta); la baja surte efecto en la siguiente y deja el `1608` |
| Que una operación de plataforma se declare mal | Tercer tipo de declaración, excluyente con los otros dos, *fail-closed*, y solo permitido dentro de `OfiFlow.Application.Platform` |
| Que una sesión robada dure | 15 minutos, sin *refresh token*; renovarla exige repetir contraseña y código |

**Riesgo aceptado: un operador comprometido.** Con una sesión robada, el atacante hace lo que el rol permite hasta que alguien lo note y dé de baja la cuenta (o pasen los 15 minutos). Los controles buscan que se note —`1601`, `1602`, `1608` y una fila de auditoría por sesión y por acción— y que el daño máximo esté acotado: un operador solo ve metadatos de las empresas y solo puede suspender o reactivar, nunca leer sus clientes o trabajos (bloque 6).

### Dónde se guardan los logs

Todo lo anterior se escribe en la **consola** y, en desarrollo y en producción, también en un **fichero**: `logs/ofiflow-AAAAMMDD.log` relativo al directorio desde el que arranca la API, uno por día y con los últimos 14 ([ADR-015](../adr/ADR-015-logs-a-fichero-con-serilog.md)). La carpeta está en `.gitignore`: un log lleva Ids e IPs, y una IP es un dato personal. En producción esa carpeta debe tener **permisos restringidos** a la cuenta que ejecuta la API (lo hace el despliegue, no el código), y la ruta se puede cambiar sin recompilar con la variable de entorno `Serilog__WriteTo__0__Args__path`. En un contenedor, sin un volumen montado el fichero se pierde al reiniciar.

Tres decisiones que debes saber explicar:

- **Es JSON, un registro por línea, y no texto.** La ruta de la petición la elige quien llama y puede traer un salto de línea (`%0A`) seguido de una línea inventada (*log forging*, CWE-117). En un fichero de texto esa línea falsa parecería una entrada real; en JSON el salto queda escapado dentro del mismo registro. Se comprobó llamando a la API con una ruta así.
- **Serilog es un proveedor más, no sustituye al de Microsoft.** Los handlers siguen usando `ILogger<T>`; no se tocó ninguno. La instalación habitual (`UseSerilog`) reemplaza la fábrica de loggers y los tests que comprueban los eventos de seguridad dejarían de ver los logs y pasarían en falso.
- **Los fallos de arranque también quedan en el fichero.** Si la API no arranca (falta `Jwt:Secret` o `Mfa:EncryptionKey`, el puerto está ocupado...) no hay ningún manejador de peticiones que lo registre, así que `Program.cs` envuelve el arranque en un `try/catch` que lo escribe como `Fatal` con su traza y lo relanza (el proceso sigue cayendo). Se comprobó arrancando la API de verdad sin `Jwt`.
- **El filtro de ruido es propio.** Serilog ignora el `Logging:LogLevel` de Microsoft; se filtra con `Serilog:MinimumLevel` (`Microsoft` y `System` en `Warning`, para que cada petición no añada dos líneas).

Para encontrar un fallo, en PowerShell:

```powershell
Get-Content src\OfiFlow.Api\logs\*.log -Encoding UTF8 | ConvertFrom-Json |
  Where-Object { $_.'@l' -in 'Error','Warning' } | Select-Object '@t','@l','@m','@x'
```

Para seguir una petición concreta, filtra por su `TraceId` (el `traceId` que recibe el cliente en un error); para vigilar un evento, por `EventId.Id` (por ejemplo `1001`).

**Qué no vas a encontrar en el fichero**, para no buscar donde no hay: los errores 4xx esperables (validación, «no encontrado», reglas de negocio; no son bugs, aunque los 403 sí quedan como evento `1301`), todo lo que el código capture y se trague sin registrar, y los niveles `Debug` y `Trace`. Y si el propio fichero falla (disco lleno, sin permisos), Serilog lo ignora en silencio.

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
| `TenantMemberships_AreOnlyCreatedForBrandNewAccounts` | Solo el registro y aceptar una invitación crean una pertenencia, y siempre para una cuenta nueva (un operador de la plataforma nunca es miembro de una empresa) |
| `TheTenantsAllowList_StillCorrespondsToARealRead` | Control positivo de la lista blanca de `Tenants`: avisa si deja de corresponder a un código real |
| `NoCommandOrQuery_AcceptsTenantIdFromTheClient` | Ninguna operación recibe el `TenantId` del cliente |
| `EveryMappedEntity_IsTenantOwnedOrDeclaredGlobal` | Toda entidad es de una empresa (con filtro) o está declarada global |
| `EveryOperationOfTheApplication_DeclaresItsAuthorization` | Toda operación declara su permiso (o que es anónima) |
| `EveryFileSink_UsesAJsonFormatter` | Ningún log a fichero en texto plano (falsificación de entradas) |
| `EveryRealEnvironment_WritesLogsToAFile` | Development y Production escriben a fichero (producción no se queda sin dónde mirar) |
| `SerilogIsAProvider_NotAReplacementOfTheLoggerFactory` | Se usa `AddSerilog`, nunca `UseSerilog` |
| `AStartupFailure_IsLoggedAsFatal_AndRethrown` | Un fallo de arranque queda en el fichero como `Fatal` y el proceso sigue cayendo |

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
| **S** · Fuerza bruta de contraseñas | 5 logins por minuto y por IP, y bloqueo por cuenta (5 fallos / 15 min) | `RateLimiting.cs:44`, `AccountLockout.cs` | `Login_SixthAttemptWithinAMinute_IsRejectedWith429` |
| **S** · Suplantar con un token falso | Firma HMAC-SHA256 | `DependencyInjection.cs:105` | `AnAccessTokenWithAnAlteredTenantId_IsRejected` |
| **S** · Robar un refresh token | Rotación y detección de reutilización | `TokenService.cs:47-64` | `RotateRefreshTokenAsync_WithAlreadyRevokedToken_...` |
| **S** · Adivinar o reutilizar un token de invitación | 256 bits aleatorios, un solo uso, misma respuesta para los cuatro fallos, evento `1403` | `OpaqueToken.cs`, `AcceptInvitationCommandHandler.cs` | `UnknownExpiredRevokedAndUsedTokens_AllGiveTheSameCode_AndCreateNothing` |
| **E** · Escalada de privilegios invitando a un `Admin` o a un `Owner` | Jerarquía de roles (`ManageableRoles`) comprobada al invitar y al revocar | `InviteUserCommandHandler.cs`, `ManageableRoles.cs` | `WhenTheHierarchyForbidsIt_ItIsRefused_AndNothingIsCreated` |
| **E** · Un empleado despedido (o degradado) sigue usando su token | Se revalida la pertenencia y el rol en la base de datos en cada petición; evento `1302` | `AuthorizationBehavior.cs`, `CurrentUser.cs` | `AMemberWhoIsRemoved_IsDeniedImmediately_WithTheSameStillValidToken`, `ADemotedMember_LosesThePowersOfTheOldRoleImmediately` |
| **E** · Un token que dice "Owner" pero es de un `Technician` | El rol sale de la base de datos; el claim del token no autoriza nada | `CurrentUser.cs` | `ATokenThatClaimsAHigherRole_DoesNotEscalate_TheDatabaseRoleDecides` |
| **S** · Contraseña robada o adivinada | Segundo factor (TOTP, RFC 6238) más el bloqueo de cuenta. | `Totp.cs`, `MfaService.cs` | `TotpTests` (vectores oficiales de las RFC) |
| **I** · Base de datos robada: secretos del segundo factor | Cifrado AES-256-GCM atado al Id de la cuenta; la clave vive fuera de la base de datos | `AesGcmSecretProtector.cs` | `SecretProtectorTests`, `TheSecret_StoredInTheDatabase_IsTheEncryptedValue_NeverThePlainSecret` |
| **S** · Un código TOTP visto por encima del hombro y reutilizado | El último paso aceptado se anota con un `UPDATE` condicional atómico | `MfaService.cs` | `FourSimultaneousUses_OfTheSameTotpCode_ProduceExactlyOneValid` |
| **E** · Un token de acceso robado intenta activar o quitar el MFA | Pide la contraseña (y un código para desactivar); los fallos cuentan para el bloqueo | `StartMfaSetupCommandHandler.cs`, `DisableMfaCommandHandler.cs` | `DisableAndRegenerateMfaTests`, `StartAndEnableMfaTests` |
| **E** · Un `Admin` comprometido quita el MFA del dueño | La jerarquía de roles también manda al reiniciar | `ResetUserMfaCommandHandler.cs` | `EveryCellOfTheHierarchy_IsEnforced` |
| **S** · Entrar solo con la contraseña en una cuenta con MFA | La contraseña da un desafío, no una sesión; solo el login sin MFA y el canje del desafío pueden emitir sesiones | `LoginCommandHandler.cs`, `VerifyMfaChallengeCommandHandler.cs` | `AnAccountWithMfa_AfterTheCorrectPassword_GetsAChallenge_AndNoSessionAtAll`, `OnlyTheLoginAndTheChallengeRedemption_CanIssueANewSession` |
| **S** · Fuerza bruta del código de 6 dígitos | Tope de 5 intentos por desafío, bloqueo de cuenta y la contraseña correcta **no reinicia** el contador | `MfaService.cs`, `IdentityService.cs` | `AfterFiveWrongCodes_TheChallengeIsDead_EvenIfTheAccountWereNotLocked`, `AnAttackerWhoKnowsThePassword_CannotKeepResettingTheCounterToGuessCodesForever` |
| **S** · Canjear dos veces (o a la vez) el mismo desafío | Un solo uso: el canje es una sentencia condicional atómica | `MfaService.cs` | `FourSimultaneousRedemptions_OfTheSameChallengeAndCode_ProduceExactlyOneSession` |
| **E** · Presentar el desafío como token de acceso | No es un JWT: es un valor opaco que el middleware de tokens no acepta | `OpaqueToken.cs` | `ATokenIsNotAJwt_SoItCanNeverBePresentedAsAnAccessToken` |
| **I** · Base de datos robada: invitaciones utilizables | Solo se guarda el hash del token | `OpaqueToken.cs` | `TheTokenIsReturnedOnce_AndOnlyItsHashIsStored` |
| **S** · Usar la API sin sesión | `RequireAuthorization()` en cada grupo | `JobEndpoints.cs:21` | `WithoutAToken_EveryRoute_Returns401` |
| **T** · Alterar el token | La firma deja de coincidir | `DependencyInjection.cs:105` | `AnAccessTokenWithAnAlteredTenantId_IsRejected` |
| **T** · Datos mal formados o enormes | Validadores y longitudes máximas | `*CommandValidator.cs` | `Register_WithPasswordLongerThanMax_Returns400_Not500` |
| **R** · Negar una acción | Eventos de seguridad con usuario y empresa | `SecurityEventIds.cs` | `ADenial_IsLoggedAsASecurityEvent_...`, `TheEvents_AreLoggedOncePerFact_AndContainNoEmailNorPassword` |
| **I** · Ver datos de otra empresa | Filtro global por empresa | `ApplicationDbContext.cs:72` | `TenantIsolationEndToEndTests`, `CrossTenantWriteIsolationTests` |
| **I** · Entidad nueva sin filtro | Clasificación obligatoria | `TenantModelRules.cs` (tests) | `EveryMappedEntity_IsTenantOwnedOrDeclaredGlobal` |
| **I** · Un 500 que cuenta detalles internos | Mensaje genérico y detalle solo al registro | `GlobalExceptionHandler.cs` | `UnhandledException_Returns500WithoutInternalDetails_AndLogsIt` |
| **I** · Datos personales en los logs | El registro solo lleva identificadores | `IdentityService.cs`, `AuthorizationBehavior.cs` | `TheEvents_AreLoggedOncePerFact_AndContainNoEmailNorPassword`, `ADenial_IsLogged...` |
| **I** · Contenido leído o incrustado por otra web | Cabeceras de seguridad | `SecurityHeadersMiddleware.cs:28-39` | `EveryResponse_IncludesSecurityHeaders` |
| **I** · Secretos en el repositorio | gitleaks y *push protection* | `ci.yml`, `.gitleaksignore` | El propio job `gitleaks` en cada PR |
| **D** · Inundar el login o el registro | Límite de peticiones por IP | `RateLimiting.cs:44-46` | `Login_SixthAttemptWithinAMinute_IsRejectedWith429` |
| **D** · Contraseñas o textos gigantes | Máximos de longitud | `PasswordRules.cs`, validadores | `Register_WithPasswordLongerThanMax_Returns400_Not500` |
| **E** · Un rol hace lo que no le toca | Permisos por rol, antes de la validación | `AuthorizationBehavior.cs` | `EachRole_CanOrCannotPerformEachOperation` (65 casos) |
| **E** · Una operación sin permiso declarado | *Fail-closed* y test de arquitectura | `AuthorizationBehavior.cs` | `EveryOperationOfTheApplication_DeclaresItsAuthorization` |
| **E** · El cliente fija su empresa o su rol | Ningún comando acepta `TenantId` | Los `*Command.cs` | `NoCommandOrQuery_AcceptsTenantIdFromTheClient` |
| **E** · Inyección SQL | EF Core parametrizado y SQL a mano prohibido, salvo una sentencia parametrizada en `AccountLockout.cs` | `Directory.Build.props:17`, `AccountLockout.cs` | `SourceCode_DoesNotUseRawSqlApis`, `ParameterizedRawSql_IsOnlyUsedInTheLockout` |
| Bajar la conexión a HTTP | HTTPS y HSTS (365 días) | `Program.cs:82-87` | **Sin test propio** |
| **I** · Otra web lee, desde el navegador de un usuario, las respuestas de la API | CORS: lista cerrada de orígenes, sin comodín ni credenciales de navegador | `Program.cs:33-64`, `:91` | `CorsTests` |
| **E** · Crear una cuenta con acceso global desde fuera, o sin segundo factor | El alta solo existe como programa del servidor (sin ruta HTTP), con el primer código TOTP comprobado antes de guardar nada y todo en un único guardado | `OfiFlow.Admin`, `PlatformOperatorAdministration.cs:89`, `:116` | `PlatformOperatorToolTests`, `PlatformOperatorAdministrationTests` |
| **E** · Un token de empresa vale en las rutas de plataforma (o al revés) | Esquemas separados con clave y audiencia propias; la API no arranca si se parecen | `DependencyInjection.cs`, `PlatformAuthentication.cs` | `PlatformSessionEndToEndTests`, `PlatformStartupTests` |
| **E** · Un operador dado de baja sigue operando con su sesión | Revalidación en la base de datos en cada petición; evento `1608` | `CurrentUser.cs`, `AuthorizationBehavior.cs` | `ADisabledOperator_LosesTheSessionOnTheNextRequest_AndTheEventIsLogged`, `CurrentUserPlatformTests` |
| **I** · El operador ve datos de las empresas (personas, clientes, trabajos) | El listado devuelve exactamente cinco campos (un test fija la forma del registro y otro inspecciona el cuerpo entero buscando emails, nombres y datos de un cliente); el recuento de miembros es el único sitio que se salta el filtro de empresa y está en la lista blanca | `PlatformTenantDto.cs`, `ListTenantsQueryHandler.cs` | `TheList_HasOnlyTheFiveMetadataFields_AndNoPersonalDataAnywhereInTheBody`, `TheTenantShape_IsExactlyFiveFields_...` |
| **D** · Una sesión robada vuelca todas las empresas de golpe, o fuerza la API | Páginas de 100 como máximo (un tope mayor es un `400`, no una página recortada), página acotada para que no desborde, y 30 peticiones por minuto y por IP | `ListTenantsQueryValidator.cs`, `RateLimiting.cs` | `APageSizeOutOfRange_IsRejected_NotSilentlyTruncated`, `ThePanelRoutes_AreLimitedToThirtyPerMinutePerIp` |
| **R** · «Yo no suspendí esa empresa» | Suspender y reactivar escriben una fila de auditoría **en la misma transacción** (o las dos o ninguna) con el operador, la empresa, la acción y la hora; y los eventos `1604` y `1605` | `SuspendTenantCommandHandler.cs`, `PlatformAudit.cs` | `WhenTheAuditRowCannotBeWritten_TheTenantIsNotSuspended`, `EachChange_LeavesOneAuditRowWithTheOperator_...` |
| **T** · Dos peticiones suspenden la misma empresa a la vez y dejan dos filas de auditoría | Versión de fila en la empresa: gana una y la otra recibe el error de siempre (`tenant.already_suspended`) | `TenantConfiguration.cs` | `TwoRequestsHoldingTheSameTenant_OnlyOneCanCommit_SoThereIsOneAuditRow`, `ManySimultaneousSuspensions_...` |
| **E** · Un `Id` de empresa llega por la petición (mass assignment) | Solo dos operaciones, con nombre y de plataforma, pueden recibirlo; cualquier otra con `TenantId` rompe el test | `SuspendTenantCommand.cs`, `ReactivateTenantCommand.cs` | `NoCommandOrQuery_AcceptsTenantIdFromTheClient_ExceptTheTwoNamedPlatformOperations` |
| **S** · Entrar en la plataforma solo con la contraseña, o saber qué emails son de operador | Siempre desafío o un único `401` con el mismo coste; desafío con audiencia | `PlatformLoginCommandHandler.cs`, `VerifyPlatformChallengeCommandHandler.cs` | `EveryLoginFailure_IsTheSameResponse_WithTheSameCost`, `ACompanyChallenge_IsNotRedeemableOnThePlatform_...` |
| Dependencia con vulnerabilidad conocida | NuGetAudit como error y Dependabot | `Directory.Build.props`, `dependabot.yml` | El propio build y el pipeline |
| Vulnerabilidad en el código | Analizadores de seguridad como error y CodeQL | `.editorconfig`, `codeql.yml` | El propio build y el pipeline |
| Vulnerabilidad en la API en ejecución | Escaneo ZAP en cada cambio | `dast.yml` | El propio job `zap-scan` |

---


## Dónde están hoy los límites

Una lista honesta de lo que **no** está cubierto, de más a menos preocupante:

**Autenticación**
1. ✅ **Cerrado (spec 010): hay bloqueo de cuenta.** Además del límite por IP, cada cuenta se bloquea 15 minutos tras 5 fallos (capítulo 6), con el intento reservado antes de comprobar la contraseña para que las peticiones simultáneas no lo esquiven. **Riesgo aceptado:** quien conozca un email puede mantener esa cuenta bloqueada fallando a propósito; se mitiga con el límite por IP y los eventos `1005`/`1006`. Los NEXT: bloqueo progresivo y desbloqueo manual.
2. ✅ **Cerrado (spec 010) en el login: el tiempo de respuesta ya no delata si un email existe** (misma verificación completa para desconocido, bloqueada y errónea). **Sigue abierto** que el registro diga "ese email ya está registrado".
3. **No hay cierre de sesión en el servidor**, y un token de acceso no se puede anular antes de caducar (hasta 30 minutos). Desde la spec 009 esto afecta menos a la autorización, porque la pertenencia se revalida en cada petición, pero un usuario **sigue autenticado** hasta que el token caduca.

   Y, con el segundo factor (spec 010), estos límites se añaden a los de la autenticación:

   - **3b. El segundo factor es opcional**, también para `Owner` y `Admin`. Obligarlo exige un estado de «sesión solo para activar el MFA» y una pantalla que guíe; será obligatorio para el operador de la plataforma (spec 011) y, cuando haya frontend, para `Owner`/`Admin`.
   - **3c. El TOTP no resiste al *phishing*.** Una web falsa que pida la contraseña y el código en directo se los reenvía a la real. Lo resuelven las *passkeys* (WebAuthn), previstas como siguiente paso.
   - **3d. Un `Owner` que pierde el móvil y los códigos de recuperación queda fuera**, porque nadie de su empresa puede reiniciarle el MFA (la jerarquía lo impide a propósito). Lo resolverá el operador de la plataforma, tras comprobar su identidad.
   - **3e. El token de acceso ya emitido sigue valiendo hasta 30 minutos** después de activar, desactivar o reiniciar el MFA: se cierran los *refresh tokens*, no los de acceso.
   - **3f. La clave `Mfa:EncryptionKey` es un secreto que no se puede perder** (los secretos ya guardados quedarían ilegibles) y todavía no hay procedimiento de rotación; el formato guardado lleva un byte de versión para hacerlo posible.
   - **3g. El `catch` de `DbUpdateException` de *activar* no tiene un test que garantice que se ejecuta.** Tres confirmaciones simultáneas dan exactamente una activación y ningún 500, pero lo normal es que las perdedoras ya hayan perdido antes, en la anotación atómica del paso del código; el `catch` es la red de seguridad de la última carrera posible.

**Autorización**
4. ✅ **Cerrado (spec 009): el rol y la pertenencia se revalidan en cada petición.** Antes viajaban en el token y un cambio no se notaba hasta renovar la sesión (hasta 30 minutos). El coste es una consulta indexada más por petición autenticada.
5. **Un `Technician` puede ver y ejecutar cualquier trabajo de su empresa**, no solo los asignados a él.
6. **Las invitaciones envían el token a quien invita, no por email.** Hasta que exista un servicio de correo, el invitante recibe el token (una sola vez, con `Cache-Control: no-store`) y se lo pasa a la persona por su canal. Es una decisión consciente (D1 de la spec 009): el invitante nunca conoce ni elige la contraseña, pero **ve el token** y podría aceptarlo él mismo.

**Abuso y red**
7. **El límite de peticiones solo cubre `auth`.** Clientes y trabajos no tienen límite: alguien con un token válido puede crear datos sin freno.
8. **Los límites viven en memoria y por IP.** Se reinician al desplegar, no se comparten entre instancias, y detrás de un proxy inverso todos los clientes compartirían la IP del proxy hasta que se configure `UseForwardedHeaders`.
9. **El `CORS` depende de que la lista de orígenes de producción esté bien puesta.** En el repositorio solo está el origen de desarrollo; si falta el de producción, el frontend no podrá leer la API (falla cerrado, que es lo seguro), y si se pone uno de más, esa web podrá leer las respuestas de sus usuarios. Además, CORS solo frena a los navegadores: no limita a quien llama sin navegador.

**Observabilidad**
10. **Hay registro, pero no vigilancia**: nadie recibe alertas, ni hay un registro de auditoría persistente de lo que hacen las empresas (el de las acciones de la plataforma, spec 011, sí existe y no se borra).

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

**El programa de administración**
18. **No tiene autenticación propia.** Quien pueda ejecutar `OfiFlow.Admin` con la cadena de conexión y la clave del segundo factor puede crear o dar de baja a un operador; la frontera es el acceso al servidor. Además la auditoría guarda al operador afectado, no a quien ejecutó el programa (no tiene cuenta en OfiFlow). Y el tramo interactivo (teclear la contraseña sin eco, reintentar el código) no tiene test automático: lo que invoca sí, y se comprueba a mano al instalar.

**El panel de la plataforma (spec 011, bloque 6)**
19. **La suspensión no dice por qué ni avisa.** Los miembros ven un mensaje genérico y el operador no deja un motivo. Un texto libre es un dato nuevo que validar y proteger, y el aviso por email espera a que exista el servicio de correo (NEXT en la spec).
20. **Un operador comprometido puede suspender cualquier empresa**, es decir, dejar sin servicio a todos los clientes. Es el riesgo aceptado del ADR-016: se mitiga (segundo factor, bloqueo, sesión de 15 minutos, baja inmediata, límite de 30 peticiones por minuto, auditoría que no caduca) pero no se elimina. Con clientes de pago convendrá la aprobación entre dos operadores.
21. **La auditoría no tiene ruta de consulta.** Las filas se escriben, nunca se modifican ni se borran desde la aplicación, y hoy solo se leen entrando en la base de datos. Alguien con acceso directo a ella sí podría alterarlas (una garantía a nivel de base de datos queda como mejora).
22. **El listado no se puede buscar ni ordenar de otra forma**: alta descendente, paginado y el filtro por suspendida. Buscar por nombre y las estadísticas son NEXT.
23. **El recuento de miembros hace una subconsulta por empresa de la página** (100 como máximo). Está medido por un test (dos consultas por listado, no una por empresa), pero no se ha probado con miles de empresas.

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
9. Despiden a un empleado a las 10:00 y su token caduca a las 10:30. ¿Qué puede hacer a las 10:15 y por qué? ¿Qué cambió respecto a antes de la spec 009?

*(Respuestas: 1 = poner varias defensas una tras otra para que, si una falla, la siguiente aún proteja; un handler que olvidara filtrar por empresa seguiría estando tras la autenticación, los permisos y la validación. 2 = impide que otra web meta la página en un `iframe` (clickjacking); `OnStarting` las añade en el último instante, después de que el manejador de errores borre las cabeceras, para que los errores también salgan protegidos. 3 = prevenir es impedir (un permiso que deniega un 403); detectar es enterarse (el evento 1301 en el registro); responder es actuar (revocar todos los refresh tokens si se reutiliza uno revocado). 4 = todo el acceso a datos pasa por EF Core, que usa parámetros; el aviso `EF1002` es un error de compilación y un test falla si aparece `FromSqlRaw` o `ExecuteSqlRaw`. 5 = que el cliente fije campos que no debería (`tenantId`, `role`); ningún comando tiene propiedad `TenantId`, el JSON sobrante se descarta y un test lo impone. 6 = el `1301` (acceso denegado); un número estable permite filtrar y crear alertas aunque se cambie la redacción del mensaje. 7 = porque cada uno se verifica rompiendo a propósito el código real y comprobando que el test se pone en rojo, y los de extremo a extremo llevan controles positivos. 8 = por ejemplo, no hay bloqueo de cuenta, el límite de peticiones solo cubre `auth`, y el rol puede estar desfasado hasta 30 minutos; yo cerraría primero el límite de peticiones en clientes y trabajos, porque es barato y hoy un token válido permite crear datos sin freno. 9 = nada: en cada petición se comprueba en la base de datos que sigue en la empresa, así que a las 10:15 recibe un 403 y queda un evento `1302`, aunque su token siga sin caducar; antes de la spec 009 el rol y la pertenencia viajaban en el token y habría conservado el acceso hasta las 10:30. El coste es una consulta indexada más por petición.)*

## Siguiente capítulo

El capítulo 9 explica los **tests**: por qué hay cuatro proyectos, cómo se prueba contra un SQL Server real, qué son los controles positivos y las pruebas de mutación, y cómo se mide si una prueba de seguridad está vacía.
