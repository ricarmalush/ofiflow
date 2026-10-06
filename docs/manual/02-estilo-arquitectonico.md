# Capítulo 2 — Estilo arquitectónico

## Qué vas a aprender

- Qué significa "arquitectura" y por qué se habla de varios estilos a la vez.
- Los cinco estilos que combina OfiFlow y qué pregunta responde cada uno.
- Cómo funciona cada uno, con código real.
- Qué hemos descartado y por qué.
- Qué aplicamos a fondo, qué de forma pragmática y qué todavía no.

## La respuesta corta

> OfiFlow es un **monolito modular** con **Clean Architecture** como regla de dependencias entre capas, **DDD** para modelar el negocio y **CQRS pragmático** con MediatR para organizar los casos de uso. Es **multi-tenant**, con una sola base de datos.

Si te hacen la pregunta "¿qué arquitectura tiene vuestro proyecto?", esa frase es la respuesta. El resto del capítulo explica cada palabra.

## Por qué hay varios estilos y no uno

La arquitectura es "cómo se organiza el código para que sea fácil de entender y de cambiar". Pero esa pregunta tiene **varios niveles**, y cada estilo responde a uno distinto. Piensa en un restaurante:

- **Qué local es y cuántas cocinas tiene** (una o varias) → *Monolito modular*.
- **Quién puede dar órdenes a quién** (el camarero pide al cocinero, nunca al revés) → *Clean Architecture*.
- **Cómo se llama y cómo se hace cada plato** (el lenguaje del oficio) → *DDD*.
- **Cómo se procesa cada pedido** (una comanda es una cosa, una consulta de la carta es otra) → *CQRS*.
- **Cómo se atiende a cada mesa sin mezclar las cuentas** → *Multi-tenancy*.

No compiten entre sí: se combinan.

| Estilo | Pregunta que responde | Dónde se decidió |
|---|---|---|
| **Monolito modular** | ¿Cómo se despliega y se divide el sistema? | [ADR-005](../adr/ADR-005-modular-monolith.md) |
| **Clean Architecture** | ¿Quién puede depender de quién? | Estructura de proyectos (este capítulo) |
| **DDD** | ¿Cómo se modela el negocio? | [ADR-005](../adr/ADR-005-modular-monolith.md), [ADR-006](../adr/ADR-006-persistencia-domain-events.md) |
| **CQRS pragmático** | ¿Cómo se organiza cada operación? | [ADR-003](../adr/ADR-003-cqrs.md) |
| **Multi-tenancy** | ¿Cómo se aíslan los clientes? | [ADR-002](../adr/ADR-002-multi-tenancy.md) |

---

## 1. Monolito modular

### La idea

Un **monolito** es una aplicación que se despliega como **una sola pieza**. Lo contrario son los **microservicios**: muchas aplicaciones pequeñas que se hablan por red.

Un monolito suele tener mala fama porque, mal hecho, acaba siendo una bola de barro donde todo depende de todo. Un **monolito modular** evita eso: sigue siendo una sola aplicación, pero por dentro está dividida en **módulos con límites claros**.

En DDD a cada módulo de negocio se le llama **bounded context** (contexto delimitado): una zona del negocio con su propio lenguaje. En OfiFlow hay estos:

`Identity` · `Tenancy` · `Customers` · `Jobs` (y más adelante `Scheduling`, `Quoting`, `Invoicing`, `Payments`...)

### Cómo se ve en el código

Cada contexto es una **carpeta dentro de cada capa**, no un proyecto aparte:

```
src/OfiFlow.Domain/Jobs/                 ← las reglas de los trabajos
src/OfiFlow.Application/Jobs/            ← las operaciones sobre trabajos
src/OfiFlow.Domain/Customers/            ← las reglas de los clientes
src/OfiFlow.Application/Customers/       ← las operaciones sobre clientes
```

### Por qué no microservicios

Con una sola persona desarrollando, los microservicios añadirían problemas (desplegar muchos servicios, comunicarlos, ver los logs repartidos) sin aportar beneficio todavía. El monolito modular da lo importante: **límites claros ahora, y la puerta abierta a separar un módulo más adelante** si hace falta.

El ADR-005 fija tres señales para plantearse extraer un contexto como servicio independiente, y exige que se cumplan de forma sostenida, no puntual: una persona dedicada solo a ese contexto, necesidad de escalarlo de forma independiente, o necesidad de desplegarlo con mucha más frecuencia que el resto.

### El riesgo que hay que vigilar

Los límites entre carpetas **no los impone el compilador**: nada impide que `Jobs` use una clase interna de `Customers`. El propio ADR-005 lo reconoce como riesgo aceptado ("requiere disciplina activa"). Hoy lo vigila el autor; no hay todavía un test automático que lo compruebe.

---

## 2. Clean Architecture

### La idea

Clean Architecture responde a una sola pregunta: **¿en qué dirección se permite que dependan unas partes de otras?** Y la respuesta es una regla:

> **Las dependencias solo apuntan hacia el centro.** Lo importante (las reglas del negocio) no depende de lo accesorio (la base de datos, HTTP, el framework).

Se dibuja como cebolla de capas concéntricas; en OfiFlow, cuatro proyectos:

```
        ┌─────────────────────────────────────────────┐
        │  Api            (HTTP: rutas, middlewares)  │
        │  ┌───────────────────────────────────────┐  │
        │  │ Infrastructure (EF Core, JWT, SQL)    │  │
        │  │  ┌─────────────────────────────────┐  │  │
        │  │  │ Application (casos de uso)      │  │  │
        │  │  │  ┌───────────────────────────┐  │  │  │
        │  │  │  │ Domain (reglas del        │  │  │  │
        │  │  │  │        negocio)           │  │  │  │
        │  │  │  └───────────────────────────┘  │  │  │
        │  │  └─────────────────────────────────┘  │  │
        │  └───────────────────────────────────────┘  │
        └─────────────────────────────────────────────┘
        Las flechas de dependencia van de fuera hacia dentro.
```

### Cómo se impone en OfiFlow

No es una convención que haya que recordar: **lo impiden las referencias entre proyectos** (`.csproj`). Si `Domain` no referencia a `Infrastructure`, el compilador no deja usarla.

| Proyecto | Referencia a | Qué significa |
|---|---|---|
| `OfiFlow.Domain` | ningún proyecto | Es el centro: no conoce a nadie |
| `OfiFlow.Application` | `Domain` | Conoce las reglas, pero no la base de datos concreta ni HTTP |
| `OfiFlow.Infrastructure` | `Domain` y `Application` | Implementa lo que Application necesita (guardar datos, emitir tokens) |
| `OfiFlow.Api` | `Application` e `Infrastructure` | Es la capa más externa y la que **conecta todo** al arrancar |

### Por qué se hace así

1. **Se puede cambiar lo accesorio sin tocar lo importante.** Si algún día se migra de SQL Server a PostgreSQL (el ADR-001 lo contempla), cambia `Infrastructure` y las reglas del negocio ni se enteran.
2. **Las reglas se pueden probar sin base de datos ni servidor.** Los tests de `Domain` son rápidos porque no necesitan nada externo.
3. **Cada cosa está en un sitio predecible.** Si buscas "dónde se valida X", sabes en qué capa mirar.

### Cómo se "invierte" la dependencia

Application necesita guardar datos, pero no debe conocer la base de datos concreta. Solución: Application **define un contrato** (una interfaz) y Infrastructure **lo implementa**:

```csharp
// Application/Common/Persistence/IApplicationDbContext.cs  → el contrato
public interface IApplicationDbContext
{
    DbSet<Job> Jobs { get; }
    DbSet<Customer> Customers { get; }
    // ...
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}

// Infrastructure/Persistence/ApplicationDbContext.cs  → la implementación real
public sealed class ApplicationDbContext(...) : DbContext(options), IApplicationDbContext
```

Los *handlers* de Application piden `IApplicationDbContext`; al arrancar, la Api le entrega la implementación real. Esto es la **inversión de dependencias**. Por eso en los tests de Application se puede usar una base de datos en memoria sin cambiar una línea de los handlers.

### Dos matices honestos

La arquitectura real tiene dos concesiones pragmáticas que conviene conocer, porque un revisor puede encontrarlas:

- **`Domain` referencia un paquete: `MediatR.Contracts`.** Lo usa solo porque `IDomainEvent` hereda de `INotification` (una interfaz vacía). Es un paquete de contratos, sin lógica ni dependencias. Decir que "Domain no depende de nada" es cierto respecto a *proyectos propios*, no respecto a ese paquete.
- **`Application` referencia el paquete de EF Core** (`Microsoft.EntityFrameworkCore`), porque `IApplicationDbContext` expone `DbSet<T>`, que vive en ese paquete. Lo que **no** conoce es el proveedor concreto (SQL Server) ni la implementación real del contexto: eso vive en Infrastructure. Se abstrae el contexto, pero **sin montar encima un patrón *Repository*** genérico, que aquí solo añadiría código sin aportar nada (ADR-006). Ojo: el ADR-006 dice que Application no referencia EF Core "directamente", y en la práctica sí referencia el paquete. La regla que se cumple de verdad es "Application no conoce el proveedor ni la implementación", y el ADR lleva una nota que lo aclara.

---

## 3. DDD (Domain-Driven Design)

### La idea

DDD es una forma de diseñar donde **el centro del software es el negocio, no la base de datos**. Dos ideas lo resumen:

1. Usar **el lenguaje del negocio** en el código: `Job`, `Customer`, `Tenant`, `Start`, `Complete`, no `Row`, `Record` o `UpdateStatus(3)`.
2. Poner las **reglas del negocio dentro de las entidades**, no desparramadas por controladores y servicios.

Clean Architecture dice **dónde** va cada cosa. DDD dice **cómo se modela lo que va dentro de `Domain`**. Por eso se usan juntos.

DDD tiene una parte **estratégica** (cómo dividir el negocio en contextos) y una **táctica** (los bloques con los que se modela cada contexto). OfiFlow aplica las dos.

### DDD táctico: los bloques

**Entidad (`Entity`).** Un objeto que tiene **identidad**: dos clientes llamados "Juan" siguen siendo clientes distintos porque tienen `Id` distinto. Está en [`Entity.cs`](../../src/OfiFlow.Domain/Common/Entity.cs): dos entidades son iguales si son del mismo tipo y tienen el mismo `Id`.

**Value Object (objeto de valor).** Un objeto **sin identidad**, que vale por su contenido y es **inmutable**. El email `a@b.com` es el mismo en cualquier sitio. Ejemplos: [`Email`](../../src/OfiFlow.Domain/Common/Email.cs) y [`PhoneNumber`](../../src/OfiFlow.Domain/Customers/PhoneNumber.cs). Su ventaja: **no puede existir un email inválido**, porque se valida al crearlo y no se puede modificar después:

```csharp
public static Email Create(string value)
{
    if (string.IsNullOrWhiteSpace(value))
        throw new DomainException(CommonErrors.EmailRequired);

    if (!IsValid(value))
        throw new DomainException(CommonErrors.EmailInvalid);

    return new Email(value);   // el constructor es privado: solo se llega aquí validado
}
```

**Aggregate Root (raíz de agregado).** Un **agregado** es un grupo de objetos que se tratan como una unidad, y la **raíz** es la única puerta de entrada para modificarlo. [`Job`](../../src/OfiFlow.Domain/Jobs/Job.cs) y [`Customer`](../../src/OfiFlow.Domain/Customers/Customer.cs) heredan de `AggregateRoot`. Desde fuera no se cambia un campo del trabajo directamente: se le pide a `Job` que lo haga.

### La idea más importante de DDD: las reglas dentro de la entidad

Mira cómo se completa un trabajo en OfiFlow:

```csharp
public sealed class Job : AggregateRoot, ITenantOwned, IAuditable
{
    public JobStatus Status { get; private set; }   // private set: nadie de fuera lo cambia

    public void Complete()
    {
        if (Status != JobStatus.InProgress)
            throw new DomainException(JobErrors.CannotComplete, Status);

        Status = JobStatus.Completed;
    }
}
```

Fíjate en tres cosas:

1. **`private set`:** no se puede escribir `job.Status = JobStatus.Completed` desde fuera. La única forma es llamar a `Complete()`.
2. **La regla está dentro:** "solo se completa lo que está en curso" vive en `Complete()`, en un único sitio.
3. **Un `Job` no puede estar en un estado imposible**, porque el constructor es privado y solo se crea con `Job.Create(...)`, que valida el título.

Compáralo con el estilo contrario, el de las **entidades anémicas**, donde la entidad es un saco de propiedades públicas y la lógica está en un "servicio":

```csharp
// ❌ Anémico: cualquiera puede romper la regla, y habría que repetirla en cada sitio
job.Status = JobStatus.Completed;

// ✅ Rico (el de OfiFlow): la regla va con el dato
job.Complete();
```

El handler de Application que completa un trabajo es casi trivial, porque la lógica ya está en `Job`:

```csharp
var job = await dbContext.Jobs.FirstOrDefaultAsync(j => j.Id == request.Id, cancellationToken)
    ?? throw new NotFoundException(JobErrors.NotFound, request.Id);

job.Complete();                                  // la regla vive en Domain

await dbContext.SaveChangesAsync(cancellationToken);
```

### Errores de dominio con código

Cuando se rompe una regla se lanza una `DomainException` con un **código estable** (`job.cannot_complete`), no con un texto. El texto en español sale de un diccionario y la API lo convierte en un error 400. Se explica en el capítulo 7.

### DDD estratégico

- **Bounded contexts:** las carpetas `Jobs`, `Customers`, `Tenancy`, `Identity` (sección 1).
- **Lenguaje ubicuo (*ubiquitous language*):** el mismo vocabulario en el negocio, el código y los tests.
- **Shared kernel (núcleo compartido):** `Email` lo usan a la vez `Customers` e `Identity`, y por eso está en `Domain/Common` y no en uno de los contextos. Es la excepción consciente a "cada contexto va por su cuenta".

### Lo que DDD prevé y OfiFlow todavía no usa: eventos de dominio

Un **evento de dominio** es un aviso de que algo importante ocurrió ("el trabajo se completó") para que otras partes reaccionen (mandar un email, por ejemplo). La infraestructura está **preparada**: `AggregateRoot` guarda una lista de eventos (`AddDomainEvent`) y existe la interfaz `IDomainEvent`.

Pero **hoy ningún agregado lanza eventos y no hay ningún componente que los publique**. El ADR-006 decidió que un interceptor de `SaveChanges` los publicaría, pero ese interceptor aún no se ha escrito (el único que existe es el de auditoría). Se hará cuando haya un caso real que lo necesite (por ejemplo, avisar al cliente al completar un trabajo).

---

## 4. CQRS pragmático con MediatR

### La idea

**CQRS** (*Command Query Responsibility Segregation*) significa separar dos tipos de operaciones:

| | Command (orden) | Query (consulta) |
|---|---|---|
| Qué hace | **Cambia** el estado del sistema | **Solo lee**, no cambia nada |
| Ejemplos | `CreateJobCommand`, `CompleteJobCommand` | `GetJobQuery`, `GetJobsQuery` |
| Devuelve | Casi nada (un `Id`, o nada) | Datos (DTOs) |

Cada operación es una **clase pequeña con su *handler*** (el código que la ejecuta), y se organizan **una carpeta por caso de uso**:

```
Application/Jobs/Commands/CreateJob/
    CreateJobCommand.cs            ← los datos que hacen falta
    CreateJobCommandValidator.cs   ← las comprobaciones de esos datos
    CreateJobCommandHandler.cs     ← el código que lo ejecuta
```

### Por qué "pragmático"

CQRS "extremo" usa dos bases de datos (una para escribir, otra para leer) y *Event Sourcing*. Para una sola aplicación con una sola base de datos eso sería sobreingeniería. OfiFlow usa **la versión simple**: misma aplicación, misma base de datos, solo separa las operaciones en comandos y consultas.

La diferencia práctica está en **cómo trabaja cada una**:

- Un **Command** carga el agregado, le pide que haga algo (`job.Complete()`) y guarda. Pasa por las reglas del dominio.
- Una **Query** **no carga el agregado**: lee directamente y devuelve un DTO optimizado para mostrar, sin pasar por el dominio. Es más rápido y sencillo, y como no cambia nada no hay reglas que proteger:

```csharp
public Task<JobDto?> Handle(GetJobQuery request, CancellationToken cancellationToken)
{
    return dbContext.Jobs
        .Where(j => j.Id == request.Id)
        .Select(j => new JobDto(j.Id, j.CustomerId, j.Title, /* ... */))
        .FirstOrDefaultAsync(cancellationToken);
}
```

### MediatR: el cartero

**MediatR** es la librería que reparte los mensajes. La Api no sabe qué handler ejecuta cada operación: envía el mensaje (`sender.Send(command)`) y MediatR lo entrega al handler correspondiente. Esto desacopla el endpoint del caso de uso.

Además permite **Pipeline Behaviors**: código que se ejecuta **antes de todos los handlers** sin tener que repetirlo en cada uno. Es como un control de acceso por el que pasa cada pedido.

**Estado real:** el ADR-003 prevé cinco behaviors (validación, logging, autorización, transacciones y rendimiento). **Hoy hay dos implementados, y en este orden:**

1. [`AuthorizationBehavior`](../../src/OfiFlow.Application/Common/Behaviors/AuthorizationBehavior.cs) (spec 008, ADR-012): comprueba que el rol del usuario tiene el permiso que declara la operación (`[RequiresPermission(Permission.JobsWrite)]`, por ejemplo). Va **primero**: quien no puede hacer algo no recibe ni siquiera información sobre si sus datos eran válidos.
2. [`ValidationBehavior`](../../src/OfiFlow.Application/Common/Behaviors/ValidationBehavior.cs): ejecuta los validadores antes de cada handler.

Los de logging, transacciones y rendimiento no se han escrito aún. La autorización es *fail-closed*: una operación que no declara su permiso se deniega, y un test de arquitectura impide que exista una sin declarar.

---

## 5. Multi-tenancy

Resumen: una sola base de datos donde cada fila de negocio lleva un `TenantId`, y EF Core añade **automáticamente** `WHERE TenantId = <empresa del token>` a cada consulta, de modo que ningún handler puede olvidarse de filtrar. El tenant sale del token de sesión, nunca de lo que envíe el cliente. Se explica a fondo, con su test, en el capítulo 5 y en el [ADR-002](../adr/ADR-002-multi-tenancy.md).

---

## Qué hemos descartado y por qué

| Alternativa | Por qué no |
|---|---|
| **Microservicios desde el inicio** | Sobreingeniería para una persona: coste operativo desproporcionado (ADR-005) |
| **Monolito sin modularizar** | Sin límites claros el código se acopla y luego es difícil separar nada (ADR-005) |
| **CQRS extremo (Event Sourcing, dos bases de datos)** | Sin necesidad real de esa escala (ADR-003) |
| **Una base de datos por empresa** | Coste de mantenimiento multiplicado por cada cliente (ADR-002) |
| **SQL escrito a mano / procedimientos almacenados en vez de EF Core** | Las reglas acabarían en la base de datos y no en `Domain`, rompiendo DDD. Además la regla actual (ADR-009, R1) prohíbe construir SQL concatenado |
| **Patrón Repository genérico sobre EF Core** | `DbContext` ya cumple ese papel; añadir otra capa sería código extra sin beneficio (ADR-006) |

## Qué aplicamos a fondo, qué de forma pragmática y qué todavía no

| Tema | Estado | Detalle |
|---|---|---|
| Regla de dependencias de Clean Architecture | **A fondo** | La imponen las referencias entre proyectos |
| Entidades ricas, Value Objects, constructores privados | **A fondo** | `Job`, `Customer`, `Email`, `PhoneNumber` |
| CQRS: comandos y consultas separados | **Pragmático** | Una base de datos, sin Event Sourcing; las consultas no pasan por el dominio |
| Abstracción de la base de datos | **Pragmático** | `IApplicationDbContext`, sin patrón Repository |
| Límites entre módulos | **Por disciplina** | Carpetas, no proyectos; sin test automático todavía |
| Pipeline Behaviors | **Parcial** | Autorización y validación hechas; logging, transacciones y rendimiento pendientes |
| Eventos de dominio | **Preparado, sin usar** | La base existe; ningún agregado lanza eventos ni hay quien los publique |
| Permisos por rol (RBAC) | **A fondo** | Tabla única rol → permisos en Domain, comprobada en cada operación y probada por HTTP con un usuario de cada rol. Pendiente: que el técnico solo vea lo asignado, y poder crear usuarios con otro rol |

Que haya cosas pendientes no es una debilidad escondida: son decisiones aplazadas a propósito, con su motivo, porque con un único desarrollador y sin usuarios reales todavía no compensan. Lo importante es que **estén identificadas** y que no se afirme que están hechas.

## Para comprobar que lo has entendido

1. ¿Qué dos preguntas distintas responden Clean Architecture y DDD, y por qué se usan juntas?
2. `Domain` no referencia a `Infrastructure`. ¿Qué evita eso, y cómo consigue Application guardar datos sin conocer la base de datos?
3. ¿Por qué `Job.Status` tiene `private set`, y qué pasaría si fuera público?
4. ¿En qué se diferencia el tratamiento de un Command y el de una Query en OfiFlow, y por qué?
5. ¿Por qué un monolito modular y no microservicios?
6. Nombra una cosa que OfiFlow *prevé* pero *todavía no hace* en arquitectura.

*(Respuestas: 1 = Clean decide quién depende de quién; DDD decide cómo se modelan las reglas dentro de Domain. 2 = evita que el negocio quede atado a la base de datos; Application depende de una interfaz que Infrastructure implementa. 3 = para que solo se cambie el estado llamando a métodos que validan la transición; si fuera público cualquiera podría saltarse las reglas. 4 = el Command pasa por el agregado y sus reglas; la Query lee un DTO directamente porque no cambia nada. 5 = un solo desarrollador: los microservicios añadirían coste sin beneficio, y el monolito modular deja la puerta abierta. 6 = eventos de dominio, o los behaviors de logging, transacciones y rendimiento.)*

## Siguiente capítulo

El capítulo 3 recorre las cuatro capas carpeta a carpeta: qué contiene cada una, por qué existe y qué NO debe llevar.
