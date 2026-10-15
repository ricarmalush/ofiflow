# Capítulo 7 — Errores y validación

## Qué vas a aprender

- Quién rechaza qué: las tres barreras que separan un dato incorrecto de la base de datos.
- Por qué un error lleva un **código** y no un texto, y cómo es ese código.
- Qué excepciones existen y por qué las genéricas de .NET se tratan como *bugs*.
- Cómo se valida la entrada con FluentValidation sin repetir reglas.
- Cómo una excepción acaba convertida en una respuesta HTTP ordenada.
- Cómo funciona el diccionario de mensajes y el idioma.
- Cómo añadir un error nuevo, paso a paso.

En los capítulos anteriores has visto errores sueltos (un 400, un 404, un 403). Aquí se explica el sistema entero que los produce.

## El problema

Piensa en lo que puede salir mal con una petición para crear un trabajo:

- El JSON viene roto.
- El título está vacío.
- El cliente no existe.
- Se intenta completar un trabajo que no está en curso.
- Falla la base de datos.

Son cinco tipos de problema, con causas y responsables distintos. Un sistema descuidado los mezcla: un texto suelto por aquí, un `500` por allá, y el cliente (una app, otro programa) no sabe qué hacer ante cada uno. OfiFlow los trata con un mismo mecanismo, y con cuatro objetivos (ADR-011):

1. **Ningún texto para el usuario dentro del código** de Domain ni de Application.
2. Poder **traducir** sin tocar esas capas.
3. Que el cliente **reconozca** cada error sin interpretar frases, que pueden cambiar.
4. **No confundir** un error de negocio con un fallo del sistema.

## Quién rechaza qué

Un dato incorrecto atraviesa hasta tres barreras. Cada una detiene lo suyo:

```
  JSON de la petición
        │
        ▼
 ① Lectura del cuerpo          JSON roto, tipo equivocado        → 400  request.invalid_body
        │
        ▼
 ② Validadores (Application)   vacío, demasiado largo, formato   → 400  validation.failed   + lista por campo
        │
        ▼
 ③ Reglas del negocio          estado imposible, regla de entidad → 400  job.cannot_complete, ...
   (Domain y handlers)         el recurso no existe              → 404  customer.not_found
```

La barrera ② protege **la entrada**: da un error claro y barato, antes de usar el dato. La ③ protege **la integridad**: no se fía de nadie, porque `Job.Create` podría llamarse desde un sitio que no pasó por un validador (capítulo 4, paso 12).

Además hay dos errores que no son "datos incorrectos": `403` (tu rol no puede, capítulo 4) y `500` (un fallo inesperado).

---

## Parte 1 — Los códigos de error

Cada error tiene un **código estable**, como `job.cannot_complete`. Es lo que el cliente usa para reaccionar (resaltar un campo, mostrar una ayuda). El texto para la persona va aparte y puede cambiar.

### El formato

`entidad.error`, en minúsculas y `snake_case`: `customer.name_required`, `tenant_user.not_found`, `common.email_invalid`. El prefijo es la **entidad a la que se refiere el error**, y `common` para lo compartido (como `Email`). Un test impone el formato.

### Dónde viven

Cada contexto tiene una clase de constantes con sus códigos:

| Clase | Códigos | Ejemplos |
|---|---|---|
| [`CommonErrors`](../../src/OfiFlow.Domain/Common/CommonErrors.cs) | 2 | `common.email_invalid` |
| [`CustomerErrors`](../../src/OfiFlow.Domain/Customers/CustomerErrors.cs) | 5 | `customer.not_found`, `customer.has_active_jobs` |
| [`JobErrors`](../../src/OfiFlow.Domain/Jobs/JobErrors.cs) | 5 | `job.cannot_complete`, `job.not_found` |
| [`TenancyErrors`](../../src/OfiFlow.Domain/Tenancy/TenancyErrors.cs) | 5 | `tenant_user.not_found`, `tenant_user.role_not_manageable`, `auth.forbidden` |
| [`InvitationErrors`](../../src/OfiFlow.Domain/Tenancy/InvitationErrors.cs) | 4 | `invitation.invalid`, `invitation.already_pending` |
| [`IdentityErrors`](../../src/OfiFlow.Domain/Identity/IdentityErrors.cs) | 2 | `user.email_already_registered` |
| [`MfaErrors`](../../src/OfiFlow.Domain/Identity/MfaErrors.cs) | 5 | `mfa.already_enabled`, `mfa.invalid_code` (spec 010; todavía ninguna operación los lanza) |
| [`ApiErrors`](../../src/OfiFlow.Api/Common/ApiErrors.cs) (en la API) | 4 | `validation.failed`, `request.invalid_body`, `rate_limit.exceeded`, `server.unexpected` |

En total **32 códigos**. Los de la API están aparte porque no pertenecen a ningún contexto de negocio: son de la propia petición HTTP.

```csharp
// Domain/Jobs/JobErrors.cs
public const string CannotComplete = "job.cannot_complete";
```

Los códigos son **contrato público**: una vez que un cliente depende de uno, no se cambia ni se reutiliza con otro significado.

---

## Parte 2 — Las excepciones

Un error se *lanza* como una excepción y se *traduce* en un único sitio (parte 4). Cada tipo expresa una causa distinta:

| Excepción | Quién la lanza | Qué significa | HTTP |
|---|---|---|---|
| `ValidationException` (de FluentValidation) | El `ValidationBehavior` | Algún campo no es válido | 400 |
| [`DomainException`](../../src/OfiFlow.Domain/Common/DomainException.cs) | Las entidades de Domain | Una regla **de una entidad** lo impide | 400 |
| `BusinessRuleException` | Los handlers | Una regla **entre varias entidades** lo impide | 400 |
| `NotFoundException` | Los handlers | El recurso no existe (o es de otra empresa) | 404 |
| `ForbiddenException` | El `AuthorizationBehavior` | Tu rol no puede | 403 |
| `IdentityOperationException` | El registro | Fallo al crear las credenciales | 400 |

Las cinco últimas llevan un **código**, no un texto: `new NotFoundException(CustomerErrors.NotFound, id)`.

### La regla que distingue un error de un bug

Una excepción de **negocio** es siempre una de las propias de arriba. Cualquier **otra** (`InvalidOperationException`, `NullReferenceException`, un fallo de la base de datos...) se considera un **bug** y acaba en un **500**.

Esto importa más de lo que parece. Antes, Domain lanzaba `InvalidOperationException` para las transiciones de estado inválidas, y los handlers la capturaban y la convertían en 400. El problema: un fallo real del sistema que lanzara ese mismo tipo se habría convertido, sin querer, en un "error de negocio" bonito, escondiendo un bug. Hoy un test impide que Domain use `ArgumentException` o `InvalidOperationException` para errores de negocio.

### Los argumentos, sin datos personales

`DomainException(code, params object?[] arguments)` admite argumentos para el mensaje. Regla de seguridad: **solo datos no personales** (un estado, un Id), nunca lo que escribió el usuario. Un email es un dato personal que podría acabar en un registro:

```csharp
// Domain/Common/Email.cs
// Solo el código, nunca el valor: es un dato personal y podría acabar en un log.
throw new DomainException(CommonErrors.EmailInvalid);
```

En términos de seguridad, esto evita el **CWE-209** (*mensajes de error que revelan información sensible*) y el "Information disclosure" de STRIDE.

---

## Parte 3 — La validación de entrada

Cada operación puede tener un **validador**: una clase con las reglas de sus campos, escrita con la librería FluentValidation. Por ejemplo, el de crear un cliente ([`CreateCustomerCommandValidator`](../../src/OfiFlow.Application/Customers/Commands/CreateCustomer/CreateCustomerCommandValidator.cs)):

```csharp
RuleFor(x => x.Type).IsInEnum();
RuleFor(x => x.Name).NotEmpty().MaximumLength(Customer.NameMaxLength);

RuleFor(x => x.Email)
    .ValidEmail()
    .When(x => !string.IsNullOrWhiteSpace(x.Email));      // el email es opcional
```

Se leen casi como una frase: "el nombre no puede estar vacío y tiene un máximo". El [`ValidationBehavior`](../../src/OfiFlow.Application/Common/Behaviors/ValidationBehavior.cs) los ejecuta **todos** antes del handler y reúne **todos** los fallos (no solo el primero), de modo que el cliente ve de golpe todo lo que tiene que corregir (líneas 26-32).

### Sin repetir números ni reglas

Dos trucos para no tener la misma regla escrita en dos sitios y que acaben desincronizadas:

**1. Una sola constante para la longitud máxima.** `Customer.NameMaxLength` (200) la usa el validador **y** la configuración de la base de datos. Si la longitud cambiara en un sitio y no en el otro, un nombre largo pasaría el validador y reventaría al guardar con un 500. Con una constante, no puede pasar.

**2. Una sola regla para el formato del email.** `Email.IsValid` está en Domain, y el validador la reutiliza a través de [`ValidationRules.ValidEmail`](../../src/OfiFlow.Application/Common/Validation/ValidationRules.cs). Así, lo que el validador rechaza con un 400 es exactamente lo que Domain rechazaría después. Además, si el texto ya es demasiado largo, no se evalúa el formato: se da un solo error y la expresión regular nunca recorre una entrada sin límite.

### Los códigos de las reglas

- Las reglas **propias** (`ValidEmail`, `ValidPhone`) llevan un código de OfiFlow (`common.email_invalid`) con su mensaje en el diccionario.
- Las reglas **estándar** de FluentValidation (`NotEmpty`, `MaximumLength`) conservan los suyos (`NotEmptyValidator`, `MaximumLengthValidator`) y sus mensajes, que FluentValidation ya traduce.

---

## Parte 4 — El traductor: de excepción a respuesta

Todo error acaba en un único sitio, el [`GlobalExceptionHandler`](../../src/OfiFlow.Api/Common/GlobalExceptionHandler.cs) (recuerda del capítulo 4, paso 3: envuelve toda la petición). Su método `Describe` (líneas 62-78) decide qué respuesta corresponde a cada tipo:

| Excepción | HTTP | Título (clave del diccionario) | `code` |
|---|---|---|---|
| `ValidationException` | 400 | `problem.title.validation` | `validation.failed` |
| `DomainException`, `BusinessRuleException` | 400 | `problem.title.business_rule` | el de la excepción |
| `IdentityOperationException` | 400 | `problem.title.registration` | el primero de sus códigos |
| `ForbiddenException` | 403 | `problem.title.forbidden` | `auth.forbidden` |
| `NotFoundException` | 404 | `problem.title.not_found` | el de la excepción |
| `BadHttpRequestException` (JSON roto) | 400 | `problem.title.bad_request` | `request.invalid_body` |
| **Cualquier otra** | **500** | `problem.title.server_error` | `server.unexpected` |

### La forma de la respuesta

Todas siguen el estándar **RFC 9457** (*Problem Details*), con campos estables añadidos. Un error de negocio:

```json
{
  "status": 400,
  "title": "Regla de negocio violada",
  "detail": "Solo se puede completar un trabajo que está en curso.",
  "code": "job.cannot_complete",
  "traceId": "00-4bf92f35..."
}
```

Y uno de validación añade la lista por campo:

```json
{
  "status": 400,
  "title": "Error de validación",
  "detail": "La petición contiene errores. Revisa el detalle de cada campo.",
  "code": "validation.failed",
  "errors": [
    { "field": "Title", "code": "NotEmptyValidator", "message": "..." }
  ],
  "traceId": "00-4bf92f35..."
}
```

| Campo | Para quién | ¿Estable? |
|---|---|---|
| `code`, `errors[].code` | Programas (el cliente) | **Sí**: contrato público |
| `title`, `detail`, `message` | Personas | No: pueden cambiar de redacción |
| `traceId` | Quien depure | Cambia en cada petición |

El `traceId` es el hilo que une la respuesta con el registro del servidor: si Marta reporta un error, con ese identificador se encuentra la entrada exacta del log.

### Los 500: nunca se cuenta nada

Para un error inesperado, el cliente recibe solo el mensaje genérico ("Ha ocurrido un error inesperado") y el `traceId`. La excepción completa, con su detalle interno, se guarda en el log (`LogUnhandledException`, línea 39) y **nunca sale al exterior**. Un test lo comprueba: lanza una excepción con un texto interno reconocible y exige que no aparezca en la respuesta, y que las cabeceras de seguridad sigan presentes.

---

## Parte 5 — El diccionario de mensajes y el idioma

Los textos para las personas **no están en el código**: están en un fichero, [`ErrorMessages.resx`](../../src/OfiFlow.Api/Resources/ErrorMessages.resx), en la capa API. Es un diccionario: la **clave** es el código y el **valor** es el mensaje.

```xml
<data name="job.cannot_complete">
  <value>Solo se puede completar un trabajo que está en curso.</value>
</data>
```

Hoy tiene **40 entradas**: los 32 códigos más los 8 títulos de las respuestas. El `GlobalExceptionHandler` busca el mensaje con `IStringLocalizer`, el mecanismo estándar de .NET. Si por algún motivo faltase una entrada, devuelve el propio código en vez de lanzar una excepción (un error al explicar otro error sería peor); y un test impide que eso llegue a producción.

### El idioma

El idioma lo elige la cabecera `Accept-Language` de la petición, **pero con una lista cerrada**: hoy solo existe `es`, y cualquier otro valor (`en-US`, `fr-FR`...) cae al español ([`Localization.cs`](../../src/OfiFlow.Api/Common/Localization.cs)). Dos razones:

- **Menos superficie de entrada:** se descartan otras formas de elegir idioma (`?culture=` en la URL, cookies) que no hacen falta.
- **Mismo resultado en cualquier servidor:** sin esto, los mensajes estándar de FluentValidation saldrían en el idioma del sistema operativo de la máquina: español en tu equipo, inglés en un servidor de GitHub. Ahora siempre salen en español.

**Añadir un idioma será crear un fichero** (`ErrorMessages.en.resx`) y una línea en la lista de idiomas, sin tocar Domain ni Application. Es la fase 11 del roadmap; hoy no se construye (diseñar para escalar, construir para la realidad actual).

---

## Parte 6 — Los guardarraíles

Un sistema de códigos y un diccionario se descuidan con facilidad: alguien añade un código y se olvida del mensaje. Por eso hay tests que lo impiden (en `OfiFlow.Api.Tests`):

| Test | Qué impide |
|---|---|
| `EveryErrorCode_HasAMessageInTheDictionary` | Un código sin mensaje (recorre por reflexión todas las constantes de las clases `*Errors`) |
| `EveryDictionaryEntry_BelongsToAKnownCode` | Una entrada huérfana en el diccionario, que ya no corresponde a nada |
| `EveryErrorCode_FollowsTheNamingConvention` | Un código que no es `entidad.error` en `snake_case` |
| `Domain_DoesNotThrowGenericExceptionsForBusinessRules` | Que Domain use excepciones genéricas de .NET para errores de negocio |
| `ExceptionMappingContractTests` y `ValidationErrorContractTests` | Que cada tipo de excepción dé su estado, su código y su mensaje del diccionario, y que la validación real devuelva el error por campo en español |
| `UnhandledException_Returns500WithoutInternalDetails_AndLogsIt` | Que un 500 filtre detalles internos |
| `MalformedJson_Returns400_WithoutLeakingInternalTypeNames` | Que un JSON roto filtre nombres de tipos internos |

Fíjate en la idea de fondo: los códigos y el diccionario son un **contrato**, y un contrato se vigila con tests, no con memoria.

## Receta: añadir un error nuevo

Supón que quieres rechazar un trabajo con un título duplicado. Los pasos, en orden:

1. **Código:** añade la constante a la clase del contexto, `JobErrors.TitleDuplicated = "job.title_duplicated"`. Si falta algún argumento, documenta cuál es.
2. **Lanzarlo:** en la entidad o el handler, `throw new DomainException(JobErrors.TitleDuplicated)` (o `BusinessRuleException` si la regla cruza varias entidades).
3. **Mensaje:** añade la entrada `job.title_duplicated` a `ErrorMessages.resx`, en español.
4. **Tests:** uno que provoque el error y compruebe el código; el de "diccionario completo" ya fallará si te olvidaste del paso 3.
5. **Nada más.** El `GlobalExceptionHandler` ya sabe traducir cualquier `DomainException`: no hay que tocarlo.

Es un buen ejemplo de por qué se diseñó así: añadir un error toca la entidad, el diccionario y un test, y ningún otro sitio.

---

## Dónde están hoy los límites

**1. El nombre de los campos en `errors[].field` va en `PascalCase`** (`Title`, `Email`), tal como se llaman en el código, mientras que el JSON de la petición va en `camelCase` (`title`, `email`). Un cliente tiene que hacer esa correspondencia. Es una pequeña incoherencia del contrato.

**2. Los códigos de las reglas estándar dependen de una librería.** `NotEmptyValidator` y `MaximumLengthValidator` son nombres internos de FluentValidation. El ADR los declara contrato público, pero si la librería los renombrara en una versión futura, el contrato cambiaría sin que nadie lo decidiera. Mitigación posible: un test que fije esos nombres.

**3. Ningún mensaje usa todavía los argumentos.** `DomainException` admite argumentos y los marcadores `{0}` del diccionario existen, pero hoy ningún mensaje los usa (por ejemplo, `job.cannot_complete` recibe el estado actual del trabajo, pero el texto no lo menciona). El mecanismo está preparado, sin estrenar.

**4. Solo existe el español.** La infraestructura para más idiomas está lista, pero no hay ningún otro fichero.

**5. Los mensajes de error de la base de datos podrían llevar datos personales al log.** Un fallo al guardar (por ejemplo, violar una restricción) genera una excepción cuyo texto puede incluir valores. Se registra con todo su detalle en el servidor (nunca sale al cliente). Está anotado como pendiente de revisar.

**6. Los errores del propio ASP.NET no siguen este formato.** El `401` lo genera la autenticación y no lleva `code` (capítulo 4). El `429` del límite de peticiones sí lleva `code` (`rate_limit.exceeded`) y la cabecera `Retry-After`, pero lo escribe el propio limitador, no el `GlobalExceptionHandler`.

**7. Todos los errores de negocio son 400.** No se distingue 409 (conflicto) ni 422 (entidad no procesable). Es una decisión de simplicidad: el `code` ya dice qué pasó con más precisión que el número.

**8. Domain se detiene en el primer error.** Los validadores informan de todos los campos a la vez, pero una `DomainException` se lanza en cuanto se rompe la primera regla: el cliente se entera de los errores de negocio de uno en uno.

## Para comprobar que lo has entendido

1. ¿Por qué un error lleva un código y no un texto? Da dos razones.
2. ¿Qué diferencia hay entre el validador de un campo y una regla de `Domain`, y por qué se hacen las dos cosas?
3. ¿Qué diferencia hay entre `DomainException` y `BusinessRuleException`? Pon un ejemplo de cada una.
4. ¿Qué pasa si una operación lanza una `InvalidOperationException`, y por qué se trata así?
5. ¿Por qué la longitud máxima del nombre de un cliente es una constante compartida entre el validador y la base de datos?
6. Un cliente envía `Accept-Language: fr-FR`. ¿En qué idioma recibe el error y por qué?
7. Quieres añadir el error "teléfono duplicado". ¿Qué tocas y qué test te avisa si te olvidas de algo?

*(Respuestas: 1 = para poder traducir sin tocar el código, y para que el cliente reconozca el error sin interpretar frases que pueden cambiar; también para no meter datos personales en los textos. 2 = el validador protege la entrada (da un 400 claro y barato antes de usar el dato); Domain protege su integridad y no se fía de nadie, porque podría llamarse sin pasar por un validador. 3 = `DomainException`: una regla dentro de una sola entidad (completar un trabajo que no está en curso); `BusinessRuleException`: una regla entre varias (borrar un cliente con trabajos activos). 4 = acaba en un 500 con mensaje genérico y el detalle va al log, porque una excepción genérica indica un bug y no debe disfrazarse de error de negocio. 5 = para que no puedan desincronizarse: si el validador aceptara más de lo que cabe en la base de datos, un nombre largo daría un 500 al guardar en vez de un 400. 6 = en español: la lista de idiomas es cerrada y cualquier otro cae al idioma por defecto, así el resultado no depende del servidor ni de lo que pida el cliente. 7 = una constante en `CustomerErrors`, el `throw` en la entidad o el handler, la entrada en `ErrorMessages.resx` y un test que provoque el error; si te olvidas del diccionario falla `EveryErrorCode_HasAMessageInTheDictionary`.)*

## Siguiente capítulo

El capítulo 8 explica la **seguridad** de la API en conjunto: las cabeceras, el límite de peticiones, los permisos por rol, los guardarraíles automáticos y cómo encajan unas capas con otras.
