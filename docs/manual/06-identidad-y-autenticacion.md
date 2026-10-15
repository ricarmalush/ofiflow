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

## Las tres operaciones

Hay tres rutas, todas **anónimas** (no exigen sesión, porque sirven justo para conseguirla):

| Ruta | Qué hace | Límite de peticiones |
|---|---|---|
| `POST /api/v1/auth/register` | Crea una empresa y su primer usuario | 3 por hora y por IP |
| `POST /api/v1/auth/login` | Comprueba la contraseña y entrega los tokens | 5 por minuto y por IP |
| `POST /api/v1/auth/refresh` | Cambia un *refresh token* por un par nuevo | 20 por minuto y por IP |

(Los límites están en [`RateLimiting.cs:28-30`](../../src/OfiFlow.Api/Common/RateLimiting.cs) y se aplican en [`AuthEndpoints.cs`](../../src/OfiFlow.Api/Endpoints/AuthEndpoints.cs).)

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
// IdentityService.cs:30-32
// Deliberadamente sin SaveChangesAsync: RegisterCommandHandler confirma esta fila
// junto con Tenant/User/TenantUser en una única transacción (spec 002).
dbContext.ApplicationUsers.Add(user);
```

**Y al revés, para quitar a alguien (spec 009).** Hay dos operaciones gemelas que siguen **la misma regla** de no confirmar por su cuenta: [`IdentityService.DeleteUserAsync`](../../src/OfiFlow.Infrastructure/Identity/IdentityService.cs) borra las credenciales (y con ello el email vuelve a quedar libre) y [`TokenService.DeleteRefreshTokensAsync`](../../src/OfiFlow.Infrastructure/Identity/TokenService.cs) borra **todos** los refresh tokens del usuario, también los ya revocados: así no puede renovar la sesión y no quedan filas huérfanas (los tokens guardan el `UserId` sin clave foránea, así que la base no las borraría sola). Las usa el caso de uso [`RemoveUserCommandHandler`](../../src/OfiFlow.Application/Tenancy/Commands/RemoveUser/RemoveUserCommandHandler.cs), junto con la pertenencia, la ficha y la desasignación de los trabajos sin terminar, en un único `SaveChanges`: **todo o nada**. Está probado con los servicios reales y SQL Server real: tras la baja no queda ninguna fila de esa persona, no puede iniciar sesión ni renovar la sesión, y su email se puede volver a usar. Y una baja que falla a medias no borra nada.

**Un límite que conviene entender.** El token de **acceso** que esa persona ya tuviera sigue valiendo hasta que caduque (30 minutos), porque el rol y la pertenencia viajan dentro del token. Cerrar esa ventana es el bloque 5 de la spec 009: revalidar el rol y la pertenencia en **cada** petición.

Solo anota "este usuario es nuevo". Es `SaveChangesAsync`, que llama el handler **una única vez** al final (línea 33), quien escribe las cuatro filas juntas: o se guardan todas, o ninguna. Hay un test que lo demuestra: [`Handle_WhenEmailAlreadyRegistered_ThrowsAndPersistsNothing`](../../tests/OfiFlow.Application.Tests/Identity/RegisterCommandHandlerTests.cs).

### El email es único

`ApplicationUser` tiene un **índice único** sobre el email normalizado en mayúsculas ([`ApplicationUserConfiguration.cs`](../../src/OfiFlow.Infrastructure/Persistence/Configurations/ApplicationUserConfiguration.cs)): `Marta@x.com` y `marta@X.com` son la misma persona. Se comprueba antes de crear (`IdentityService.cs:19-25`) y la base de datos lo garantiza además, por si dos registros llegaran a la vez.

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

No vale cualquier hash. OfiFlow usa el `PasswordHasher` de ASP.NET Core ([`IdentityService.cs:13`](../../src/OfiFlow.Infrastructure/Identity/IdentityService.cs)), que aplica **PBKDF2**, diseñado para ser:

- **Lento a propósito.** Para el servidor, comprobar una contraseña cuesta una fracción de segundo; para un atacante que prueba millones de combinaciones, esa lentitud multiplicada es un muro.
- **Con sal** (*salt*): un valor aleatorio distinto por contraseña, guardado junto al hash. Dos personas con la misma contraseña tienen hashes distintos, y un atacante no puede usar tablas precalculadas.

```csharp
// IdentityService.cs:28 (al crear)
user.PasswordHash = PasswordHasher.HashPassword(user, password);

// IdentityService.cs:66 (al entrar)
var result = PasswordHasher.VerifyHashedPassword(user, user.PasswordHash, password);
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
 ① IdentityService.ValidateCredentialsAsync   ¿existe el email? ¿el hash coincide?
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

**Una sola respuesta de fallo.** Tanto si el email no existe como si la contraseña es incorrecta, la API responde exactamente lo mismo: un **401** sin explicación (`result is null ? Results.Unauthorized()`, [`AuthEndpoints.cs:29`](../../src/OfiFlow.Api/Endpoints/AuthEndpoints.cs)). Decir "ese email no existe" regalaría a un atacante la lista de quién usa el sistema.

**El log sí distingue el motivo, pero sin datos personales.** Cuando falla, se registra el evento de seguridad `1001` con el motivo (`UnknownEmail` o `WrongPassword`) y, si se conoce, el `UserId`, nunca el email ni la contraseña (`IdentityService.cs:60-62`): en un ataque con miles de emails ajenos, el registro se llenaría de datos personales de terceros.

El paso ② consulta la pertenencia del usuario a una empresa con `IgnoreQueryFilters()` ([`LoginCommandHandler.cs:29`](../../src/OfiFlow.Application/Identity/Commands/Login/LoginCommandHandler.cs)): es una de las dos excepciones justificadas al filtro de empresa del capítulo 5, porque aún no hay empresa activa; es justo lo que el login determina.

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
  "iss": "OfiFlow.Api",           // quién lo emitió
  "aud": "OfiFlow.Client",        // para quién es
  "exp": 1791234567               // cuándo caduca
}
```

Lo escribe [`TokenService.GenerateAccessToken`](../../src/OfiFlow.Infrastructure/Identity/TokenService.cs) (líneas 103-123). Dura **30 minutos**.

### Por qué no se puede falsificar

La firma es la clave. El servidor calcula la firma con un secreto que solo él conoce. Si alguien cambia `"role": "Technician"` por `"Owner"`, o `tenant_id` por el de otra empresa, la firma que viaja **ya no corresponde** al contenido, y el servidor la rechaza con un 401. Para falsificar un token válido habría que conocer el secreto.

En cada petición, el servidor comprueba el token con estas reglas ([`DependencyInjection.cs:67-74`](../../src/OfiFlow.Infrastructure/DependencyInjection.cs)):

| Qué se comprueba | Qué evita |
|---|---|
| La **firma** | Un token falsificado o modificado |
| El **emisor** (`iss`) y el **destinatario** (`aud`) | Un token emitido para otra aplicación |
| La **caducidad**, sin margen de tolerancia (`ClockSkew = 0`) | Un token viejo reutilizado |

Y en la configuración, un detalle que dio un susto de verdad: `options.MapInboundClaims = false` (línea 63).

> **Un fallo real que encontramos.** Por defecto, la librería de JWT **renombra** algunos claims al validarlos: `role` pasa a llamarse `http://schemas.microsoft.com/ws/2008/06/identity/claims/role`, y `sub` pasa a otro nombre largo. El código de permisos buscaba `role` y no lo encontraba: hasta el `Owner` recibía un 403 en todo. El claim `tenant_id` no tiene renombrado, y por eso el aislamiento entre empresas nunca lo notó. Se corrigió con esa línea, y hay un test que usa la configuración **real** de producción y se pone en rojo si alguien la quita.

### El secreto

El secreto que firma los tokens es lo más delicado de todo el sistema: quien lo tenga puede fabricar un token de cualquiera. Por eso:

- **Nunca está en el repositorio.** En local vive en `dotnet user-secrets` (fuera del proyecto); en producción, en una variable de entorno o un almacén de secretos.
- **Si falta, la aplicación no arranca**, con un mensaje que indica el comando exacto ([`DependencyInjection.cs:49-52`](../../src/OfiFlow.Infrastructure/DependencyInjection.cs)). Prefiere no funcionar a funcionar con una clave débil por defecto.
- En los tests se genera uno distinto en cada ejecución.

### Un límite inevitable

Un JWT es *sin estado* (*stateless*): el servidor no guarda la lista de tokens vigentes, solo comprueba la firma. Eso lo hace rápido y escalable, pero significa que **un token válido no se puede "anular"** antes de que caduque. Por eso dura poco: 30 minutos. Si alguien lo robara, el daño tiene fecha de caducidad.

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

Un token revocado **no debería volver a aparecer** nunca, salvo que lo hayan copiado. Cuando el servidor lo detecta ([`TokenService.cs:46-63`](../../src/OfiFlow.Infrastructure/Identity/TokenService.cs)):

- **Revoca todos los refresh tokens activos de ese usuario**, incluido el T2 que tenía Marta. El ladrón se queda sin nada, y Marta también (tendrá que volver a iniciar sesión, que es lo seguro).
- Registra el evento de seguridad `1003` (*reutilización de refresh token*) con el usuario y la empresa, sin el token.

Dos tests lo demuestran: `RotateRefreshTokenAsync_WithAlreadyRevokedToken_RevokesWholeChainAndReturnsNull` y `..._LogsSecurityEventWithoutTheToken`.

---

## Parte 6 — Invitaciones: entrar en una empresa que ya existe (spec 009, en construcción)

Hasta aquí, la única forma de tener cuenta es **registrarse**, y eso crea una empresa nueva donde eres el `Owner`. Para que un técnico o una persona de oficina entre en **la empresa de otro** hace falta otra puerta: la **invitación**.

> **Estado.** La lógica ya existe y está probada en `Application`, pero **todavía no hay endpoints**: no se puede usar desde la API ni desde Swagger. Llegan en el bloque 6 de la spec. Lo que sigue es cómo funciona por dentro.

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
- Se genera y se hashea en **un único sitio**, [`InvitationToken`](../../src/OfiFlow.Application/Tenancy/InvitationToken.cs), para que crear y aceptar usen exactamente la misma regla.

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

**Una segunda lección, más pequeña.** Un test intermitente de mi propia comprobación del formato del token destapó que la función de .NET que usé (`Base64Url.IsValid`) **ignora los espacios en blanco**: un token con un espacio dentro podía darse por bueno según el azar de su último carácter. No era una vulnerabilidad (ese texto daría otro hash y no se encontraría), pero la función prometía "solo caracteres de URL" y no lo cumplía. Ahora se comprueba el alfabeto carácter a carácter. La moraleja: **no confiar en que una función de la librería hace exactamente lo que su nombre sugiere**, y probar con muchos valores aleatorios, no con uno.

---

## Cómo se protege todo el flujo

Una tabla para ver qué amenaza frena cada control (el germen del mapa de amenazas final del manual). Las técnicas **T1110** son de MITRE ATT&CK, que verás en CyberOps:

| Amenaza | Control | Dónde | Test |
|---|---|---|---|
| **Fuerza bruta** de contraseñas (T1110) | Límite de 5 logins por minuto y por IP | `RateLimiting.cs:28` | `RateLimitingTests` |
| Contraseñas filtradas si roban la base de datos | Hash lento con sal (PBKDF2) | `IdentityService.cs:13,28` | `IdentityServiceTests` prueba crear y validar, pero **ninguno comprueba que lo guardado no sea la contraseña** (ver "Controles sin test") |
| Saturar el servidor con contraseñas enormes | Máximo de 128 caracteres | `PasswordRules.cs` | `Register_WithPasswordLongerThanMax_Returns400_Not500` |
| Saber qué emails están registrados | Un único 401 para email desconocido o contraseña mala | `AuthEndpoints.cs:29` | `Handle_WithInvalidCredentials_ReturnsNull` |
| Datos personales en los logs | El log no incluye email ni contraseña | `IdentityService.cs:60-62` | `ValidateCredentialsAsync_WhenFails_LogsSecurityEventWithoutEmailOrPassword` |
| Registro a medias (persona sin empresa) | Una sola transacción | `RegisterCommandHandler.cs:33` | `Handle_WhenEmailAlreadyRegistered_ThrowsAndPersistsNothing` |
| Falsificar o modificar un token | Firma HMAC-SHA256 | `DependencyInjection.cs:71` | `AnAccessTokenWithAnAlteredTenantId_IsRejected` |
| Token de acceso robado | Caduca a los 30 minutos (`ValidateLifetime`, sin margen) | `DependencyInjection.cs:73-74` | **Sin test propio** (ver "Controles sin test") |
| **Refresh token robado** | Rotación y detección de reutilización | `TokenService.cs:46-63` | `RotateRefreshTokenAsync_WithAlreadyRevokedToken_...` |
| Base de datos robada: tokens utilizables | Solo se guarda el hash SHA-256 | `TokenService.cs:133` | `RotateRefreshTokenAsync_WithValidToken_...` |
| Base de datos robada: invitaciones utilizables | Solo se guarda el hash del token de la invitación | `InvitationToken.cs`, `InviteUserCommandHandler.cs` | `TheTokenIsReturnedOnce_AndOnlyItsHashIsStored` |
| Adivinar o probar tokens de invitación | 256 bits aleatorios; forma validada antes de buscar; misma respuesta para los cuatro fallos; evento `1403` | `InvitationToken.cs`, `AcceptInvitationCommandHandler.cs` | `UnknownExpiredRevokedAndUsedTokens_AllGiveTheSameCode_AndCreateNothing` |
| Usar dos veces la misma invitación | Un solo uso + versión de fila: gana una | `Invitation.cs`, `InvitationConfiguration.cs` | `TwoSimultaneousAcceptances_OfTheSameToken_CreateExactlyOneAccount` |
| Un invitante que se queda con la contraseña de otro | La persona elige la suya al aceptar; el invitante solo fija email y rol | `AcceptInvitationCommand.cs` | `ThePersonCanThenLogInWithTheirOwnPassword` |
| Secreto débil o ausente | La aplicación no arranca sin él | `DependencyInjection.cs:49-52` | **Sin test propio** (ver "Controles sin test") |
| El rol del token no se lee bien | `MapInboundClaims = false`, probado con la configuración real | `DependencyInjection.cs:63` | `ATokenIssuedByTokenService_AndValidatedWithTheProductionJwtBearerConfig_...` |

---

## Dónde están hoy los límites

Un buen diseño conoce sus límites. Estos son los de este flujo, y casi todos están anotados como pendientes en el ADR-009:

**1. No hay bloqueo de cuenta.** El límite de peticiones es **por IP**, no por cuenta. Un ataque de *credential stuffing* (T1110.004: probar pares email/contraseña filtrados en otros sitios) repartido entre **miles de IPs**, con un intento por IP, no lo vería. Falta un contador de fallos por cuenta (*account lockout*).

**2. El tiempo de respuesta delata si un email existe.** La respuesta es idéntica, pero tarda distinto: si el email no existe, el servidor contesta enseguida; si existe, calcula el hash de la contraseña (lento a propósito). Un atacante que mida el tiempo puede distinguirlos. La solución es hacer el mismo trabajo en ambos casos; está en el ADR-009 como pendiente.

**3. El registro revela si un email ya está en uso.** Responde "el email ya está registrado". Es cómodo para el usuario, pero permite comprobar si alguien tiene cuenta. Se revisará cuando exista confirmación de email.

**4. No hay cierre de sesión en el servidor.** No existe un endpoint para revocar un refresh token. Hoy, "cerrar sesión" sería solo olvidar los tokens en el cliente, y el refresh token seguiría siendo válido hasta que caduque o rote.

**5. No hay confirmación de email ni recuperación de contraseña.** Es la razón de que no se use `UserManager` de ASP.NET Identity: se aplaza hasta que esas funciones se construyan (ADR-007).

**6. Un token de acceso no se puede anular** antes de caducar (hasta 30 minutos), por ser un JWT sin estado.

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
