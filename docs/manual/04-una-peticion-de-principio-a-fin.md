# Capítulo 4 — Una petición de principio a fin

## Qué vas a aprender

- El recorrido completo de una petición real, desde que llega por la red hasta que se guarda en la base de datos.
- En qué capa, fichero y línea ocurre cada paso.
- Qué pasa cuando algo falla, y en qué punto se detiene cada tipo de error.
- Qué NO hace hoy esta petición (para no hacerte una idea equivocada).

Los capítulos anteriores explicaron las piezas por separado. Este las junta: si entiendes este capítulo, entiendes cómo funciona OfiFlow.

## El caso

Marta ya tiene cuenta, ha iniciado sesión y tiene un cliente creado (Juan). Ahora quiere registrar un trabajo para él. Su aplicación envía esto:

```http
POST /api/v1/jobs HTTP/1.1
Host: api.ofiflow.example
Authorization: Bearer <token que Marta recibió al iniciar sesión>
Accept-Language: es
Content-Type: application/json

{
  "customerId": "3f2b8c1e-...-a91d",
  "title": "Reparar fuga bajo el fregadero",
  "description": "Gotea desde ayer",
  "priority": 3
}
```

Dos observaciones sobre esta petición:

- **No dice en qué empresa trabaja Marta.** Eso lo sabe el servidor por el token. Lo verás en los pasos 11 y 12.
- **`priority` es un número**, no un texto: la API no tiene configurado el formato de texto para enumerados, así que espera el valor numérico de `JobPriority` (`Low` = 0, `Normal` = 1, `High` = 2, `Urgent` = 3). Si enviaras `"Urgent"` entre comillas, el JSON no se podría convertir y recibirías un 400. Curiosamente, las *respuestas* sí devuelven el estado y la prioridad como texto (`"Urgent"`), porque `JobDto` los convierte con `ToString()`. Es una asimetría a tener en cuenta al construir un cliente.

## Vista aérea

```
   Marta (cliente HTTP)
          │
          ▼
┌──────────────────────────── API (capa Api) ────────────────────────────┐
│ 1  Cabeceras de seguridad (se enganchan para la salida)                │
│ 2  Idioma de la petición                                               │
│ 3  Red de seguridad para errores (ExceptionHandler)                    │
│ 4  HTTPS · 5 IP al log · 6 Rate limiter                                │
│ 7  Autenticación: ¿el token es válido?  →  8 Autorización: ¿hay sesión?│
│ 9  Endpoint: lee el JSON y lo convierte en un Command                  │
└─────────────────────────────────┬──────────────────────────────────────┘
                                  ▼
┌────────────────────── APPLICATION (capa Application) ──────────────────┐
│ 10 ValidationBehavior: ¿los datos son válidos?                         │
│ 11 Handler: ¿existe ese cliente en MI empresa?                         │
│ 12 Pide a Domain que cree el trabajo                                   │
└──────────────┬──────────────────────────────────────┬──────────────────┘
               ▼                                      ▼
┌──── DOMAIN ─────────────┐        ┌────────── INFRASTRUCTURE ───────────┐
│ Job.Create(): aplica    │        │ 11b  Filtro por tenant en la consulta│
│ sus reglas y crea el    │        │ 13   Guardar: INSERT en SQL Server   │
│ objeto válido           │        │      (con CreatedAt automático)      │
└─────────────────────────┘        └──────────────────────────────────────┘
                                  │
                                  ▼
                    14 Respuesta 201 Created, con las cabeceras de seguridad
```

Son 14 pasos. Vamos uno a uno.

---

## Parte 1 — La puerta (capa Api)

Todo lo que sigue está en [`Program.cs`](../../src/OfiFlow.Api/Program.cs), líneas 33 a 52. Una petición HTTP en ASP.NET Core atraviesa una cadena de **middlewares** (piezas por las que pasa en orden), y el **orden es una decisión de diseño**.

### Paso 1 — Cabeceras de seguridad (`Program.cs:33`)

`SecurityHeadersMiddleware` no escribe nada todavía. Solo **se apunta una tarea** para el último instante: "justo antes de enviar la respuesta, añade estas cabeceras". Va el primero para cubrir **todas** las respuestas, incluidos los errores (capítulo de seguridad).

### Paso 2 — Idioma (`Program.cs:34`)

Lee `Accept-Language: es` y fija el idioma de la petición. Va antes del manejador de errores para que los mensajes de error salgan en el idioma correcto. Solo se acepta la cabecera, no `?culture=` ni cookies: menos superficie de entrada.

### Paso 3 — La red de seguridad (`Program.cs:35`)

`UseExceptionHandler()` envuelve **todo lo que viene después** en un gran `try/catch`. Si cualquier paso lanza una excepción (validación fallida, cliente no encontrado, un fallo inesperado), cae aquí y [`GlobalExceptionHandler`](../../src/OfiFlow.Api/Common/GlobalExceptionHandler.cs) la convierte en una respuesta de error ordenada. Por eso, más adelante, los handlers pueden simplemente **lanzar excepciones** sin preocuparse de construir respuestas HTTP.

### Pasos 4 a 6 — HTTPS, IP al log y rate limiter (`Program.cs:42-45`)

- **HTTPS:** si la petición llegó por HTTP, se redirige a HTTPS.
- **IP al log:** `RequestLogScopeMiddleware` apunta la IP de origen en el contexto de logging, para que cualquier evento de seguridad que ocurra después la incluya sin tener que pasarla de mano en mano.
- **Rate limiter:** limita las peticiones por IP. **Pero solo en las rutas que lo piden**, y hoy solo lo piden las de `auth` (registro, login, renovación). Las rutas de trabajos y clientes **no tienen límite de peticiones**. Es una limitación conocida; la comentamos al final.

### Paso 7 — Autenticación (`Program.cs:47`)

Aquí se responde "¿**quién eres**?". El middleware de JWT mira la cabecera `Authorization`, extrae el token y comprueba:

| Qué se comprueba | Qué evita |
|---|---|
| La **firma**, con el secreto del servidor | Un token falsificado o modificado |
| El **emisor** y el **destinatario** | Un token emitido para otra aplicación |
| La **caducidad**, sin margen de tolerancia | Un token viejo reutilizado |

(Configurado en [`DependencyInjection.cs:57-68`](../../src/OfiFlow.Infrastructure/DependencyInjection.cs) de Infrastructure.)

Si todo es correcto, el servidor construye un objeto de usuario con los datos del token (sus *claims*): quién es y, **lo importante, en qué empresa está operando** (`tenant_id`).

### Paso 8 — Autorización (`Program.cs:48`)

Aquí se responde "¿**puedes pasar**?". El grupo de rutas de trabajos se declaró con `RequireAuthorization()` ([`JobEndpoints.cs:21`](../../src/OfiFlow.Api/Endpoints/JobEndpoints.cs)), lo que significa: **sin sesión válida, no se pasa**. Si Marta no hubiese enviado token (o estuviese caducado), la petición terminaría aquí con un **401**, y **nunca llegaría al código de la aplicación**.

> Hoy esta comprobación solo mira "¿hay sesión?". **No mira el rol**: un `Employee` puede hacer lo mismo que un `Owner`. El control de permisos por rol está pendiente (capítulo 2).

### Paso 9 — El endpoint (`JobEndpoints.cs:23-27`)

Por fin llega al código que gestiona esta ruta:

```csharp
group.MapPost("/", async (CreateJobCommand command, ISender sender, CancellationToken cancellationToken) =>
    {
        var id = await sender.Send(command, cancellationToken);
        return Results.Created($"{Route}/{id}", new { id });
    })
```

ASP.NET hace dos cosas **antes** de ejecutar el cuerpo:

1. **Convierte el JSON en un objeto `CreateJobCommand`.** Si el JSON está mal formado, o un campo trae un tipo incorrecto (por ejemplo `priority` como texto), falla aquí con un error 400 genérico, sin revelar detalles internos. Un número fuera de rango (`"priority": 99`) sí se convierte sin problema; lo detiene el validador del paso 10.
2. **Entrega al endpoint un `ISender`** (el "cartero" de MediatR).

Fíjate en lo que el comando **no tiene**:

```csharp
// Application/Jobs/Commands/CreateJob/CreateJobCommand.cs:6
public sealed record CreateJobCommand(Guid CustomerId, string Title, string? Description, JobPriority Priority)
```

No tiene `TenantId`. Aunque un atacante añadiese `"tenantId": "<otra empresa>"` al JSON, **no hay dónde guardarlo** y se ignora. Un test de arquitectura impide que nadie añada esa propiedad. Esto es protección contra *mass assignment*.

El endpoint no decide nada: solo entrega el comando y espera. La línea `sender.Send(command)` es el paso a la capa Application.

---

## Parte 2 — El caso de uso (capa Application)

### Paso 10 — Validación (`ValidationBehavior.cs:26-32`)

Antes de que el comando llegue a su handler, MediatR lo hace pasar por los *behaviors*. El único que existe hoy ejecuta los **validadores**: [`CreateJobCommandValidator`](../../src/OfiFlow.Application/Jobs/Commands/CreateJob/CreateJobCommandValidator.cs), líneas 10 a 13:

| Campo | Regla |
|---|---|
| `CustomerId` | No puede estar vacío |
| `Title` | No puede estar vacío (ni solo espacios) y máximo 200 caracteres (`Job.TitleMaxLength`) |
| `Description` | Máximo 2.000 caracteres |
| `Priority` | Debe ser un valor válido del enumerado |

Si algo falla, **se lanza una excepción** y el handler **ni siquiera se ejecuta**. Esto cumple dos objetivos: rechazar con 400 una entrada incorrecta (en vez de que acabe en un error 500 al llegar a la base de datos) y no gastar recursos en datos que no valen.

### Paso 11 — El handler comprueba que el cliente existe (`CreateJobCommandHandler.cs:16-20`)

```csharp
var customerExists = await dbContext.Customers.AnyAsync(c => c.Id == request.CustomerId, cancellationToken);
if (!customerExists)
{
    throw new NotFoundException(CustomerErrors.NotFound, request.CustomerId);
}
```

Aquí ocurre lo más importante del proyecto, aunque en el código no se vea. La consulta dice "¿existe un cliente con este `Id`?". Pero **no dice nada de la empresa**. Y sin embargo, solo busca entre los clientes de Marta. ¿Por qué?

#### Paso 11b — El filtro automático por tenant (capa Infrastructure)

Porque `Customer` lleva la etiqueta `ITenantOwned`, y al arrancar, [`ApplicationDbContext.cs:63`](../../src/OfiFlow.Infrastructure/Persistence/ApplicationDbContext.cs) añadió a **toda** entidad con esa etiqueta una condición permanente:

```csharp
modelBuilder.Entity<TEntity>().HasQueryFilter(entity => entity.TenantId == tenantContext.TenantId);
```

`tenantContext.TenantId` sale de [`TenantContext.cs:17-20`](../../src/OfiFlow.Infrastructure/Tenancy/TenantContext.cs): lee el claim `tenant_id` **del token validado en el paso 7**. Es decir: la empresa no la elige el cliente en el JSON, la dice el token, que está firmado.

El SQL que genera EF Core es, aproximadamente:

```sql
SELECT CASE WHEN EXISTS (
    SELECT 1 FROM Customers
    WHERE TenantId = @tenant      -- ← añadido automáticamente por el filtro
      AND Id = @customerId        -- ← lo que pedía el handler
) THEN 1 ELSE 0 END
```

Con dos efectos importantes:

- **Ningún handler puede olvidarse de filtrar por empresa**, porque no lo hace él: lo hace el sistema.
- Si Marta envía el `Id` de un cliente de **otra empresa**, la consulta no lo encuentra, y recibe el mismo **404** que si no existiera. Esto es deliberado: no se revela que ese cliente existe. El código del error es `customer.not_found` en ambos casos.

Los valores `@tenant` y `@customerId` viajan como **parámetros**, no como texto concatenado, así que no hay posibilidad de inyección SQL.

### Paso 12 — El handler le pide a Domain que cree el trabajo (`CreateJobCommandHandler.cs:22`)

```csharp
var job = Job.Create(tenantContext.TenantId, request.CustomerId, request.Title, request.Description, request.Priority);
```

El `TenantId` que se le pasa **no viene del comando**: viene de `tenantContext`, es decir, del token. Es el único sitio donde se asigna la empresa de un trabajo nuevo, y es el servidor quien decide.

Ahora entra la capa Domain. [`Job.Create`](../../src/OfiFlow.Domain/Jobs/Job.cs) (líneas 49-54):

```csharp
public static Job Create(Guid tenantId, Guid customerId, string title, string? description, JobPriority priority)
{
    ValidateTitle(title);                                                       // regla de negocio

    return new Job(Guid.NewGuid(), tenantId, customerId, title.Trim(), description, priority);
}
```

- **Vuelve a comprobar el título**, aunque el validador ya lo hizo. Es la diferencia entre las dos capas: el validador protege la *entrada* (y da un 400 bonito); `Domain` protege la *integridad* y no se fía de nadie, porque mañana alguien podría llamar a `Job.Create` desde otro sitio sin pasar por el validador.
- **Genera el `Id`** en el servidor (un `Guid` nuevo); el cliente no lo elige.
- **Fija el estado inicial a `New`.** El cliente no puede crear un trabajo ya "completado".
- **Quita los espacios sobrantes** del título.

Si el título fuese inválido, se lanzaría una `DomainException` con el código `job.title_required`.

---

## Parte 3 — Guardar (capa Infrastructure)

### Paso 13 — `SaveChangesAsync` (`CreateJobCommandHandler.cs:24-25`)

```csharp
dbContext.Jobs.Add(job);
await dbContext.SaveChangesAsync(cancellationToken);
```

`Add` solo anota "este objeto es nuevo". Es `SaveChangesAsync` quien escribe. Justo antes, ocurre algo sin que el handler lo pida:

- **El interceptor de auditoría** ([`AuditableEntitySaveChangesInterceptor.cs:41-42`](../../src/OfiFlow.Infrastructure/Persistence/Interceptors/AuditableEntitySaveChangesInterceptor.cs)) ve que `Job` es `IAuditable` y está en estado `Added`, y rellena `CreatedAt` con la hora actual (UTC). Ningún handler toca esos campos.
- **La configuración de la tabla** ([`JobConfiguration.cs`](../../src/OfiFlow.Infrastructure/Persistence/Configurations/JobConfiguration.cs)) define cómo se guarda cada campo: título obligatorio con longitud máxima, `Status` y `Priority` guardados como texto.

EF Core genera entonces un `INSERT` parametrizado y lo ejecuta contra SQL Server. Es una sola operación, por lo que se guarda completa o no se guarda nada.

El handler devuelve el `Id` del trabajo nuevo.

---

## Parte 4 — La respuesta

### Paso 14 — 201 Created (`JobEndpoints.cs:26`)

El endpoint recibe el `Id` y responde:

```http
HTTP/1.1 201 Created
Location: /api/v1/jobs/8d1f...-c3a2
Content-Type: application/json
X-Content-Type-Options: nosniff
X-Frame-Options: DENY
Referrer-Policy: no-referrer
Content-Security-Policy: default-src 'none'; frame-ancestors 'none'
Cross-Origin-Resource-Policy: same-origin

{ "id": "8d1f...-c3a2" }
```

- `201 Created` y la cabecera `Location` indican dónde está el recurso nuevo.
- Las **cinco cabeceras de seguridad** se añaden aquí, en el último instante: es la tarea que el paso 1 se había apuntado.
- Solo se devuelve el `Id`, no el trabajo entero. Si el cliente quiere verlo, pide `GET /api/v1/jobs/{id}`.

---

## Cuando algo falla: dónde se detiene cada error

Casi todo lo que sale mal se convierte en una **excepción** que cae en la red de seguridad del paso 3. Dependiendo de dónde ocurra, se corta en un punto distinto:

| Qué pasa | Dónde se detiene | Respuesta | `code` |
|---|---|---|---|
| Sin token, o token inválido o caducado | Paso 7-8 (autenticación) | **401** | *(lo genera la autenticación de ASP.NET, no nuestro manejador, así que no lleva el formato con `code`)* |
| JSON mal formado, o un campo con tipo incorrecto (p. ej. `priority` como texto) | Paso 9 (lectura del cuerpo) | **400** | `request.invalid_body` |
| Título vacío o demasiado largo, `customerId` vacío, `priority` fuera de rango... | Paso 10 (validación) | **400** | `validation.failed`, con una lista `errors` por campo |
| El cliente no existe, **o es de otra empresa** | Paso 11 (handler) | **404** | `customer.not_found` |
| Una regla de Domain se rompe (título inválido que se colara) | Paso 12 (`Job.Create`) | **400** | `job.title_required` |
| Cualquier fallo inesperado (la base de datos no responde...) | Cualquier paso | **500** | `server.unexpected` |

En todos los casos de error de nuestra capa, la respuesta tiene la forma estándar RFC 9457 (`ProblemDetails`), con un `code` estable, un texto en el idioma de la petición y un `traceId` para buscar el detalle en el log. Por ejemplo:

```json
{
  "status": 404,
  "title": "Recurso no encontrado",
  "detail": "No se ha encontrado el cliente.",
  "code": "customer.not_found",
  "traceId": "00-4bf92f35..."
}
```

Para los **500**, el cliente solo recibe un mensaje genérico. La excepción completa, con su detalle, se guarda en el log del servidor y nunca sale al exterior.

---

## Qué hace esta petición y qué no

Para no llevarte una idea más optimista de la real:

- ✅ **Aísla por empresa**, sin que ningún handler tenga que acordarse.
- ✅ **Valida** la entrada antes de usarla y vuelve a comprobar en Domain.
- ✅ **Responde siempre con el mismo formato** de error y sin filtrar detalles internos.
- ⚠️ **No tiene límite de peticiones**: solo lo tiene `auth`. Alguien con un token válido podría crear trabajos sin freno.
- ⚠️ **No comprueba el rol**: cualquier miembro de la empresa puede crear trabajos.
- ⚠️ **Hace dos viajes a la base de datos** (comprobar el cliente y guardar el trabajo). Es suficiente para el tamaño actual; si hiciera falta, se optimizaría con datos reales.
- ⚠️ **No publica eventos de dominio**: nadie se entera de que se creó un trabajo salvo quien lo consulte después.
- ⚠️ **No registra la creación en un log de auditoría** (quién creó qué y cuándo): solo queda `CreatedAt`. El log de auditoría persistente está planificado para más adelante.

## El resumen por capas

| Capa | Pasos | Su trabajo en esta petición |
|---|---|---|
| **Api** | 1-9, 14 | Seguridad de entrada, saber quién eres, traducir HTTP a un comando y la respuesta de vuelta |
| **Application** | 10-12 | Validar, comprobar que el cliente existe y orquestar |
| **Domain** | 12 | Crear un trabajo siempre válido |
| **Infrastructure** | 7, 11b, 13 | Validar el token, filtrar por empresa, guardar |

Observa que Infrastructure aparece **tres veces repartidas por todo el recorrido**, a pesar de que "está al fondo": el token (paso 7), el filtro (11b) y el guardado (13). Es lo que significa que la capa **implementa los contratos** que otras capas declararon: no se la llama directamente, se la usa a través de interfaces.

## Para comprobar que lo has entendido

1. ¿Por qué Marta no envía su `tenantId` en el JSON, y de dónde lo saca el servidor?
2. Un atacante añade `"tenantId": "<otra empresa>"` al JSON. ¿Qué pasa y por qué?
3. Marta envía el `customerId` de un cliente de otra empresa. ¿Qué respuesta recibe y por qué es la misma que si no existiera?
4. ¿Qué diferencia hay entre el validador del paso 10 y la comprobación del título en `Job.Create` (paso 12)? ¿Por qué hacerlo dos veces?
5. ¿Por qué un handler puede lanzar una excepción en vez de devolver un error HTTP?
6. Nombra dos cosas que esta petición no controla todavía.

*(Respuestas: 1 = el tenant va en el token firmado, no lo decide el cliente; el servidor lo lee del claim `tenant_id` validado en el paso 7. 2 = no pasa nada: el comando no tiene propiedad `TenantId` y el JSON sobrante se ignora; el tenant lo fija el servidor. 3 = un 404 `customer.not_found`; el filtro automático por tenant hace que esa consulta no vea clientes ajenos, y se devuelve lo mismo para no revelar que existe. 4 = el validador protege la entrada y da un 400 claro; Domain protege su integridad y no confía en nadie, porque podría llamarse sin pasar por el validador. 5 = porque el `ExceptionHandler` del paso 3 envuelve todo y las traduce a respuestas. 6 = rate limiting en estas rutas, control de rol, eventos de dominio, log de auditoría.)*

## Siguiente capítulo

El capítulo 5 profundiza en lo que hemos visto en el paso 11: **cómo se aísla cada empresa** y cómo sabemos que de verdad funciona (el test `TenantIsolationIntegrationTests`).
