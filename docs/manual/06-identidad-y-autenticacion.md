# Capítulo 6 — Identidad y autenticación

## Qué vas a aprender

- Qué es autenticar y en qué se diferencia de autorizar.
- Cómo se registra alguien en OfiFlow y por qué ese registro es atómico.
- Cómo se guarda una contraseña (y por qué nunca "se guarda" de verdad).
- Qué es un token JWT, qué lleva dentro y por qué no se puede falsificar.
- Para qué sirve el *refresh token*, cómo rota y cómo detecta un robo.
- Qué ataques frena este flujo, y qué límites tiene hoy.

Este capítulo explica el **token** que ya has visto en los capítulos 4 y 5: de dónde sale, qué contiene y cómo se protege.

## Autenticar no es autorizar

Son dos preguntas distintas, y se confunden a menudo:

| | Pregunta | Ejemplo | Dónde en OfiFlow |
|---|---|---|---|
| **Autenticación** (*authentication*) | ¿**Quién eres**? | "Soy Marta, y aquí está mi contraseña" | Este capítulo |
| **Autorización** (*authorization*) | ¿**Qué puedes hacer**? | "Marta es `Owner`: puede borrar clientes" | Capítulo 8 (y lo viste en el 4, paso 10a) |

En el modelo **AAA** de CyberOps (*Authentication, Authorization, Accounting*), esto es la primera A. La segunda es la tabla de permisos por rol. La tercera, *accounting*, es el registro de lo que pasa: los eventos de seguridad que verás aquí también.

## Las operaciones anónimas

Estas son las rutas **anónimas** de la primera hora (no exigen sesión, porque sirven justo para conseguirla, para quien aún no tiene cuenta o —el cierre de sesión— para poder salir con la sesión ya caducada; el canje del segundo factor, anónimo también, está en la Parte 7):

| Ruta | Qué hace | Límite de peticiones |
|---|---|---|
| `POST /api/v1/auth/register` | Crea una empresa y su primer usuario | 3 por hora y por IP |
| `POST /api/v1/auth/login` | Comprueba la contraseña y entrega los tokens | 5 por minuto y por IP |
| `POST /api/v1/auth/refresh` | Cambia un *refresh token* por un par nuevo | 20 por minuto y por IP |
| `POST /api/v1/auth/logout` | Cierra la sesión de un *refresh token* (lo revoca; spec 013, Parte 5) | 20 por minuto y por IP (cubo propio, distinto del de renovar) |
| `DELETE /api/v1/account/sessions` | Cierra **todas** las sesiones de la cuenta de quien lo pide (autenticada; spec 014, Parte 5) | 10 por hora y por IP (cubo propio) |
| `POST /api/v1/auth/accept-invitation` | Acepta una invitación con su token: la persona elige nombre y contraseña y nace su cuenta (Parte 6) | 10 por hora y por IP |

(Los límites están en [`RateLimiting.cs:44-46`](../../src/OfiFlow.Api/Common/RateLimiting.cs) y se aplican en [`AuthEndpoints.cs`](../../src/OfiFlow.Api/Endpoints/AuthEndpoints.cs).)

---

## Parte 1 — Registrarse

`RegisterCommand` recibe el nombre de la empresa, el nombre de la persona, el email y la contraseña. El handler ([`RegisterCommandHandler.cs`](../../src/OfiFlow.Application/Identity/Commands/Register/RegisterCommandHandler.cs)) crea **cuatro cosas** de una vez:

| Se crea | Qué es | Dónde vive |
|---|---|---|
| `ApplicationUser` | Las **credenciales**: email y hash de la contraseña | Infrastructure |
| `Tenant` | La empresa | Domain |
| `User` | La persona (nombre y email de contacto) | Domain |
| `TenantUser` con rol `Owner` | "Esta persona es la dueña de esa empresa" | Domain |

### Por qué todo ocurre en una sola transacción

Si se guardara primero el usuario y fallara la creación de la empresa, quedaría una persona con contraseña **sin empresa**: no podría entrar y su email estaría ocupado. Para evitarlo, [`IdentityService.CreateUserAsync`](../../src/OfiFlow.Infrastructure/Identity/IdentityService.cs) **no guarda nada**:

```csharp
// IdentityService.cs:40-41
// Deliberadamente sin SaveChangesAsync: RegisterCommandHandler confirma esta fila
// junto con Tenant/User/TenantUser en una única transacción (spec 002).
dbContext.ApplicationUsers.Add(user);
```

**Y al revés, para quitar a alguien (spec 009).** Hay dos operaciones gemelas que siguen **la misma regla** de no confirmar por su cuenta: [`IdentityService.DeleteUserAsync`](../../src/OfiFlow.Infrastructure/Identity/IdentityService.cs) borra las credenciales (y con ello el email vuelve a quedar libre) y [`TokenService.DeleteRefreshTokensAsync`](../../src/OfiFlow.Infrastructure/Identity/TokenService.cs) borra **todos** los refresh tokens del usuario, también los ya revocados: así no puede renovar la sesión y no quedan filas huérfanas (los tokens guardan el `UserId` sin clave foránea, así que la base no las borraría sola). Las usa el caso de uso [`RemoveUserCommandHandler`](../../src/OfiFlow.Application/Tenancy/Commands/RemoveUser/RemoveUserCommandHandler.cs), junto con la pertenencia, la ficha y la desasignación de los trabajos sin terminar, en un único `SaveChanges`: **todo o nada**. Está probado con los servicios reales y SQL Server real: tras la baja no queda ninguna fila de esa persona, no puede iniciar sesión ni renovar la sesión, y su email se puede volver a usar. Y una baja que falla a medias no borra nada.

**Y el token de acceso que ya tuviera no le sirve de nada.** El token sigue sin caducar hasta 30 minutos (un JWT no se puede anular por sí solo), pero ya no importa, por dos barreras que se comprueban en **cada** petición. La primera, desde la spec 015: el token lleva el Id de su sesión y esa sesión ya no existe (la baja borró sus refresh tokens), así que recibe un **401** en su siguiente petición (ver «El token muere con su sesión», más abajo). La segunda, la de la spec 009: se comprueba en la base de datos que la persona sigue en la empresa y con qué rol (capítulo 8), y si ya no es miembro pero su sesión seguía activa, recibe un **403**. En ningún caso a los 30 minutos.

Solo anota "este usuario es nuevo". Es `SaveChangesAsync`, que llama el handler **una única vez** al final (línea 33), quien escribe las cuatro filas juntas: o se guardan todas, o ninguna. Hay un test que lo demuestra: [`Handle_WhenEmailAlreadyRegistered_ThrowsAndPersistsNothing`](../../tests/OfiFlow.Application.Tests/Identity/RegisterCommandHandlerTests.cs).

### El email es único

`ApplicationUser` tiene un **índice único** sobre el email normalizado en mayúsculas ([`ApplicationUserConfiguration.cs`](../../src/OfiFlow.Infrastructure/Persistence/Configurations/ApplicationUserConfiguration.cs)): `Marta@x.com` y `marta@X.com` son la misma persona. Se comprueba antes de crear (`IdentityService.cs:27-35`) y la base de datos lo garantiza además, por si dos registros llegaran a la vez.

El registro **no devuelve tokens**: devuelve el `tenantId`. Para entrar hay que hacer login después.

---

## Parte 2 — La contraseña: hash, no cifrado

Una contraseña **nunca se guarda tal cual**, ni cifrada. Se guarda su *hash*. Son cosas distintas:

| | Se puede deshacer | Para qué |
|---|---|---|
| **Cifrado** (*encryption*) | Sí, con la clave | Guardar algo que luego necesitas volver a leer |
| **Hash** | **No** | Comprobar algo sin guardarlo |

No necesitamos volver a leer la contraseña de Marta: solo necesitamos saber si la que escribe hoy es la misma que eligió. Se calcula el hash de lo que escribe y se compara con el guardado. Si alguien robara la base de datos, no encontraría contraseñas, sino hashes que no se pueden deshacer.

### Un hash lento y con sal

No vale cualquier hash. OfiFlow usa el `PasswordHasher` de ASP.NET Core ([`IdentityService.cs:18`](../../src/OfiFlow.Infrastructure/Identity/IdentityService.cs)), que aplica **PBKDF2**, diseñado para ser:

- **Lento a propósito.** Para el servidor, comprobar una contraseña cuesta una fracción de segundo; para un atacante que prueba millones de combinaciones, esa lentitud multiplicada es un muro.
- **Con sal** (*salt*): un valor aleatorio distinto por contraseña, guardado junto al hash. Dos personas con la misma contraseña tienen hashes distintos, y un atacante no puede usar tablas precalculadas.

```csharp
// IdentityService.cs:38 (al crear)
user.PasswordHash = PasswordHasher.HashPassword(user, password);

// PasswordHasherChecker.cs:34 (al entrar)
return Hasher.VerifyHashedPassword(NoUser, storedHash, password) != PasswordVerificationResult.Failed;
```

### Longitud mínima y máxima

[`PasswordRules.cs`](../../src/OfiFlow.Application/Identity/PasswordRules.cs): mínimo **8** y máximo **128** caracteres.

- **Mínimo 8, sin exigir símbolos ni mayúsculas.** Es el criterio de **NIST SP 800-63B**: pesa más la longitud que la complejidad artificial, que empuja a la gente a poner `Contraseña1!`.
- **Máximo 128.** Parece raro limitar *hacia arriba*, pero tiene motivo de seguridad: como el hash es lento a propósito, hashear una contraseña de **un millón de caracteres** gastaría mucha CPU. Sin tope, un atacante podría tumbar el servidor enviando contraseñas enormes. Un test lo comprueba: la API responde 400, no 500.

---

## Parte 3 — Iniciar sesión

```
 email + contraseña
        │
        ▼
 ① IdentityService.AuthenticateAsync          ¿existe? ¿bloqueada? ¿el hash coincide?
        │  no → null  ─────────────────────────────────────────▶ 401
        ▼
 ② LoginCommandHandler busca la empresa del usuario   (TenantUser)
        │  no tiene → null  ───────────────────────────────────▶ 401
        ▼
 ③ TokenService.IssueTokensAsync              token de acceso + refresh token
        │
        ▼
      200 { accessToken, refreshToken, expiresAt }
```

Dos decisiones de seguridad en este flujo:

**Una sola respuesta de fallo.** Tanto si el email no existe, como si la contraseña es incorrecta, como si la cuenta está bloqueada (más abajo), la API responde exactamente lo mismo: un **401** sin explicación (`default: return Results.Unauthorized();` del `switch` sobre el resultado del login, [`AuthEndpoints.cs:45`](../../src/OfiFlow.Api/Endpoints/AuthEndpoints.cs)). Decir "ese email no existe" regalaría a un atacante la lista de quién usa el sistema.

**El log sí distingue el motivo, pero sin datos personales.** Cuando falla, se registra el evento de seguridad `1001` con el motivo (`UnknownEmail` o `WrongPassword`) y, si se conoce, el `UserId`, nunca el email ni la contraseña (`IdentityService.cs:90` y `:109`): en un ataque con miles de emails ajenos, el registro se llenaría de datos personales de terceros.

El paso ② consulta la pertenencia del usuario a una empresa con `IgnoreQueryFilters()` ([`LoginCommandHandler.cs:37`](../../src/OfiFlow.Application/Identity/Commands/Login/LoginCommandHandler.cs)): es una de las dos excepciones justificadas al filtro de empresa del capítulo 5, porque aún no hay empresa activa; es justo lo que el login determina.

**Una empresa suspendida no obtiene sesión** (spec 011). Tras la contraseña correcta, el login comprueba si la empresa de la persona está suspendida y, si lo está, responde **403** con `tenant.suspended` (con una contraseña errónea sigue siendo el 401 de siempre, sin revelar nada). Se hace **antes** de crear el desafío del segundo factor: sin sesión posible no hay segundo paso. No cuenta como intento fallido: se pueden hacer siete intentos seguidos y la cuenta no se bloquea por ello. El detalle de los cinco sitios que lo hacen cumplir está en el [capítulo 5](./05-multi-tenancy.md).

### El operador de la plataforma: una cuenta que no es de ninguna empresa (spec 011)

Todo lo anterior habla de personas que **pertenecen a una empresa**. La spec 011 añade una excepción deliberada: el **operador de la plataforma**, que puede ver qué empresas existen y suspender o reactivar una, **sin ver nada de lo que hay dentro**. Es la excepción más peligrosa al aislamiento (una cuenta robada vería todas las empresas), así que se construye lo más estrecha posible. Esta parte explica su identidad; la sesión viene en la sección siguiente y las operaciones (listar, suspender y reactivar), en el anexo del capítulo 4 y en el capítulo 8.

**Qué es un operador.** Una cuenta normal (mismo hash de contraseña, mismo segundo factor, mismo bloqueo) **más una fila en [`PlatformOperator`](../../src/OfiFlow.Infrastructure/Identity/PlatformOperator.cs)**. No es un rol de empresa ni un booleano en la cuenta, por dos motivos: un rol vive *dentro* de una empresa y el operador no tiene ninguna, y un solo bit mal cambiado en la cuenta daría acceso global, mientras que una tabla aparte se puede auditar y proteger por separado. La baja **no borra la fila**: rellena `DisabledAt`, y la fila conserva quién fue y cuándo.

**La regla que lo sostiene: una cuenta es de empresa o de plataforma, nunca de las dos (R1).** Se garantiza por los dos lados:

- **Que un miembro no pueda hacerse operador:** el alta ([`PlatformOperators.AddAsync`](../../src/OfiFlow.Infrastructure/Identity/PlatformOperators.cs)) comprueba que la cuenta no tenga **ninguna** pertenencia, en cualquier empresa. Por eso usa `IgnoreQueryFilters()` (es el quinto sitio de la lista blanca del capítulo 5): sin empresa activa, el filtro la limitaría a la empresa del contexto y una cuenta de otra empresa pasaría por operador. Un test lo comprueba abriendo el contexto para *otra* empresa.
- **Que un operador no pueda hacerse miembro:** ninguna ruta añade una pertenencia a una cuenta que ya existe. Solo el registro y aceptar una invitación crean una pertenencia, y los dos con una cuenta **recién creada**. Un test de arquitectura ([`TenantMemberships_AreOnlyCreatedForBrandNewAccounts`](../../tests/OfiFlow.Api.Tests/Architecture/SecurityArchitectureTests.cs)) fija esos dos sitios: si mañana alguien escribe un «invitar a quien ya tiene cuenta», el test falla y obliga a repensar la regla.

**Un desafío del segundo paso no cruza de un mundo a otro (R9).** El desafío que recibe quien pone bien la contraseña ahora lleva una **audiencia** (`Tenant` o `Platform`). El canje de empresa solo acepta los de empresa y el de plataforma solo los de plataforma; uno de otro mundo se trata como un desafío desconocido, **antes de comprobar el código**, de modo que no gasta ni un intento de la cuenta ni el código (si contara, cualquiera podría bloquear una cuenta ajena con desafíos del otro mundo). Los desafíos que ya existían al migrar se marcan como de empresa, que es lo que eran.

**La auditoría solo se inserta.** Cada acción de un operador que cambia algo dejará una fila en [`PlatformAuditEntry`](../../src/OfiFlow.Infrastructure/Identity/PlatformAuditEntry.cs) (solo Ids, un código de acción y la hora), en la **misma transacción** que la acción: si la acción falla, no queda fila. El log no sirve de auditoría (caduca a los 14 días, ADR-015). El `ApplicationDbContext` rechaza cualquier intento de **modificar o borrar** una fila de auditoría. **Límite honesto:** eso protege contra el código de la aplicación, no contra alguien con acceso directo a la base de datos, ni contra una sentencia `ExecuteUpdate`; una garantía a nivel de base de datos (un usuario sin permiso de `UPDATE`/`DELETE` sobre esa tabla) queda como mejora futura.

Un operador **no se crea por HTTP**: lo da de alta el programa del servidor `OfiFlow.Admin`, enrolando el segundo factor en el mismo acto (capítulo 1 y capítulo 8).

### La sesión de plataforma: otro mundo, otra llave (spec 011, bloque 5)

Un operador entra por **su propia puerta**: `POST /api/v1/platform/auth/login` y `POST /api/v1/platform/auth/verify`. No comparte con las empresas ni la ruta, ni la clave, ni la audiencia, ni el esquema de autenticación. La razón es la misma que separa los dos tipos de cuenta: si fueran el mismo mundo, un solo fallo en una validación dejaría que un token de empresa valiera para suspender empresas.

| | Sesión de empresa | Sesión de plataforma |
|---|---|---|
| Clave que firma | `Jwt:Secret` | `Jwt:PlatformSecret` (otra; la API no arranca si es igual) |
| Audiencia | `Jwt:Audience` | `Jwt:PlatformAudience` (otra; la API no arranca si es igual) |
| Esquema de autenticación | `Bearer` (el de por defecto) | `PlatformBearer`, fijado por la política `PlatformOperator` |
| Qué lleva | `sub`, `tenant_id`, `role` | `sub`, `realm = platform`, `jti`. Ni empresa ni rol: un operador no tiene ninguno |
| Duración | 30 minutos, con *refresh token* | **15 minutos fijos, sin refresh token**: pasado el tiempo hay que repetir contraseña y código |
| Quién la emite | Login sin segundo factor, canje del desafío y *refresh* | **Un único sitio**: el canje del desafío de plataforma |

**Siempre dos pasos.** El primer paso (`/login`) **nunca** devuelve una sesión: con las credenciales de un operador activo devuelve un desafío (`200 { mfaRequired, challenge, expiresAt }`); con cualquier otra cosa, un `401`. El segundo (`/verify`) canjea el desafío con un código de la app o de recuperación y devuelve la sesión. No hay operador sin segundo factor (el alta lo exige), y si por algún error faltara, la contraseña sola no da nada.

**Un único 401 (R6).** En el primer paso, seis casos —email desconocido, contraseña errónea, cuenta bloqueada, **cuenta de empresa con su contraseña correcta**, operador dado de baja y operador sin segundo factor— dan **exactamente la misma respuesta** (mismo estado, mismas cabeceras, mismo cuerpo) y cuestan **una verificación de contraseña cada uno** (los que no tienen hash real la hacen contra uno ficticio). Así quien intenta entrar no aprende si un email es de un operador, y la respuesta tampoco delata a una persona de empresa que puso bien su contraseña. En el segundo paso, un desafío desconocido, caducado, agotado o de la empresa, o un código erróneo o mal formado, también dan el mismo 401. El motivo real solo llega al registro de eventos (1602 y el 1504 del canje). *Lo único que no es un 401 es un campo vacío o enorme: es un error de validación (`400`), igual que en la puerta de las empresas.*

**Se revalida en cada petición (R10).** El token dice «soy un operador», pero lo que decide es la base de datos: la política de las rutas de plataforma deja pasar la petición y después la operación, declarada con [`[RequiresPlatformOperator]`](../../src/OfiFlow.Application/Common/Authorization/RequiresPlatformOperatorAttribute.cs), pregunta si esa cuenta **sigue siendo un operador activo** (una consulta, una vez por petición). Dar de baja a un operador surte efecto en **su siguiente petición**, no a los 15 minutos de su sesión; esa petición recibe un `403` y deja el evento `1608`. Es el mismo principio que ya usan el rol y la suspensión de empresa (capítulo 5).

**Tres tipos de declaración, y se excluyen.** Toda operación declara una de tres cosas: `[RequiresPermission]` (un rol de empresa), `[AllowAnonymousRequest]` (nadie en particular) o `[RequiresPlatformOperator]` (un operador). Combinar dos se deniega y lo detecta un test de arquitectura. Dentro del espacio `OfiFlow.Application.Platform` solo caben operaciones de plataforma o anónimas: ningún permiso de empresa se cuela en el mundo de la plataforma (R14).

**Qué se prueba, y cómo.** El ciclo completo contra SQL Server real; un token de empresa en una ruta de plataforma y al revés (siempre `401`); fabricar tokens con la clave de empresa y la audiencia de plataforma, o con la clave de plataforma y la audiencia de empresa; un token de plataforma sin el claim del mundo (`403`); un desafío de empresa canjeado en plataforma y al revés **sin gastarse** (con el código correcto sigue valiendo en su mundo); y que la API no arranca con la clave ausente, mal formada, de otra longitud o repetida. Como todavía no hay rutas de plataforma con efecto (llegan en el bloque 6), los tests añaden una ruta de prueba que pasa por el pipeline real.

**Un riesgo que no se cierra con código.** Mientras dure la sesión (15 minutos), un operador **al que han robado la sesión** puede hacer todo lo que el rol permite; la baja lo corta en la siguiente petición, pero hay que enterarse. Por eso cada sesión emitida deja el evento `1601` y una fila de auditoría, y las operaciones del bloque 6 dejarán la suya.

### El bloqueo de cuenta (spec 010)

El límite de 5 logins por minuto es **por IP**: no frena a quien reparte los intentos entre muchas IP contra una misma cuenta. Por eso la spec 010 añade un contador **por cuenta**: al quinto fallo (contraseña errónea), la cuenta se bloquea **15 minutos**. Lo importante no es el número, sino *cómo* se cuenta:

**1. El intento se reserva antes de comprobar la contraseña.** La idea ingenua es "leer el contador, comprobar la contraseña y sumar uno si falla". Falla con peticiones **simultáneas**: 50 peticiones leen a la vez "0 fallos", las 50 comprueban la contraseña y el bloqueo llega tarde; el atacante ha tenido 50 intentos en vez de 5. OfiFlow lo hace al revés: una única sentencia SQL, atómica, comprueba que la cuenta no está bloqueada **y** suma el intento, todo a la vez ([`AccountLockout.cs`](../../src/OfiFlow.Infrastructure/Identity/AccountLockout.cs)). Solo los intentos que consiguen reservar llegan a comprobar la contraseña, así que el máximo es 5 **aunque lleguen a la vez**. El intento que alcanza el umbral fija en la misma sentencia la hora de fin del bloqueo: no hay un hueco entre «llegó a 5» y «quedó bloqueada». Es la única sentencia SQL del proyecto fuera de LINQ, va parametrizada y está en una lista blanca que un test vigila.

**2. Bloqueada, ni la contraseña correcta entra.** Si entrara, bloquear no serviría de nada frente a quien sigue probando. Los intentos durante el bloqueo ni cuentan ni lo alargan.

**3. Desde fuera no se nota.** Una cuenta bloqueada, un email que no existe y una contraseña errónea dan **la misma respuesta** (401, mismos encabezados, mismo cuerpo) y **tardan lo mismo**: en los tres casos se hace una verificación de contraseña completa, contra un hash ficticio cuando no hay una real que comprobar. Ese hash se genera al arrancar a partir de un secreto que nadie conoce, así que verificar contra él no puede salir bien nunca. Esto cierra además el límite anotado en ADR-009: antes, un email inexistente respondía enseguida y uno existente tardaba el tiempo del hash, y medirlo delataba qué emails tenían cuenta. Cómo se comprueba sin cronómetros (que serían intermitentes): un verificador que **cuenta las llamadas** (capítulo 9).

**4. Solo un acceso completo pone el contador a cero.** En una cuenta **sin** segundo factor, la contraseña correcta es todo el acceso y lo reinicia. En una cuenta **con** segundo factor, la contraseña correcta **no** lo reinicia (devuelve su intento): si lo hiciera, quien conoce la contraseña podría reiniciar el contador y seguir adivinando códigos. Solo la contraseña **y** el código juntos lo reinician (Parte 7).

**Lo que el bloqueo no hace.** No cierra las sesiones ya abiertas (quien las abrió tenía la contraseña; bloquearle a él castigaría a la víctima), y tiene un coste: quien conozca un email puede mantener esa cuenta bloqueada fallando a propósito cada 15 minutos. Es el precio de bloquear por cuenta, y se mitiga con el límite por IP y con los eventos `1005` y `1006`, que hacen visible una ráfaga (capítulo 8).

---

## Parte 4 — El token de acceso (JWT)

### Qué es

Un **JWT** (*JSON Web Token*) es un texto con tres partes separadas por puntos:

```
eyJhbGciOi...   .   eyJzdWIiOi...   .   2Tq9xK...
  cabecera           contenido            firma
```

- **Cabecera:** el algoritmo de firma (HMAC-SHA256).
- **Contenido** (*payload*): los datos, llamados *claims*. Está **codificado, no cifrado**: cualquiera puede leerlo. Por eso **nunca** lleva contraseñas ni datos privados.
- **Firma:** un resumen del contenido calculado con el **secreto del servidor**.

El contenido de un token de OfiFlow es este (ejemplo con valores inventados):

```json
{
  "sub": "5f2c9e0a-...",          // quién eres (el Id del usuario)
  "tenant_id": "8d1f3b7c-...",    // en qué empresa trabajas
  "role": "Owner",                // con qué rol
  "sid": "c3a1b6d2-...",          // de qué sesión es (spec 015)
  "iss": "OfiFlow.Api",           // quién lo emitió
  "aud": "OfiFlow.Client",        // para quién es
  "exp": 1791234567               // cuándo caduca
}
```

Lo escribe [`TokenService.GenerateAccessToken`](../../src/OfiFlow.Infrastructure/Identity/TokenService.cs) (líneas 103-123). Dura **30 minutos**.

### Por qué no se puede falsificar

La firma es la clave. El servidor calcula la firma con un secreto que solo él conoce. Si alguien cambia `"role": "Technician"` por `"Owner"`, o `tenant_id` por el de otra empresa, la firma que viaja **ya no corresponde** al contenido, y el servidor la rechaza con un 401. Para falsificar un token válido habría que conocer el secreto.

En cada petición, el servidor comprueba el token con estas reglas ([`DependencyInjection.cs:98-108`](../../src/OfiFlow.Infrastructure/DependencyInjection.cs)):

| Qué se comprueba | Qué evita |
|---|---|
| La **firma** | Un token falsificado o modificado |
| El **emisor** (`iss`) y el **destinatario** (`aud`) | Un token emitido para otra aplicación |
| La **caducidad**, sin margen de tolerancia (`ClockSkew = 0`) | Un token viejo reutilizado |
| Que su **sesión siga activa** (`sid`, spec 015): hay un refresh token sin revocar y sin caducar de esa sesión y de esa cuenta | Un token que sigue sin caducar pero cuya sesión ya se cerró (el móvil perdido) |

Y en la configuración, un detalle que dio un susto de verdad: `options.MapInboundClaims = false` (línea 63).

> **Un fallo real que encontramos.** Por defecto, la librería de JWT **renombra** algunos claims al validarlos: `role` pasa a llamarse `http://schemas.microsoft.com/ws/2008/06/identity/claims/role`, y `sub` pasa a otro nombre largo. El código de permisos buscaba `role` y no lo encontraba: hasta el `Owner` recibía un 403 en todo. El claim `tenant_id` no tiene renombrado, y por eso el aislamiento entre empresas nunca lo notó. Se corrigió con esa línea, y hay un test que usa la configuración **real** de producción y se pone en rojo si alguien la quita.

### El secreto

El secreto que firma los tokens es lo más delicado de todo el sistema: quien lo tenga puede fabricar un token de cualquiera. Por eso:

- **Nunca está en el repositorio.** En local vive en `dotnet user-secrets` (fuera del proyecto); en producción, en una variable de entorno o un almacén de secretos.
- **Si falta, la aplicación no arranca**, con un mensaje que indica el comando exacto ([`DependencyInjection.cs:78-84`](../../src/OfiFlow.Infrastructure/DependencyInjection.cs)). Prefiere no funcionar a funcionar con una clave débil por defecto.
- En los tests se genera uno distinto en cada ejecución.

### Un límite inevitable (y cómo se resolvió)

Un JWT es *sin estado* (*stateless*): el servidor no guarda la lista de tokens vigentes, solo comprueba la firma. Eso lo hace rápido y escalable, pero significa que **un token válido no se puede "anular"** antes de que caduque. Por eso dura poco: 30 minutos. Si alguien lo robara, el daño tiene fecha de caducidad.

Hasta la spec 015 eso era todo lo que se podía hacer: cerrar la sesión cortaba la **renovación**, pero el token ya emitido seguía valiendo hasta 30 minutos. Ahora el token **no es puramente sin estado**: lleva el Id de su sesión y la API comprueba, además de la firma, que esa sesión siga activa. Es el precio de poder revocarlo, y está explicado en la [ADR-018](../adr/ADR-018-sesion-ligada-al-access-token.md) y en «El token muere con su sesión» (más abajo).

---

## Parte 5 — El refresh token

Treinta minutos son pocos: obligar a Marta a escribir su contraseña cada media hora sería insoportable. Para eso existe el **refresh token** (*token de renovación*): una segunda credencial, de larga duración (**7 días**), que solo sirve para pedir un par nuevo de tokens.

| | Token de acceso | Refresh token |
|---|---|---|
| Para qué | Acceder a la API | Conseguir un token de acceso nuevo |
| Dura | 30 minutos | 7 días |
| Dónde viaja | En cada petición | Solo en `POST /auth/refresh` |
| Qué es | Un JWT firmado | Una cadena aleatoria |

### Cómo se crea y cómo se guarda

[`TokenService.CreateRefreshToken`](../../src/OfiFlow.Infrastructure/Identity/TokenService.cs) (líneas 125-133) genera **64 bytes aleatorios** criptográficamente seguros. Eso es lo que recibe el cliente. En la base de datos **no se guarda ese valor**, sino su hash SHA-256 (entidad [`RefreshToken`](../../src/OfiFlow.Infrastructure/Identity/RefreshToken.cs), con un índice único sobre el hash).

Misma idea que con las contraseñas: si alguien robara la base de datos, no encontraría tokens utilizables.

Una pregunta natural: *¿por qué para las contraseñas usamos un hash lento (PBKDF2) y aquí SHA-256, que es rápido?* Porque las contraseñas las **elige una persona** (previsibles: se pueden adivinar probando muchas), mientras que este token son **64 bytes aleatorios**: no hay nada que adivinar, así que la lentitud no aporta nada.

### La rotación: cada token vale una sola vez

Cuando Marta renueva su sesión ([`RotateRefreshTokenAsync`](../../src/OfiFlow.Infrastructure/Identity/TokenService.cs), líneas 35-91), pasa esto:

1. El servidor busca el hash del token recibido. Si no existe → 401.
2. Si ya estaba **revocado** → alarma (ver abajo).
3. Si está **caducado** → 401.
4. Comprueba que Marta **sigue perteneciendo** a esa empresa y **vuelve a leer su rol** (puede haber cambiado desde el login).
5. **Revoca el token usado** y emite un par nuevo: otro token de acceso y **otro refresh token**.

Cada refresh token se usa **una sola vez**. El anterior queda inservible.

### La detección de robo

Aquí está lo ingenioso. Imagina que un ladrón copia el refresh token de Marta:

```
Marta usa su token T1  → recibe T2        (T1 queda revocado)
El ladrón usa T1       → T1 ya estaba revocado: ¡alguien lo está reutilizando!
```

Un token revocado **no debería volver a aparecer** nunca, salvo que lo hayan copiado. Cuando el servidor lo detecta ([`TokenService.cs:47-64`](../../src/OfiFlow.Infrastructure/Identity/TokenService.cs)):

- **Revoca todos los refresh tokens activos de ese usuario**, incluido el T2 que tenía Marta. El ladrón se queda sin nada, y Marta también (tendrá que volver a iniciar sesión, que es lo seguro).
- Registra el evento de seguridad `1003` (*reutilización de refresh token*) con el usuario y la empresa, sin el token.

Dos tests lo demuestran: `RotateRefreshTokenAsync_WithAlreadyRevokedToken_RevokesWholeChainAndReturnsNull` y `..._LogsSecurityEventWithoutTheToken`.

### Cerrar sesión de verdad (spec 013)

Hasta aquí, "Cerrar sesión" en la pantalla solo hacía que el navegador se **olvidara** de los tokens. El refresh token seguía vivo en la base de datos hasta 7 días: si alguien lo había copiado antes (un portátil compartido, una extensión maliciosa), podía seguir renovando la sesión **después** de que Marta pulsara "Salir". Un cierre de sesión que no cierra nada en el servidor es un fallo clásico.

Ahora existe `POST /api/v1/auth/logout` con el refresh token en el cuerpo ([`RevokeRefreshTokenAsync`](../../src/OfiFlow.Infrastructure/Identity/TokenService.cs)). Las decisiones, y el porqué de cada una:

| Decisión | Por qué |
|---|---|
| **Anónimo**: el refresh token es la credencial, no se pide token de acceso | El caso más frecuente es volver con el token de acceso ya caducado y pulsar "Salir". Exigir sesión para cerrarla obligaría a renovar primero, que es absurdo |
| **Solo esa sesión** (ese dispositivo) | Cerrar el portátil no debe echar al móvil. "Cerrar en todos" será su propia ruta, autenticada |
| **Se revoca, no se borra** la fila | Así, si alguien usa después ese token, `/auth/refresh` lo ve como un token revocado y dispara la detección de robo de arriba (evento `1003`, cadena revocada): un token que su dueño ya destruyó y alguien usa es, por definición, una copia. Borrarlo lo haría indistinguible de un token inventado |
| **`204` siempre** si la forma es válida (exista el token o no, esté caducado o ya rotado) | Si respondiera `401` a los inválidos, la ruta serviría para comprobar si un token robado sigue vivo. Quien no tiene el token no aprende nada |
| Cerrar con un token **ya rotado no revoca la cadena** | Presentarlo en `/refresh` es un *uso*; presentarlo en `/logout` es un *cierre*. Confundirlos haría que un cliente que cierra con un token viejo echara a todos sus dispositivos |
| Un evento `1007` **solo cuando se cierra algo de verdad**, con Ids y sin token ni hash | El frontend cierra sesión siempre, aunque ya no haya sesión: registrar cada intento sin efecto sería ruido |
| **Cubo de límite propio** (`auth-logout`, 20/min/IP) | Que cerrar sesión no gaste el presupuesto de renovar |

**Lo que esta spec no hacía, y la 015 resolvió:** el *token de acceso* ya emitido seguía valiendo hasta 30 minutos (es un JWT sin estado); lo que se cortaba era poder **renovarlo**. Un test (`TheAccessTokenAlreadyIssued_KeepsWorkingUntilItExpires_ThisIsTheKnownLimit`) dejaba ese límite escrito en el código. Desde la spec 015 ese test se llama `TheAccessTokenOfTheClosedSession_IsRejectedAtOnce_NotAfter30Minutes` y espera un **401** (ver «El token muere con su sesión», más abajo).

### Cerrar todas mis sesiones (spec 014)

El caso que el cierre de una sola sesión no resuelve es **perder el móvil**: la sesión que hay que cerrar es la de *otro* dispositivo, y desde el portátil no se tiene su refresh token. Ahora existe `DELETE /api/v1/account/sessions` ([`RevokeAllRefreshTokensAsync`](../../src/OfiFlow.Infrastructure/Identity/TokenService.cs)), que revoca **todos los refresh tokens activos de la cuenta**, también el del dispositivo desde el que se pide. Lo que cambia respecto al cierre de una sola, y por qué:

| Decisión | Por qué |
|---|---|
| **Autenticada** y colgada del grupo `/account`, no de `/auth` | A diferencia de `logout`, aquí la credencial es el access token: la cuenta sale **del token**. El grupo `/auth` es anónimo y su marca gana a `RequireAuthorization()` (lo aprendimos en la spec 013); una ruta autenticada dentro de él seguiría siendo anónima en HTTP |
| **Sin Id, cuerpo ni parámetros** | Nadie puede pedir las sesiones de otra persona por aquí. Si la petición trae un `userId` ajeno, se ignora. Un test comprueba que el comando **no tiene propiedades** y otro que `/account/sessions/{id}` da `404` |
| **Un permiso nuevo, `Account.CloseSessions`**, para los cinco roles | Un permiso dice *qué se hace*: ni «ver quién soy» (`Account.Read`) ni «gestionar mi segundo factor» (`Mfa.Manage`) son cerrar sesiones aunque lo tengan los mismos roles. La matriz pasa de 60 a 65 celdas |
| **Se revoca, no se borra** (como la spec 013) | Si un ladrón usa después su copia, `/auth/refresh` la ve como un token revocado y deja el evento `1003`. **Coste que conviene saber:** esa reutilización revoca también los tokens activos de la cuenta, así que si la víctima ya volvió a entrar, el intento del ladrón le cierra la sesión nueva (falla hacia cerrar, que es el lado seguro) |
| **No pide la contraseña** | Solo *quita* acceso, nunca lo da, y se usa en el momento más urgente. Quien tenga una sesión robada solo podría molestar (cerrar la de su dueño), no escalar. La contraseña sí se pide para activar o desactivar el segundo factor porque eso cambia las credenciales |
| **`204` haya o no sesiones activas** | Idempotente; quien pide ya sabe quién es, no hay nada que ocultar |
| **Un evento `1008` en cada petición, con el recuento (cero incluido)** | A diferencia de `1007`, es una acción explícita y rara de una persona identificada: «alguien pidió cerrar todo y no había nada» es información. Lleva el `UserId` y un número, nunca tokens ni hashes |
| **`Cache-Control: no-store`** y **cubo de límite propio** (`account-sessions`, 10/hora/IP) | Como el resto de la cuenta; y que gastar el cubo de cerrar todas no afecte a renovar ni a cerrar una sesión |

**Lo que esta spec no hacía, y la 015 resolvió:** el *token de acceso* ya emitido seguía valiendo hasta 30 minutos, igual que en la spec 013. Era justo el caso del móvil perdido: el ladrón conservaba ese margen aunque ya no pudiera renovarlo. Ahora los access tokens de **todas** esas sesiones dan `401` en la petición siguiente (el test se llama `TheAccessTokensOfEveryClosedSession_AreRejectedAtOnce_NotAfter30Minutes`). **Carrera conocida y aceptada:** si en el mismo instante hay un `/auth/refresh` en vuelo de esa cuenta, puede nacer un token nuevo justo después de la revocación y sobrevivir; la ventana es de milisegundos y repetir la operación lo cierra. No se añade bloqueo (coste desproporcionado para el riesgo).

Otra consecuencia de la spec 015: **quien llama siempre tiene al menos una sesión activa (la suya)**, porque su propio access token solo vale si esa sesión sigue viva. Así que el `204` con recuento **cero** que prometía la spec 014 ya no se alcanza por HTTP (una segunda llamada con el mismo token da `401`); el recuento cero sigue existiendo en el servicio y lo cubren sus tests.

### El token muere con su sesión (spec 015)

Hasta aquí, cerrar una sesión revocaba su **refresh token**: nadie podía renovar. Pero el *token de acceso* ya emitido, al ser un JWT sin estado, seguía valiendo hasta 30 minutos. La spec 015 lo ata a su sesión. Las piezas, y el porqué de cada una:

| Pieza | Qué hace y por qué |
|---|---|
| **`RefreshToken.SessionId`** | Una *sesión* es un inicio de sesión **y todas sus renovaciones**. Iniciar sesión crea un `SessionId` nuevo; renovar crea una fila nueva **con el mismo** (el token anterior se revoca y el nuevo se inserta en la misma confirmación a la base de datos, así que la sesión nunca se queda sin un refresh token activo). Dos dispositivos, dos sesiones: cerrar el portátil no echa al móvil |
| **El claim `sid`** del access token | Es el Id de la sesión. Lo escribe [`TokenService.GenerateAccessToken`](../../src/OfiFlow.Infrastructure/Identity/TokenService.cs) |
| **[`SessionTokenValidation`](../../src/OfiFlow.Infrastructure/Identity/SessionTokenValidation.cs)**, conectado al evento `OnTokenValidated` del esquema de las empresas | Después de comprobar la firma y la caducidad, pregunta a `ITokenService.IsSessionActiveAsync(userId, sessionId)`: *¿existe un refresh token **de esa sesión y de esa cuenta**, sin revocar y sin caducar?* Si no, `context.Fail` ⇒ **`401`** sin cuerpo |
| **Por qué va en la autenticación y da `401`, no `403`** | El interceptor del frontend solo renueva la sesión o vuelve al login ante un `401`. Un `403` (que es lo que daría comprobarlo en la autorización) dejaría a la persona ante errores sueltos en cada pantalla |
| **Por qué el `UserId` va en la pregunta** | Sin él, el Id de sesión de una cuenta valdría en el token de otra. Un test fabrica un token firmado de verdad con *mi* `sub` y el `sid` de la sesión de *otra* persona: da `401` |
| **Un token sin `sid` se rechaza** (fail-closed) | Son los emitidos antes de desplegar esto. Caducan en 30 minutos de todos modos y el frontend, al recibir el `401`, los renueva solo con su refresh token (que sigue valiendo: la migración da a cada fila antigua `SessionId = Id`) |
| **El evento `1009`**, nivel `Information` | Con el motivo (`MissingSessionId` o `SessionNotActive`) y el `UserId`; nunca el token ni el `sid`. Que llegue un token de una sesión cerrada es **normal** (una petición en vuelo, una pestaña que no se enteró): no merece `Warning` |
| **Solo el esquema de las empresas** | La sesión de plataforma (spec 011) no tiene refresh token (15 minutos fijos) y se comprueba por otra vía: que el operador siga activo, en cada petición |

**Qué cierra:** cerrar una sesión (spec 013), todas (spec 014), la detección de reutilización, activar o desactivar el segundo factor y quitar a un miembro dejan a la sesión sin refresh token activo, y por tanto cortan **también** su access token, en la petición siguiente. Esas operaciones no se tocaron: es la comprobación nueva la que las hace inmediatas.

**La migración, con cuidado.** La columna se añade **anulable**, cada fila existente pasa a ser su propia sesión (`SessionId = Id`) y solo entonces se vuelve `NOT NULL`, **sin valor por defecto**. Si EF la hubiera creado con el Guid vacío por defecto (lo que genera si no se corrige), todas las filas de hoy habrían sido **una sola sesión**. Se comprobó con la API real: sesiones creadas por la versión anterior sobre el esquema anterior, la migración aplicada encima y la versión nueva servida después. El access token anterior da `401`, y el refresh token anterior **renueva** y devuelve un token con `sid` igual al `SessionId` de la fila antigua.

**Lo que cuesta.** Una consulta más por petición autenticada. Medido en local con la API real (300 peticiones a `GET /account/me`): mediana 12,17 ms → 13,66 ms y p95 30,06 ms → 32,35 ms. El plan de ejecución es un `Index Seek` sobre `IX_RefreshTokens_SessionId` (con `UserId`, `RevokedAt` y `ExpiresAt` incluidas en el índice), 2 lecturas lógicas, sin `Key Lookup` ni `Scan`. Sin caché a propósito: reintroduciría una ventana. Y como se consulta en cada petición, **purgar** las filas caducadas o revocadas (spec 016) pasa de deseable a necesaria a medio plazo.

**Lo que no cierra.** Un token robado **mientras nadie cierra la sesión** sigue valiendo: esto cierra el robo *después* de cerrar sesión (el móvil perdido), no detecta que lo use otra persona. Una petición que ya pasó la comprobación cuando se cierra la sesión se completa; la siguiente falla.

---

## Parte 6 — Invitaciones: entrar en una empresa que ya existe (spec 009)

Hasta aquí, la única forma de tener cuenta es **registrarse**, y eso crea una empresa nueva donde eres el `Owner`. Para que un técnico o una persona de oficina entre en **la empresa de otro** hace falta otra puerta: la **invitación**.

> **Estado.** Ya tiene endpoints: `POST /api/v1/invitations` (invitar), `GET` y `DELETE` sobre esa misma ruta (ver y revocar) y `POST /api/v1/auth/accept-invitation` (aceptar), además de los de miembros del capítulo 8. Puedes probarlos en Swagger o en la sección 6 de la colección `.http`.

### El recorrido

```
 Owner/Admin                         La API                              La persona
     │  "invita a ana@… como Técnico"   │                                     │
     │ ────────────────────────────────▶│ genera un token aleatorio           │
     │                                  │ guarda solo su HASH                 │
     │ ◀──────── el token (UNA vez) ────│                                     │
     │                                  │                                     │
     │  se lo pasa por su canal (WhatsApp, en persona...)  ──────────────────▶│
     │                                  │                                     │
     │                                  │◀── token + nombre + contraseña ─────│
     │                                  │ busca la invitación por el hash     │
     │                                  │ crea cuenta + ficha + pertenencia   │
     │                                  │ marca la invitación como usada      │
```

Todavía no hay envío por email, así que el token se **devuelve a quien invita**, que se lo da a la persona. Lo importante es lo que **no** pasa: quien invita **nunca conoce ni elige la contraseña** de la otra persona (la elige ella al aceptar), y no puede cambiar el email ni el rol después.

### El token: igual que el refresh token

Es la misma idea del capítulo anterior, y por las mismas razones:

- **256 bits aleatorios** de un generador criptográfico: imposible de adivinar (se codifica en 43 caracteres aptos para un enlace).
- **Solo se guarda su hash** (SHA-256). Si roban la base de datos, no encuentran tokens utilizables.
- **Un solo uso y caduca a los 7 días.** Se puede revocar mientras esté pendiente.
- Se genera y se hashea en **un único sitio**, [`OpaqueToken`](../../src/OfiFlow.Application/Common/Security/OpaqueToken.cs), para que crear y aceptar usen exactamente la misma regla. Está en `Common/Security/` porque desde la spec 010 lo usa también el **desafío del login en dos pasos** (Parte 7): mismo generador, mismo hash, distinto uso.

Un detalle que se parece a la sal y no lo es: este hash **no lleva sal**, a diferencia del de las contraseñas. Una sal sirve para que dos contraseñas iguales no den el mismo hash y para frenar diccionarios; un token ya es aleatorio de 256 bits, no hay diccionario que atacar, y hace falta un hash **determinista** para poder buscarlo en el índice.

### Quién puede invitar a quién

Tener el permiso `Users.Manage` no basta: hay que poder gestionar **ese rol** (la tabla `ManageableRoles` del capítulo 8). Un `Admin` no puede invitar a otro `Admin` y nadie puede invitar a un `Owner`. Si se intenta, **403** y un evento `1301` con el motivo.

### Las reglas de la invitación

- Solo **una pendiente por email** en cada empresa (da igual cómo estén escritas las mayúsculas) y un **máximo de 25 pendientes** por empresa, para que nadie pueda llenar la base de datos.
- **No se invita a quien ya tiene cuenta**: hoy una cuenta pertenece a una sola empresa. (Esto revela a quien invita si un email tiene cuenta; es el mismo riesgo, aceptado, que ya tiene el registro.)
- Una invitación caducada o revocada **deja de contar** para esos límites.

### Aceptar: la operación más delicada

Es **anónima** (la persona aún no tiene cuenta) y por eso es la que más cuidado merece:

1. **Se valida la forma del token antes de tocar nada:** longitud exacta y solo caracteres de URL. Así no se hashea ni se busca texto arbitrario de tamaño ilimitado.
2. **La misma respuesta para cuatro fallos distintos.** Un token desconocido, caducado, revocado o ya usado devuelve **exactamente el mismo error** (`invitation.invalid`). Quien lo intenta no aprende cuál de los cuatro es. El motivo real **sí** queda en el log (evento `1403`), para un analista, sin el token ni el email.
3. **Se busca saltándose el filtro de empresa.** Aún no hay empresa activa; es el tercer uso justificado de `IgnoreQueryFilters()` (capítulo 5), y como los otros está en la lista blanca vigilada por un test.
4. **Todo o nada.** Cuenta, ficha, pertenencia y "invitación usada" se confirman en **una sola transacción**. Si algo falla, el token **no se consume**.
5. **Dos aceptaciones a la vez: gana una.** La invitación lleva una columna de versión (`rowversion`): si dos peticiones la leen a la vez como pendiente, la base de datos solo deja confirmar a una.

### Una lección: el test contra la base real cazó un 500

Al probar la carrera de dos aceptaciones con SQL Server de verdad, la perdedora **no** recibía el error de concurrencia que yo esperaba. Recibía una violación del **índice único del email**: las dos peticiones intentan crear una cuenta con el mismo email, y la base de datos rechaza la segunda *antes* de comprobar la versión de la invitación. Sin arreglo, esa petición habría acabado en un **error 500** en vez del mensaje de "token que ya no sirve".

La corrección fue tratar ambos fallos de la confirmación como lo mismo (ambos significan "este token ya no sirve"). Lo interesante para entender: **los tests con una base en memoria no podían ver esto**, porque no aplican índices únicos ni versiones de fila. Es una de las razones por las que los tests de integración usan SQL Server real (capítulo 9).

**Una tercera vuelta de la misma carrera (la cazó el CI, en la spec 011).** Hay una ventana más: las dos peticiones leen la invitación como pendiente, la primera confirma (cuenta y token consumido) y la segunda llega a *crear la cuenta* cuando el email ya existe. Eso salía como `user.email_already_registered`, no como `invitation.invalid`: la perdedora aprendía algo distinto de «este token ya no sirve». No es un fallo grave (el email es el de su propia invitación) pero rompe la regla de que todos los tokens que no sirven dan la misma respuesta (R7). Ahora, cuando la creación de la cuenta falla por email ocupado, el handler **vuelve a leer el estado actual de la invitación** (sin seguimiento: la que ya cargó sigue diciendo «pendiente»): si entre tanto se consumió, es `invitation.invalid`; si sigue pendiente, sí fue otra persona la que registró ese email y la invitación no se consume. Un test con un doble que consume la invitación justo antes de crear la cuenta lo fija (y falla sin la corrección).

**Una segunda lección, más pequeña.** Un test intermitente de mi propia comprobación del formato del token destapó que la función de .NET que usé (`Base64Url.IsValid`) **ignora los espacios en blanco**: un token con un espacio dentro podía darse por bueno según el azar de su último carácter. No era una vulnerabilidad (ese texto daría otro hash y no se encontraría), pero la función prometía "solo caracteres de URL" y no lo cumplía. Ahora se comprueba el alfabeto carácter a carácter. La moraleja: **no confiar en que una función de la librería hace exactamente lo que su nombre sugiere**, y probar con muchos valores aleatorios, no con uno.

---

## Parte 7 — El segundo factor (spec 010)

La spec 010 añade un **segundo factor** opcional (un código de 6 dígitos que cambia cada 30 segundos, en una app del móvil) y el bloqueo de cuenta, y es lo que protege una cuenta cuando la contraseña se roba o se adivina. Esta parte sigue el orden en que se construyó, que también es el orden en que conviene entenderlo: **cómo se guarda el secreto** (la decisión que más se parece a una trampa), **cómo se activa, se desactiva y se regenera**, y por último **cómo cambia el login**. Las rutas HTTP están en el capítulo 8 y los tests, en el 9.

### Por qué el secreto se cifra y no se hashea

Una contraseña se guarda como hash (Parte 2) porque el servidor **solo necesita comprobar** si lo que escribes coincide. El secreto TOTP es distinto: para calcular el código correcto de este instante, el servidor necesita **el secreto en claro**. Un hash no sirve, porque no se puede deshacer. Hay que guardarlo de forma que se pueda recuperar, y por eso se **cifra**: quien robe la base de datos se lleva texto ilegible, no los secretos de todos.

### AES-256-GCM, y lo que añade cada pieza

El cifrado es **AES-256 en modo GCM**, que además de ocultar el contenido lo **autentica**: si alguien cambia un solo byte del texto guardado, al descifrar no sale basura, sale un error. Lo guardado en la columna es `versión | nonce | etiqueta | texto cifrado`, en Base64 ([`AesGcmSecretProtector`](../../src/OfiFlow.Infrastructure/Identity/AesGcmSecretProtector.cs)):

| Pieza | Para qué |
|---|---|
| **Nonce** (12 bytes aleatorios, distinto en cada cifrado) | Repetirlo con la misma clave rompe GCM por completo. Por eso se genera uno nuevo cada vez, y un test cifra el mismo secreto mil veces y comprueba que no se repite ninguno |
| **Etiqueta** (16 bytes) | La que detecta cualquier manipulación |
| **Id de la cuenta como *dato asociado*** | No se cifra, pero queda atado al resultado. Si alguien con acceso a la base de datos copia el secreto de una cuenta a la fila de otra, **no se descifra**: el Id ya no coincide |
| **Byte de versión** | El día que haya que cambiar la clave, lo ya guardado dice con cuál se cifró |

### La clave es otro secreto más

Quien tenga la base de datos **y** la clave puede descifrar. Por eso la clave (`Mfa:EncryptionKey`) vive **fuera** de la base de datos, en user-secrets o en una variable de entorno (capítulo 1), y la API **no arranca sin ella** o si no mide 32 bytes: el mensaje de error dice qué pasa pero nunca reproduce el valor. Es el mismo trato que `Jwt:Secret`, y la razón por la que no se usó la herramienta de protección de datos de ASP.NET (guarda su llavero normalmente en la misma base de datos, así que no protegería frente a su robo).

### Tres tablas, ninguna de una empresa

| Tabla | Qué guarda |
|---|---|
| `MfaCredentials` | El secreto cifrado de la cuenta (como mucho uno, porque su clave es el propio Id de la cuenta), si ya está confirmado y el último paso de 30 s aceptado |
| `RecoveryCodes` | El **hash** de cada código de recuperación (10 por cuenta) y si se usó |
| `MfaChallenges` | El hash del desafío del login en dos pasos, cuándo caduca y cuántos códigos erróneos lleva |

Como `ApplicationUser` y `RefreshToken`, son credenciales de una **cuenta**: no llevan filtro de empresa (capítulo 5). Y como `RefreshToken`, **no tienen clave foránea** hacia la cuenta, así que la base de datos no las borra sola. Por eso `DeleteUserAsync` las borra explícitamente al quitar a un miembro, y un test contra SQL Server comprueba que no queda ninguna fila suya (y que las de otra persona siguen ahí).

Las tres llevan la columna de versión de fila (`rowversion`) de las invitaciones: cuando llegue la verificación, **dos peticiones simultáneas con el mismo código** leerán el mismo último paso, y solo una podrá guardar.

### Activar, desactivar y regenerar (bloque 4)

Todas estas operaciones son sobre **la propia cuenta** (permiso `Mfa.Manage`, que tienen los cinco roles): la cuenta sale del token, así que no hay ningún Id en la petición que manipular. Los pasos, y el porqué de cada uno:

| Paso | Qué exige | Por qué |
|---|---|---|
| **1. Pedir el secreto** | La **contraseña actual** | Un token de acceso robado (vale 30 minutos) no debe bastar para enrolar el móvil del atacante. La contraseña se comprueba **antes** que el estado de la cuenta: quien solo tiene el token no averigua, por un error distinto, si ya hay MFA |
| **2. Confirmar** | Un código válido de la app, del secreto pendiente | Hasta que no se demuestra que el móvil lo ha escaneado bien, el MFA **no cuenta como activo** (si no, un error al escanear dejaría a la persona fuera). Se devuelven **diez códigos de recuperación**, una sola vez |
| **3. Desactivar** | Contraseña **y** un código (de la app o de recuperación) | Quien solo tenga la contraseña, o solo un token, no puede quitar el segundo factor |
| **4. Regenerar códigos** | Contraseña y un código | Los anteriores, usados o no, dejan de valer |

**Activar y desactivar cierran todas las sesiones de la cuenta.** Lo que cambia es *cómo se demuestra quién eres*, y las sesiones abiertas se abrieron con las reglas antiguas. Se hace en el **mismo `SaveChanges`** que el cambio, para que sea todo o nada. Desde la spec 015, además, el token de acceso ya emitido de esas sesiones deja de valer en la petición siguiente (antes valía hasta 30 minutos): su sesión se queda sin refresh token activo.

**Un código no se acepta dos veces, ni siquiera a la vez.** El estándar (RFC 6238) lo exige: quien viera el código por encima del hombro podría reutilizarlo durante los 30 segundos de su vida. Se guarda el último paso aceptado, y se anota con un `UPDATE` condicional (`... WHERE LastUsedTimeStep < `): de cuatro peticiones simultáneas con el mismo código, **solo una cambia una fila**. Un `if` en el código no lo garantizaría, porque comprobar y escribir serían dos pasos. Con los códigos de recuperación pasa igual (`... WHERE UsedAt IS NULL`). Y cuando se acepta un código de un paso *posterior*, los anteriores dejan de valer aunque aún estén en la ventana de ±30 segundos.

**Los fallos cuentan para el bloqueo de cuenta** (Parte 3): la contraseña errónea y el código erróneo, al pedir el secreto, desactivar o regenerar, reservan y suman un intento como en el login. Con un matiz que se pensó a propósito: un **acierto** devuelve el intento pero **no pone el contador a cero**, porque no es un inicio de sesión; si lo hiciera, quien tuviera un token robado y la contraseña podría reiniciar el contador de otro ataque en curso. Los fallos al **confirmar** el primer código, en cambio, *no* cuentan: quien se equivoca al escanear el QR no debe quedarse 15 minutos sin poder entrar (se limitan por IP).

**Si se pierde el móvil.** Primero, los **códigos de recuperación**: diez de 80 bits, de un solo uso, de los que solo se guarda el hash. Y si faltan, **un gestor puede reiniciar el segundo factor** del miembro, con la misma jerarquía de roles de la spec 009 (capítulo 8): borra su secreto y sus códigos y cierra sus sesiones. Es idempotente (si no había MFA, no falla) y no se puede usar sobre uno mismo ni sobre el `Owner`.

**Por qué SHA-256 basta para los códigos de recuperación y no para una contraseña.** Una contraseña la elige una persona, así que existe un diccionario de candidatos y hace falta un hash lento (PBKDF2). Un código de recuperación es **aleatorio de 80 bits**: no hay diccionario, y probar 2^80 candidatos es inviable aunque alguien robe la base de datos.

### El login en dos pasos (bloque 5)

Con el segundo factor activo, la contraseña **ya no da sesión**. El recorrido:

```
 POST /auth/login  { email, contraseña }
        │
        ▼
 ① IdentityService.AuthenticateAsync     ¿existe? ¿bloqueada? ¿la contraseña coincide?
        │  sin MFA  → sesión como siempre ───────────────────────────────▶ 200 { accessToken, ... }
        │  con MFA  → devuelve la reserva (NO pone el contador a cero)
        ▼
 ② LoginCommandHandler                  crea un DESAFÍO (solo se guarda su hash) ▶ 200 { mfaRequired, challenge, expiresAt }
        │                                        ^ aquí no se emite ni un token
        ▼
 POST /auth/mfa/verify  { challenge, código }       (el desafío no es un token de acceso)
        │
        ▼
 ③ MfaService.RedeemChallengeAsync       ¿desafío vivo? ¿código válido? → lo canjea, UNA vez
        ▼
 ④ VerifyMfaChallengeCommandHandler      busca la empresa de la cuenta ▶ 200 { accessToken, ... }
```

**El desafío es opaco, no un JWT.** Sería tentador devolver «un token provisional» firmado. Pero un JWT del mismo emisor es un token como cualquier otro: un error de audiencia lo convertiría en un token de acceso. El desafío son **256 bits aleatorios** (el mismo generador que las invitaciones: `OpaqueToken`), de los que solo se guarda el **hash**, y no tiene la forma de un JWT, así que ni por error se acepta como sesión. Además, al guardarse en la base de datos puede hacerse lo que un JWT no permite: que **dure 5 minutos**, que **se use una sola vez** y que **muera a los 5 códigos erróneos**.

**Un solo uso, de verdad.** Canjearlo es una única sentencia condicional (`... WHERE ConsumedAt IS NULL AND ExpiresAt > ahora AND FailedAttempts < 5`): de cuatro canjes simultáneos, solo uno cambia la fila. Y como el código TOTP ya se anota de forma atómica (arriba), ni siquiera dos códigos distintos canjean el mismo desafío dos veces.

**Todos los fallos del segundo paso son iguales desde fuera** (401): desafío inventado, caducado, usado o agotado, código erróneo o repetido, cuenta bloqueada, o texto con la forma equivocada. El motivo real va al registro (`1504`). Por eso el validador de la petición solo pone un tope de tamaño y **no** comprueba la forma: un validador respondería 400, y esa diferencia ya diría que «la forma estaba mal».

**Qué cuenta contra el bloqueo.** Cada código erróneo suma uno al contador de la cuenta **y** gasta uno de los 5 intentos del desafío. Un desafío desconocido o caducado **no** cuenta contra la cuenta: no hay nadie intentando adivinar nada, y si contara, cualquiera podría bloquear una cuenta ajena inventando desafíos.

**La pieza clave: la contraseña correcta no reinicia el contador (R17).** Si lo reiniciara, un atacante que ya conoce la contraseña probaría 4 códigos, pondría la contraseña para volver a cero, probaría 4 más… y adivinaría el código sin límite (un código de 6 dígitos tiene un millón de valores). Por eso, con MFA, la contraseña correcta **devuelve** su intento reservado en vez de poner el contador a cero, y solo un **acceso completo** (contraseña y segundo factor) lo reinicia. Un test intercala la contraseña correcta entre los fallos y comprueba que el contador sigue donde estaba y que el quinto fallo bloquea la cuenta.

**Quién puede emitir sesiones.** Es la promesa de todo el segundo factor, y se vigila con un test de arquitectura: solo el login (rama sin MFA) y el canje del desafío llaman a `IssueTokensAsync`. Si alguien añade mañana otro camino (un «login con Google», un «recordar dispositivo»), el test falla y obliga a decidir, a propósito, qué pasa allí con el segundo factor. Otro test comprueba que, dentro del login, la rama «falta el segundo factor» termina con un `return` **antes** de la línea que emite la sesión.

**Un fallo que solo cazó la prueba de extremo a extremo.** El diseño inicial tenía un agujero sutil. Con **4 fallos previos**, el quinto intento *reserva*, el contador llega a 5 y la cuenta se bloquea **antes de saber si la contraseña era buena**. Si era buena (cuenta con MFA), devolver la reserva bajaba el contador a 4 pero **dejaba el bloqueo puesto**: una persona legítima que por fin acertaba con su contraseña quedaba fuera 15 minutos por un intento que no fue un fallo. Los tests de cada pieza por separado pasaban, porque cada uno probaba una parte; lo cazó el test que hace el recorrido entero (4 fallos, login con MFA completo, 4 fallos más). La corrección está en `ReleaseAsync`: si al devolver el intento el contador baja del umbral, **levanta también el bloqueo que esa misma reserva provocó**; un bloqueo de verdad (5 fallos reales) no se toca, porque el contador sigue en el umbral o por encima. Hay un test en cada nivel (el servicio, el login y el recorrido HTTP).

## Parte 8 — Quién soy: `GET /account/me` (spec 012)

Hasta aquí el cliente sabía quién es por lo que **decía el token** al emitirse. Eso tiene un problema que ya conoces: el rol del token puede estar desfasado hasta 30 minutos (la API lo revalida en la base de datos en cada petición, el frontend no). `GET /api/v1/account/me` es la forma de preguntarle **a la API, ahora**: devuelve tu nombre y email, tu empresa (Id y nombre), el Id de tu pertenencia, tu **rol efectivo** y los permisos de ese rol, con nombres estables (`Customers.Read`, `Jobs.Assign`...).

**Por qué es así:**

- **Sin parámetros, solo habla de ti.** La cuenta sale del token validado. No hay un Id que cambiar para ver a otra persona, y un `userId` o `tenantId` en la consulta se ignora (un test lo prueba).
- **El rol sale de la base de datos**, la misma verificación que ya hace el `AuthorizationBehavior` antes de cada operación. Así `/me` y un 403 no pueden contradecirse: tras un cambio de rol, el mismo token recibe el rol nuevo (un test cambia el rol con el token ya emitido y lo comprueba).
- **Un permiso propio, `Account.Read`**, que tienen los cinco roles. No se reutilizó `Mfa.Manage` aunque lo tengan los mismos: un permiso dice *qué se hace*, y «ver quién soy» no es «gestionar mi segundo factor». Cuesta una fila más en la tabla de `RolePermissions` y en su test; evita que alguien, dentro de un año, entienda mal por qué una cosa depende de la otra.
- **Los nombres de los permisos se derivan del enum** (`CustomersRead` → `Customers.Read`) en [`PermissionNames`](../../src/OfiFlow.Domain/Tenancy/PermissionNames.cs), en vez de escribir una tabla de textos a mano. Un permiso nuevo no puede quedarse sin nombre ni con otro formato, y los doce textos exactos están fijados a mano en un test (son un contrato con el frontend).
- **El Id de la pertenencia** (`membershipId`). El `sub` del token es el Id de tu *cuenta*, pero un trabajo se asigna a una *pertenencia* (`TenantUser`). Un técnico no puede listar el equipo (no tiene `Users.Read`), así que hasta ahora no tenía forma de saber cuáles eran «sus» trabajos. Con `/me` lo sabe: un test asigna un trabajo a un técnico y comprueba que su `membershipId` coincide con el `assignedTenantUserId` del trabajo.
- **Una sola sentencia que parte de la pertenencia filtrada por empresa.** La ficha de la persona y el nombre de la empresa se leen **a través** de esa fila (`TenantUsers` ⋈ `Users` ⋈ `Tenants`), así que ninguna de las dos tablas globales se consulta suelta. El handler entra con su motivo en las dos listas blancas de lectura (capítulo 5) y un test contra SQL Server real comprueba que es **una** sentencia y que una pertenencia de otra empresa es invisible incluso si el token la reclamara.
- **Quién no llega:** sin token, con basura o con una sesión de **plataforma**, un `401` (el operador no es una persona de empresa); con la empresa suspendida, el `403 tenant.suspended` de siempre; con la pertenencia ya borrada, un `403`.

**Un límite que hay que decir.** Los permisos de la respuesta sirven para **pintar la interfaz**, no para protegerla: quien decide es la API en cada operación. Un cliente que los falsifique solo consigue botones que dan 403.

---

## Cómo se protege todo el flujo

Una tabla para ver qué amenaza frena cada control (el germen del mapa de amenazas final del manual). Las técnicas **T1110** son de MITRE ATT&CK, que verás en CyberOps:

| Amenaza | Control | Dónde | Test |
|---|---|---|---|
| **Fuerza bruta** de contraseñas (T1110) | Límite de 5 logins por minuto y por IP, **y bloqueo por cuenta** (5 fallos / 15 min, con el intento reservado antes de comprobar) | `RateLimiting.cs:44`, `AccountLockout.cs` | `RateLimitingTests`, `FiftySimultaneousWrongAttempts_PerformExactlyFiveRealVerifications_AndLockTheAccount` |
| Contraseñas filtradas si roban la base de datos | Hash lento con sal (PBKDF2) | `IdentityService.cs:18,38` | `IdentityServiceTests` prueba crear y validar, pero **ninguno comprueba que lo guardado no sea la contraseña** (ver "Controles sin test") |
| Saturar el servidor con contraseñas enormes | Máximo de 128 caracteres | `PasswordRules.cs` | `Register_WithPasswordLongerThanMax_Returns400_Not500` |
| Saber qué emails están registrados | Un único 401 para email desconocido o contraseña mala | `AuthEndpoints.cs:45` | `Handle_WithInvalidCredentials_IsRejected` |
| Datos personales en los logs | El log no incluye email ni contraseña | `IdentityService.cs` (`LogLoginFailed` y los dos eventos del bloqueo) | `TheEvents_AreLoggedOncePerFact_AndContainNoEmailNorPassword` (SQL real) y `TheSecurityEvents_AreLoggedWithoutTheEmailNorThePassword` (HTTP) |
| Registro a medias (persona sin empresa) | Una sola transacción | `RegisterCommandHandler.cs:33` | `Handle_WhenEmailAlreadyRegistered_ThrowsAndPersistsNothing` |
| Falsificar o modificar un token | Firma HMAC-SHA256 | `DependencyInjection.cs:105` | `AnAccessTokenWithAnAlteredTenantId_IsRejected` |
| Token de acceso robado **tras cerrar la sesión** | La sesión del token ya no está activa (`sid`, spec 015): `401` en la petición siguiente | `SessionTokenValidation.cs` + `TokenService.IsSessionActiveAsync` | `TheAccessTokenOfTheClosedSession_IsRejectedAtOnce_NotAfter30Minutes`, `SessionTokenEndToEndTests` (19) |
| Token de acceso robado **sin cerrar la sesión** | Caduca a los 30 minutos (`ValidateLifetime`, sin margen) | `DependencyInjection.cs:106-107` | **Sin test propio** (ver "Controles sin test") |
| **Refresh token robado** | Rotación y detección de reutilización | `TokenService.cs:47-64` | `RotateRefreshTokenAsync_WithAlreadyRevokedToken_...` |
| Base de datos robada: tokens utilizables | Solo se guarda el hash SHA-256 | `TokenService.cs:142` | `RotateRefreshTokenAsync_WithValidToken_...` |
| Base de datos robada: invitaciones utilizables | Solo se guarda el hash del token de la invitación | `OpaqueToken.cs`, `InviteUserCommandHandler.cs` | `TheTokenIsReturnedOnce_AndOnlyItsHashIsStored` |
| Adivinar o probar tokens de invitación | 256 bits aleatorios; forma validada antes de buscar; misma respuesta para los cuatro fallos; evento `1403` | `OpaqueToken.cs`, `AcceptInvitationCommandHandler.cs` | `UnknownExpiredRevokedAndUsedTokens_AllGiveTheSameCode_AndCreateNothing` |
| Usar dos veces la misma invitación | Un solo uso + versión de fila: gana una | `Invitation.cs`, `InvitationConfiguration.cs` | `TwoSimultaneousAcceptances_OfTheSameToken_CreateExactlyOneAccount` |
| Un invitante que se queda con la contraseña de otro | La persona elige la suya al aceptar; el invitante solo fija email y rol | `AcceptInvitationCommand.cs` | `ThePersonCanThenLogInWithTheirOwnPassword` |
| Secreto débil o ausente | La aplicación no arranca sin él | `DependencyInjection.cs:78-84` | **Sin test propio** (ver "Controles sin test") |
| El rol del token no se lee bien | `MapInboundClaims = false`, probado con la configuración real | `DependencyInjection.cs:96` | `ATokenIssuedByTokenService_AndValidatedWithTheProductionJwtBearerConfig_...` |

---

## Dónde están hoy los límites

Un buen diseño conoce sus límites. Estos son los de este flujo, y casi todos están anotados como pendientes en el ADR-009:

**1. ✅ Cerrado (spec 010): hay bloqueo de cuenta.** Antes, el límite de peticiones era solo **por IP**: un ataque de *credential stuffing* (T1110.004: probar pares email/contraseña filtrados en otros sitios) repartido entre miles de IPs, con un intento por IP, no lo habría visto. Ahora hay un contador por cuenta (5 fallos → 15 minutos) que resiste las peticiones simultáneas (Parte 3). **Riesgo aceptado:** se puede mantener bloqueada una cuenta ajena fallando a propósito.

**2. ✅ Cerrado (spec 010): el tiempo de respuesta del login ya no delata si un email existe.** Antes, un email inexistente respondía enseguida y uno existente tardaba lo que cuesta el hash. Ahora se hace la misma verificación completa en los tres casos (desconocido, bloqueada, contraseña errónea). Sigue abierto, aparte, que el **registro** diga «el email ya está registrado».

**3. El registro revela si un email ya está en uso.** Responde "el email ya está registrado". Es cómodo para el usuario, pero permite comprobar si alguien tiene cuenta. Se revisará cuando exista confirmación de email.

**4. ✅ Cerrado (specs 013, 014 y 015): hay cierre de sesión en el servidor, de una o de todas, y corta también el token de acceso.** `POST /auth/logout` revoca el refresh token y `DELETE /account/sessions` los de todas las sesiones (Parte 5); desde la spec 015 el *token de acceso* de esas sesiones también deja de valer en la petición siguiente (lleva el Id de su sesión, `sid`; ver «El token muere con su sesión»). Siguen abiertos: listar mis sesiones y cerrar una concreta, y purgar los refresh tokens caducados o revocados (las filas se acumulan).

**5. No hay confirmación de email ni recuperación de contraseña.** Es la razón de que no se use `UserManager` de ASP.NET Identity: se aplaza hasta que esas funciones se construyan (ADR-007).

**6. ✅ Resuelto (spec 015): un token de acceso se puede anular antes de caducar.** Por ser un JWT sin estado no se podía (valía hasta 30 minutos); ahora lleva el Id de su sesión y la API comprueba en cada petición que esa sesión siga activa (ADR-018). **Lo que sigue abierto:** un token robado *mientras nadie cierra la sesión* sigue valiendo hasta que caduque, y es una consulta más por petición (medida, ver «El token muere con su sesión»).

**7. La rotación no tiene control de concurrencia explícito.** Si dos peticiones llegaran *a la vez* con el mismo refresh token, en teoría ambas podrían leerlo como válido antes de que ninguna lo revoque. No está probado ni se ha observado; lo apunto como pregunta abierta, no como fallo confirmado.

**8. El login toma la primera empresa del usuario.** Hoy cada usuario tiene una sola, así que no hay ambigüedad (capítulo 5).

**9. Los límites de peticiones viven en memoria**, por IP. Se reinician con cada despliegue y no se comparten entre instancias (ADR-009).

### Controles sin test propio

Al revisar la tabla anterior contra los tests, tres controles **existen en el código pero ningún test los comprueba directamente**. Funcionan (se ven al ejecutar la aplicación), pero si alguien los rompiera, nada lo avisaría:

| Control | Qué haría falta |
|---|---|
| La aplicación **no arranca sin el secreto** JWT | Un test que construya la configuración sin `Jwt:Secret` y exija el error con el mensaje del comando |
| El token **caduca** a los 30 minutos | Un test que valide con la configuración real un token ya caducado y exija un 401 |
| El hash guardado **no es la contraseña** | Un test que cree un usuario y compruebe que `PasswordHash` no contiene la contraseña y cambia entre dos usuarios con la misma (la sal) |

Son los tres candidatos más baratos para la próxima ronda de endurecimiento, con el mismo método que en las specs anteriores: escribir el test y verlo fallar rompiendo el código a propósito.

## Para comprobar que lo has entendido

1. ¿Qué diferencia hay entre autenticar y autorizar? Pon un ejemplo de OfiFlow de cada una.
2. Si alguien roba la base de datos, ¿qué encuentra de las contraseñas y de los refresh tokens, y por qué no le sirve?
3. ¿Por qué el hash de las contraseñas es lento a propósito, y por qué el de los refresh tokens puede ser rápido?
4. Un atacante cambia `"role": "Technician"` por `"Owner"` en su token. ¿Qué pasa y por qué?
5. Explica con tus palabras cómo detecta el sistema que le han robado un refresh token.
6. ¿Por qué las contraseñas tienen un **máximo** de 128 caracteres?
7. Nombra dos límites que tiene hoy el flujo de autenticación y cómo cerrarías uno.
8. En una invitación, ¿qué se guarda en la base de datos del token y por qué no se le añade una sal como a las contraseñas?
9. Un atacante prueba tokens de invitación al azar. ¿Por qué no le sirve, qué respuesta recibe y qué ve un analista?
10. Dos personas aceptan la misma invitación exactamente a la vez. ¿Qué garantiza que solo nazca una cuenta y qué error inesperado cazó el test contra SQL Server real?

*(Respuestas: 1 = autenticar es saber quién eres (el login con contraseña); autorizar es saber qué puedes hacer (que un `Technician` no pueda borrar clientes). 2 = solo hashes: el de las contraseñas es irreversible, con sal y lento de calcular; el de los refresh tokens es SHA-256 de 64 bytes aleatorios, que no se pueden adivinar ni deshacer. 3 = porque una contraseña la elige una persona y es previsible, así que la lentitud frena a quien prueba millones; un refresh token son 64 bytes aleatorios, sin nada que adivinar. 4 = la firma deja de corresponder al contenido y el servidor lo rechaza con un 401; solo con el secreto se podría firmar un token válido. 5 = cada refresh token se usa una vez y queda revocado; si alguien presenta uno ya revocado, es que lo copiaron, y el sistema revoca todos los tokens activos del usuario y registra el evento 1003. 6 = porque el hash es lento a propósito: sin tope, enviar contraseñas enormes gastaría CPU y tumbaría el servidor. 7 = por ejemplo, no hay bloqueo de cuenta (se cerraría con un contador de fallos por cuenta) y el tiempo de respuesta delata si un email existe (se cerraría haciendo el mismo trabajo de hash en ambos casos). 8 = solo el hash SHA-256; no lleva sal porque el token ya es aleatorio de 256 bits (no hay diccionario que atacar) y hace falta un hash determinista para buscarlo en el índice. 9 = no puede adivinar 256 bits aleatorios; además la forma del token se valida antes de buscar y recibe siempre el mismo error (`invitation.invalid`) sea cual sea el motivo; el analista ve eventos `1403` con el motivo real (y la IP), sin el token ni el email. 10 = la invitación tiene una columna de versión de fila y, además, el índice único del email impide dos cuentas iguales: solo una confirma y la otra recibe `invitation.invalid`; el test con SQL real cazó que la perdedora recibía una violación del índice único (que acababa en un 500) y no el error de concurrencia esperado, porque una base en memoria no aplica índices únicos.)*

## Siguiente capítulo

El capítulo 7 explica **errores y validación**: cómo se rechaza un dato incorrecto, el diccionario de códigos de error y mensajes, y por qué cada error devuelve un código estable en vez de un texto.
