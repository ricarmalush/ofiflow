# Capítulo 3 — Las capas y sus carpetas

## Qué vas a aprender

- Qué contiene cada una de las cuatro capas, carpeta por carpeta.
- Por qué existe cada capa y qué problema resolvería su ausencia.
- Qué **sí** y qué **no** debe llevar cada una.
- Cómo decidir en qué capa va una pieza de código nueva.

Antes de empezar, recuerda del capítulo 2 la regla que lo gobierna todo: **las dependencias solo apuntan hacia el centro**. `Domain` no conoce a nadie; `Application` conoce a `Domain`; `Infrastructure` conoce a ambas; `Api` conoce a `Application` e `Infrastructure` y lo conecta todo.

## El mapa completo

```
src/
├── OfiFlow.Domain            ← LAS REGLAS: qué es un trabajo, qué puede hacerse con él
├── OfiFlow.Application       ← LAS OPERACIONES: qué puede hacer el sistema
├── OfiFlow.Infrastructure    ← LO TÉCNICO: base de datos, tokens, contraseñas
└── OfiFlow.Api               ← LA PUERTA: HTTP, rutas, errores, seguridad de entrada

tests/                        ← Un proyecto de test por cada capa
```

Una forma de recordarlo con una pregunta por capa:

| Capa | Su pregunta |
|---|---|
| **Domain** | ¿Qué es esto y qué reglas cumple siempre? |
| **Application** | ¿Qué se puede hacer, y en qué pasos? |
| **Infrastructure** | ¿Cómo se guarda, se firma, se consulta? |
| **Api** | ¿Cómo se pide y cómo se responde por HTTP? |

---

## Capa 1 — `OfiFlow.Domain`

### Qué es y por qué existe

Es el **corazón**: las reglas del negocio escritas en C# puro. Existe para que reglas como "un trabajo completado no se puede cancelar" vivan en **un único sitio** y no dependan de la base de datos, del framework web ni de cómo llegó la petición. Sin esta capa, esas reglas acabarían repetidas en controladores, consultas y servicios, y bastaría con olvidar una copia para que el sistema aceptara un estado imposible.

### Qué entra y qué NO entra

| ✅ Entra | ❌ No entra |
|---|---|
| Entidades, value objects, enumerados | Cualquier cosa de EF Core o SQL |
| Reglas de negocio dentro de las entidades | HTTP, JSON, tokens, contraseñas |
| Códigos de error de negocio | Acceso a la base de datos |
| Constantes de longitud máxima | Nada que pueda cambiar por motivos técnicos |

### Carpetas

La carpeta de primer nivel es siempre un **contexto de negocio** (capítulo 2, sección 1). Cada contexto sigue el mismo patrón: entidad, enumerados, value objects y su fichero de códigos de error.

| Carpeta | Qué contiene | Por qué está ahí |
|---|---|---|
| `Common/` | `Entity`, `AggregateRoot`, `IDomainEvent`, `DomainException`, `Email`, `ITenantOwned`, `IAuditable`, `CommonErrors` | Lo que comparten todos los contextos. `Email` está aquí porque lo usan a la vez Customers e Identity (*shared kernel*) |
| `Customers/` | `Customer`, `CustomerType`, `PhoneNumber`, `CustomerErrors` | Todo lo que es un cliente |
| `Jobs/` | `Job`, `JobStatus`, `JobPriority`, `JobErrors` | Todo lo que es un trabajo y sus transiciones de estado |
| `Tenancy/` | `Tenant`, `TenantUser`, `TenantRole`, `TenancyErrors` | La empresa, y la pertenencia de una persona a ella con un rol |
| `Identity/` | `User`, `IdentityErrors` | Los **datos de negocio** de la persona. Las credenciales (contraseña) no están aquí: viven en Infrastructure |

Dos interfaces de `Common/` merecen atención, porque son "etiquetas" que activan comportamientos automáticos en otras capas:

- **`ITenantOwned`**: "esta entidad pertenece a una empresa". Infrastructure la usa para aplicar el filtro de tenant automáticamente a toda entidad que la lleve (capítulo 5).
- **`IAuditable`**: "rellena mis fechas de creación y modificación". Un interceptor de Infrastructure las completa al guardar, así que ningún handler las toca.

### De qué depende

De ningún proyecto propio. Solo del paquete `MediatR.Contracts` (interfaces vacías, para `IDomainEvent`).

### Seguridad

Esta capa evita que existan **datos imposibles**: un `Email` mal formado no se puede construir, y un `Job` no puede saltarse el orden de sus estados. Además, los errores llevan solo un código y **nunca el valor recibido** (un email es un dato personal que podría acabar en un log).

### Ficheros para mirar

[`Job.cs`](../../src/OfiFlow.Domain/Jobs/Job.cs) (la entidad con sus reglas), [`Email.cs`](../../src/OfiFlow.Domain/Common/Email.cs) (un value object), [`ITenantOwned.cs`](../../src/OfiFlow.Domain/Common/ITenantOwned.cs).

### Cómo se prueba

`OfiFlow.Domain.Tests`. Son los tests más rápidos: no necesitan base de datos ni servidor, porque `Domain` no depende de nada externo.

### Pregunta de tribunal

*¿Por qué `Job.Status` no se puede cambiar desde fuera?* → Porque las transiciones son reglas de negocio y deben pasar siempre por métodos (`Start`, `Complete`, `Cancel`) que las validan; si el campo fuese público cualquiera podría saltárselas.

---

## Capa 2 — `OfiFlow.Application`

### Qué es y por qué existe

Es el **catálogo de lo que el sistema sabe hacer**: crear un cliente, completar un trabajo, iniciar sesión. Cada operación es un *caso de uso*. Su papel es **orquestar**: cargar lo que hace falta, pedirle a `Domain` que aplique la regla, y guardar. No contiene reglas de negocio propias (esas están en `Domain`) ni conoce la tecnología (eso es `Infrastructure`).

Existe para separar "qué se puede hacer" de "cómo se hace técnicamente". Gracias a eso, los casos de uso se pueden probar sin servidor web ni SQL Server real.

### Qué entra y qué NO entra

| ✅ Entra | ❌ No entra |
|---|---|
| Un Command o Query y su handler por caso de uso | Reglas que pertenecen a una entidad (van a `Domain`) |
| Validadores de entrada (FluentValidation) | Tipos de HTTP (`HttpContext`, códigos de estado) |
| **Contratos** (interfaces) que `Infrastructure` implementa | La implementación de esos contratos |
| DTOs de lectura | El proveedor concreto de base de datos |

### Carpetas

**Dentro de cada contexto** (`Customers/`, `Jobs/`, `Identity/`):

```
Jobs/
├── Commands/
│   ├── CreateJob/      → CreateJobCommand, CreateJobCommandValidator, CreateJobCommandHandler
│   ├── StartJob/       → StartJobCommand, StartJobCommandHandler
│   └── ...             → una carpeta por caso de uso
├── Queries/
│   ├── GetJob/         → GetJobQuery, GetJobQueryHandler
│   └── GetJobs/
└── JobDto.cs           → la forma en que se devuelve un trabajo
```

Cada caso de uso es una carpeta autocontenida: lo que hace falta para entenderlo está junto. Añadir una operación nueva es crear una carpeta, no editar diez ficheros.

**Contextos que hay hoy:**

| Carpeta | Contenido |
|---|---|
| `Customers/` | Crear, ver, listar, modificar y eliminar clientes |
| `Jobs/` | Crear, ver, listar, modificar, empezar, completar, cancelar y asignar trabajos |
| `Identity/` | Registro, login y renovación de token. Incluye `AuthResultDto`, `PasswordRules` (longitud mínima y máxima) y `OfiFlowClaimTypes` (el nombre del *claim* del tenant en el token) |
| `Tenancy/` | Reservada: todavía no tiene casos de uso propios (el registro, que crea la empresa, está en `Identity`) |

**La carpeta `Common/`**, lo transversal:

| Subcarpeta | Qué contiene | Por qué |
|---|---|---|
| `Abstractions/` | `ITenantContext`, `IIdentityService`, `ITokenService` | Los **contratos** que Application necesita y que `Infrastructure` implementa. Aquí se produce la inversión de dependencias del capítulo 2 |
| `Persistence/` | `IApplicationDbContext` | El contrato para acceder a los datos, sin atarse a SQL Server |
| `Behaviors/` | `ValidationBehavior` | Código que se ejecuta antes de **todos** los handlers (hoy, validar) |
| `Exceptions/` | `NotFoundException`, `BusinessRuleException`, `IdentityOperationException` | Los errores que Application puede lanzar, cada uno con un código estable |
| `Validation/` | `ValidationRules` | Reglas de validación reutilizadas por varios validadores (email, teléfono) |
| `Logging/` | `SecurityEventIds` | El catálogo de identificadores de los eventos de seguridad (login fallido, rate limit...) |

Y en la raíz, [`DependencyInjection.cs`](../../src/OfiFlow.Application/DependencyInjection.cs): una sola llamada, `AddApplication()`, que registra MediatR y todos los validadores buscándolos en el ensamblado automáticamente.

### Un detalle importante: ¿qué pasa con las reglas que cruzan varias entidades?

Hay reglas que una sola entidad no puede comprobar. Ejemplo: "no se puede eliminar un cliente con trabajos activos". `Customer` no conoce sus trabajos, así que la regla vive en el handler:

```csharp
var hasActiveJobs = await dbContext.Jobs.AnyAsync(
    j => j.CustomerId == request.Id
      && j.Status != JobStatus.Completed && j.Status != JobStatus.Cancelled,
    cancellationToken);

if (hasActiveJobs)
    throw new BusinessRuleException(CustomerErrors.HasActiveJobs);
```

Por eso existen **dos excepciones distintas**:
- `DomainException` (de `Domain`): se rompe una regla **dentro de una sola entidad**.
- `BusinessRuleException` (de `Application`): se rompe una regla que **abarca varias**.

Las dos acaban como un error 400 para el cliente, pero distinguirlas mantiene claro dónde vive cada regla.

### De qué depende

De `Domain`. Y de los paquetes MediatR, FluentValidation y EF Core (este último solo para el tipo `DbSet<T>`, ver capítulo 2).

### Seguridad

- **Validación antes del handler:** nada llega a la lógica sin pasar por su validador, y los datos con longitud excesiva se rechazan con 400.
- **Sin `TenantId` en ningún Command o Query.** El handler toma el tenant de `ITenantContext`, nunca del cliente. Un test de arquitectura lo impone (protección contra *mass assignment*).
- Los errores llevan códigos, no datos personales.

### Ficheros para mirar

[`CreateJobCommandHandler.cs`](../../src/OfiFlow.Application/Jobs/Commands/CreateJob/CreateJobCommandHandler.cs), [`DeleteCustomerCommandHandler.cs`](../../src/OfiFlow.Application/Customers/Commands/DeleteCustomer/DeleteCustomerCommandHandler.cs), [`IApplicationDbContext.cs`](../../src/OfiFlow.Application/Common/Persistence/IApplicationDbContext.cs).

### Cómo se prueba

`OfiFlow.Application.Tests`. Los handlers se prueban contra una base de datos **en memoria** y con **dobles de prueba** (`FakeTenantContext`, `FakeIdentityService`, `FakeTokenService`) que sustituyen a los contratos. Los validadores tienen sus propios tests.

### Pregunta de tribunal

*¿Dónde está la regla "no borrar un cliente con trabajos activos", y por qué no en `Customer`?* → En el handler de Application, porque cruza dos agregados (`Customer` y `Job`) y ninguno de los dos conoce al otro.

---

## Capa 3 — `OfiFlow.Infrastructure`

### Qué es y por qué existe

Es **todo lo técnico**: guardar en SQL Server, firmar tokens, guardar contraseñas de forma segura, saber en qué empresa está el usuario. Implementa los contratos que `Application` declaró. Es la **única capa que conoce** SQL Server, el formato JWT y cómo se mapea cada entidad a una tabla.

Existe para que esos detalles se puedan **cambiar sin tocar las reglas del negocio**. Si mañana se cambia de base de datos, se modifica esta capa y nada más.

### Qué entra y qué NO entra

| ✅ Entra | ❌ No entra |
|---|---|
| El `DbContext` real y la configuración de cada tabla | Reglas de negocio |
| Emisión y validación de tokens, hash de contraseñas | Decisiones sobre qué operaciones existen |
| Implementaciones de los contratos de Application | Tipos de la capa Api (rutas, middlewares) |
| Migraciones de base de datos | |

### Carpetas

| Carpeta | Qué contiene | Por qué está ahí |
|---|---|---|
| `Persistence/` | `ApplicationDbContext` | El `DbContext` real: implementa `IApplicationDbContext` y aplica **automáticamente** el filtro de tenant a toda entidad `ITenantOwned` |
| `Persistence/Configurations/` | Una clase por entidad (`JobConfiguration`, `CustomerConfiguration`...) | Define cómo cada entidad se convierte en tabla (tipos, longitudes, índices). Así **`Domain` no necesita atributos de EF Core** |
| `Persistence/Interceptors/` | `AuditableEntitySaveChangesInterceptor` | Rellena `CreatedAt` y `UpdatedAt` al guardar, sin que ningún handler lo haga |
| `Persistence/Migrations/` | Una clase por cambio del esquema | El historial de la base de datos, generado con `dotnet ef`. Es un registro, no se escribe a mano |
| `Persistence/` (otros) | `ApplicationDbContextFactory`, `ConnectionStringNames` | La fábrica la usa solo `dotnet ef` para generar migraciones; los nombres evitan repetir cadenas mágicas |
| `Identity/` | `IdentityService`, `TokenService`, `ApplicationUser`, `RefreshToken`, `JwtOptions` | Contraseñas, tokens y su configuración (ver más abajo) |
| `Tenancy/` | `TenantContext` | Lee el `tenant_id` del token del usuario autenticado. Implementa `ITenantContext` |

Y en la raíz, [`DependencyInjection.cs`](../../src/OfiFlow.Infrastructure/DependencyInjection.cs): `AddInfrastructure()` conecta el `DbContext`, los servicios y la validación de tokens JWT. **Si falta el secreto `Jwt:Secret`, la aplicación se niega a arrancar** con un mensaje que indica cómo configurarlo, antes que arrancar de forma insegura.

### Un diseño que sorprende: las credenciales están separadas del usuario

Hay **dos objetos** para una misma persona, con el mismo `Id`:

| Objeto | Vive en | Guarda |
|---|---|---|
| `User` | `Domain` | Datos de negocio: nombre, email de contacto |
| `ApplicationUser` | `Infrastructure` | Credenciales: el hash de la contraseña |

¿Por qué? Porque `Domain` no debe saber qué es una contraseña ni cómo se almacena; eso es un detalle técnico. Además, el código de negocio nunca toca un hash por accidente. Los dos se guardan juntos en una misma transacción al registrarse.

### De qué depende

De `Domain` y `Application` (para implementar sus contratos).

### Seguridad

Es la capa donde más controles técnicos se concentran:

- **Contraseñas** guardadas con hash (`PasswordHasher`, PBKDF2), nunca en claro.
- **Tokens JWT** validados en cada petición: firma, emisor, destinatario y caducidad, sin margen de tolerancia.
- **Refresh tokens** guardados solo como hash, con **rotación** y detección de robo: si se reutiliza uno ya usado, se revoca toda la cadena.
- **Aislamiento entre empresas** con el filtro global de EF Core (capítulo 5).
- **SQL parametrizado** de forma automática por EF Core (no hay concatenación de SQL).
- **Secreto JWT obligatorio** para arrancar y fuera del repositorio.

### Ficheros para mirar

[`ApplicationDbContext.cs`](../../src/OfiFlow.Infrastructure/Persistence/ApplicationDbContext.cs) (el filtro por reflexión), [`JobConfiguration.cs`](../../src/OfiFlow.Infrastructure/Persistence/Configurations/JobConfiguration.cs), [`TenantContext.cs`](../../src/OfiFlow.Infrastructure/Tenancy/TenantContext.cs).

### Cómo se prueba

`OfiFlow.Infrastructure.Tests`, contra un **SQL Server real en Docker** (Testcontainers). Incluye el test más importante del proyecto: `TenantIsolationIntegrationTests`, que demuestra que una empresa nunca ve los datos de otra.

### Pregunta de tribunal

*¿Por qué la contraseña no está en la entidad `User` de `Domain`?* → Porque almacenar credenciales es un detalle técnico; `Domain` solo debe contener reglas de negocio, y así el código de negocio nunca maneja un hash.

---

## Capa 4 — `OfiFlow.Api`

### Qué es y por qué existe

Es la **puerta de entrada**: recibe peticiones HTTP, las convierte en Commands o Queries, y convierte el resultado en una respuesta. Es también la capa que **conecta todo** al arrancar (*composition root*): es la única que conoce a las demás y decide qué implementación real corresponde a cada contrato.

Existe para que **ninguna otra capa sepa nada de HTTP**. Mañana podría añadirse una app móvil o un proceso por línea de comandos que reutilice `Application` sin tocarla.

### Qué entra y qué NO entra

| ✅ Entra | ❌ No entra |
|---|---|
| Rutas y su asociación con Commands/Queries | Reglas de negocio |
| Convertir errores en respuestas HTTP | Acceso a datos |
| Middlewares de seguridad (cabeceras, rate limiting) | Lógica de casos de uso |
| Configuración de arranque | |

### Carpetas y ficheros

| Elemento | Qué contiene | Por qué está ahí |
|---|---|---|
| `Program.cs` | El arranque y el **orden** de los middlewares | El orden es una decisión de seguridad (ver capítulo 8) |
| `Endpoints/` | `AuthEndpoints`, `CustomerEndpoints`, `JobEndpoints` | Un fichero por contexto. Cada endpoint hace tres cosas: recibir, enviar a MediatR, devolver |
| `Common/` | `GlobalExceptionHandler`, `RateLimiting`, `SecurityHeadersMiddleware`, `RequestLogScopeMiddleware`, `Localization`, `ApiErrors` | Todo lo transversal de HTTP: errores, límites, cabeceras, idioma |
| `Resources/` | `ErrorMessages.resx` | El **diccionario** de mensajes de error en español. El código devuelve un código y el texto sale de aquí |
| `Properties/` | `launchSettings.json` | Cómo arranca en local |
| `appsettings.json` | Configuración base, **sin secretos** ni cadena de conexión | En producción esos valores llegan por variables de entorno |
| `appsettings.Development.json` | Cadena de conexión a LocalDB y opciones de JWT (sin el secreto) | Solo para desarrollo local |

Un endpoint típico es deliberadamente corto: no decide nada, solo traduce.

```csharp
group.MapPost("/", async (CreateJobCommand command, ISender sender, CancellationToken ct) =>
    {
        var id = await sender.Send(command, ct);          // MediatR lo entrega al handler
        return Results.Created($"{Route}/{id}", new { id });
    })
    .WithName("CreateJob");
```

Fíjate en que el grupo entero se declara con `RequireAuthorization()`: **toda ruta exige sesión salvo que se indique lo contrario**. Solo el grupo de `auth` (registro, login, renovar) es anónimo.

`Api/Common/` parece un cajón de sastre, pero tiene un criterio: es **todo lo transversal que solo tiene sentido en HTTP**. Un middleware de cabeceras o un límite de peticiones por IP no existen fuera de la web, así que no pertenecen a ninguna otra capa.

> **Nota:** `OfiFlow.Api.http` es un fichero para probar peticiones desde Visual Studio. Hoy conserva el contenido de la plantilla por defecto (una ruta `weatherforecast` que ya no existe), así que no sirve todavía como colección de pruebas.

### De qué depende

De `Application` e `Infrastructure`. Es la única capa que referencia las dos.

### Seguridad

Es la **primera línea de defensa**: cabeceras de seguridad, HSTS, rate limiting en la autenticación, autenticación y autorización, y un manejador global de excepciones que devuelve errores estructurados sin filtrar detalles internos. Todo esto se explica en el capítulo 8.

### Ficheros para mirar

[`Program.cs`](../../src/OfiFlow.Api/Program.cs), [`JobEndpoints.cs`](../../src/OfiFlow.Api/Endpoints/JobEndpoints.cs), [`GlobalExceptionHandler.cs`](../../src/OfiFlow.Api/Common/GlobalExceptionHandler.cs).

### Cómo se prueba

`OfiFlow.Api.Tests`: arranca la API entera en memoria (`WebApplicationFactory`) y le hace peticiones reales. No necesita SQL Server: sus tests o no tocan la base de datos, o la validación rechaza la petición antes de llegar a ella. Aquí también están los **tests de arquitectura**, que fallan si alguien introduce SQL crudo, usa `IgnoreQueryFilters()` donde no debe, o añade un código de error sin su mensaje.

### Pregunta de tribunal

*¿Por qué un endpoint no contiene lógica?* → Porque la Api solo traduce entre HTTP y Application; si la lógica estuviese aquí, no se podría reutilizar desde otro tipo de cliente ni probar sin levantar un servidor web.

---

## Cómo se conecta todo

Un handler pide en su constructor `IApplicationDbContext`, pero `Application` no sabe quién lo implementa. ¿Quién se lo da? **La Api, al arrancar**:

```csharp
// Program.cs
builder.Services.AddApplication();                              // registra casos de uso y validadores
builder.Services.AddInfrastructure(builder.Configuration);      // registra las implementaciones reales
```

Esto se llama **inyección de dependencias**: las clases declaran *qué necesitan* (una interfaz) y un contenedor les entrega *la pieza concreta*. Es la herramienta que hace posible la inversión de dependencias del capítulo 2.

## ¿En qué capa pongo...?

La tabla que más te servirá en el día a día:

| Quiero añadir... | Va en... |
|---|---|
| La regla "un trabajo completado no se puede cancelar" | `Domain` → método `Job.Cancel()` |
| La longitud máxima del título de un trabajo | `Domain` → constante `Job.TitleMaxLength`, usada por la configuración de EF y por el validador |
| Una regla que mira varias entidades (borrar cliente con trabajos activos) | `Application` → handler, con `BusinessRuleException` |
| Una operación nueva, por ejemplo "reabrir trabajo" | `Application` → carpeta nueva `Commands/ReopenJob`, más un método en `Job` y un endpoint en `Api` |
| El tipo y la longitud de una columna de la base de datos | `Infrastructure` → `Configurations` |
| Enviar emails con un servicio externo | Contrato en `Application/Common/Abstractions`, implementación en `Infrastructure` |
| Una cabecera de seguridad nueva | `Api` → `SecurityHeadersMiddleware` |
| El texto de un mensaje de error | `Api` → `Resources/ErrorMessages.resx` |
| Un código de error nuevo | `Domain` o `Application` (fichero `*Errors.cs`) **y** su texto en el `.resx`; un test de arquitectura comprueba que no falte ninguno |
| Una ruta HTTP nueva | `Api` → `Endpoints` |

## Los tests: un proyecto por capa

Los cuatro proyectos de `tests/` reflejan las capas y son cada vez más lentos y más realistas conforme se sale del centro:

| Proyecto | Prueba | Con qué |
|---|---|---|
| `OfiFlow.Domain.Tests` | Las reglas del negocio | Nada: C# puro, instantáneo |
| `OfiFlow.Application.Tests` | Los casos de uso y los validadores | Base de datos en memoria y dobles de prueba |
| `OfiFlow.Infrastructure.Tests` | Persistencia, tokens y aislamiento entre empresas | **SQL Server real** en Docker |
| `OfiFlow.Api.Tests` | HTTP, seguridad y arquitectura | La API completa en memoria, sin base de datos |

Se detallan en el capítulo 9.

## Cosas que verás en el repositorio y que conviene saber

- **Ficheros `.gitkeep`** dentro de carpetas que ya tienen contenido (por ejemplo, `Domain/Jobs/.gitkeep`). Son restos de cuando las carpetas se crearon vacías; no hacen nada y son inofensivos.
- **`Application/Tenancy/`** solo contiene un `.gitkeep`: es un contexto reservado todavía sin casos de uso.
- **`Domain/Common/IAuditable`** declara sus fechas con `set` público, porque quien las rellena (el interceptor) está en otra capa y necesita poder escribirlas. Es una concesión pragmática: a diferencia del resto de propiedades de `Job`, estas dos no están protegidas con `private set`.

## Para comprobar que lo has entendido

1. ¿Qué dos preguntas distintas responden `Domain` y `Application`?
2. Quieres añadir "reabrir un trabajo cancelado". Indica qué tocarías en cada capa.
3. ¿Por qué `Infrastructure` tiene una clase `ApplicationUser` y `Domain` tiene `User`?
4. ¿Qué hace `DependencyInjection.cs` y por qué existe uno en `Application` y otro en `Infrastructure`?
5. ¿Por qué `Api/Common/` contiene el middleware de cabeceras de seguridad y no `Infrastructure`?
6. ¿Qué diferencia hay entre `DomainException` y `BusinessRuleException`?

*(Respuestas: 1 = Domain: qué es esto y qué reglas cumple; Application: qué se puede hacer y en qué pasos. 2 = Domain: un método en `Job` con la regla de qué estados pueden reabrirse; Application: carpeta `Commands/ReopenJob` con comando, validador y handler; Api: un endpoint `POST /jobs/{id}/reopen`; Infrastructure: nada, salvo que cambie el esquema; tests en cada capa. 3 = Domain no debe conocer contraseñas ni su almacenamiento; son datos técnicos. 4 = Cada capa registra sus propias piezas en el contenedor; la Api los llama al arrancar. Así cada capa se configura a sí misma. 5 = Porque las cabeceras son un concepto de HTTP, y Infrastructure no debe conocer la web. 6 = DomainException: regla dentro de una sola entidad; BusinessRuleException: regla que abarca varias.)*

## Siguiente capítulo

El capítulo 4 sigue **una petición real de principio a fin** (`POST /api/v1/jobs`), señalando en qué carpeta y en qué línea ocurre cada paso.
