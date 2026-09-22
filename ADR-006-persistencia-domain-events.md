# ADR-006 — Persistencia con EF Core y Domain Events

**Estado:** Aceptado
**Fecha:** 2026-09-20
**Proyecto:** OfiFlow

---

## Contexto

ADR-001 ya decidió SQL Server + EF Core, ADR-002 ya decidió Global Query Filter para el aislamiento de tenant, y ADR-005 ya decidió Modular Monolith. Antes de la primera migración quedan varios detalles de implementación sin cerrar que afectan directamente al esquema de base de datos y a cómo Domain/Application se mantienen independientes de EF Core: qué tipo usar para los identificadores, cómo acceder a los datos sin acoplar Domain/Application al paquete de EF Core, cómo aplicar el filtro de tenant sin repetir código entidad por entidad, cómo auditar automáticamente creación/modificación, si se permite borrado lógico, cómo se modela el estado de un `Job`, y cómo se propagan los Domain Events.

## Problema

¿Qué convenciones de persistencia y modelado se usan de forma consistente en todo el proyecto, para no descubrirlas de forma distinta contexto a contexto (Jobs, Customers, y los que vengan después)?

## Opciones consideradas

### Tipo de identificador

**Opción 1: IDs fuertemente tipados (`CustomerId`, `JobId`, etc.)**
- Ventajas: elimina en compilación el riesgo de pasar un `CustomerId` donde se espera un `JobId`.
- Desventajas: fricción real con EF Core (value converters, comparers, más código boilerplate por cada entidad); coste de mantenimiento no justificado para un equipo de una persona sin evidencia de que el error real ocurra.

**Opción 2: `Guid` plano para todos los IDs**
- Ventajas: cero código adicional; suficiente para el volumen y complejidad actual del proyecto.
- Desventajas: el compilador no evita mezclar IDs de distintas entidades; depende de revisión/tests.

### Acceso a datos desde Application

**Opción 1: Repositorio genérico (`IRepository<T>`)**
- Ventajas: patrón muy conocido, abstrae completamente EF Core.
- Desventajas: EF Core ya es en sí mismo un Unit of Work + Repository; añadir otra capa por encima es abstracción sobre abstracción sin problema real que resolver.

**Opción 2: `IApplicationDbContext` con los `DbSet<T>` necesarios**
- Ventajas: Domain/Application no referencian `Microsoft.EntityFrameworkCore` directamente (se mantiene la independencia de infraestructura); mucho menos código que mantener que un repositorio genérico.
- Desventajas: Application conoce la existencia de `DbSet<T>`, aunque no la implementación concreta de EF Core.

### Aislamiento de tenant a nivel de código

**Opción 1: Configurar el Global Query Filter entidad por entidad**
- Ventajas: explícito por entidad.
- Desventajas: fácil de olvidar en una entidad nueva — el mismo riesgo que ADR-002 quiere eliminar, trasladado a "olvidar configurar el filtro" en vez de "olvidar el `WHERE`".

**Opción 2: Interfaz `ITenantOwned { Guid TenantId }` + configuración del filtro una sola vez por reflection sobre todas las entidades que la implementen**
- Ventajas: una entidad nueva que implemente `ITenantOwned` queda protegida automáticamente, sin tocar la configuración de EF Core cada vez.
- Desventajas: ninguna relevante; es el mecanismo estándar recomendado para este patrón en EF Core.

### Auditoría de creación/modificación

**Opción 1: Rellenar `CreatedAt`/`UpdatedAt` manualmente en cada Handler**
- Desventajas: se olvida con el tiempo; trabajo repetido en cada Command.

**Opción 2: Interfaz `IAuditable` + `SaveChanges` interceptor que rellena los campos automáticamente**
- Ventajas: cero trabajo manual por Handler; imposible de olvidar una vez configurado.

### Borrado

**Opción 1: Soft delete (columna `IsDeleted` + filtro adicional)**
- Desventajas: complejidad añadida (otro filtro que combinar con el de tenant) sin necesidad real todavía; la auditoría (sección 36 del prompt maestro) ya cubre el historial de acciones importantes.

**Opción 2: Borrado físico**
- Ventajas: simplicidad; sin filtros adicionales que mantener.

### Modelado del estado de `Job`

**Opción 1: State Pattern**
- Desventajas: sobreingeniería para 6 estados con transiciones simples; no hay reglas condicionales complejas todavía que lo justifiquen.

**Opción 2: Enum `JobStatus` + métodos de transición con guard clauses en el aggregate (`Job.Complete()`, `Job.Cancel()`, etc.)**
- Ventajas: cubre el invariante (transiciones válidas) sin la complejidad de un patrón adicional; fácil de evolucionar a State Pattern más adelante si las reglas se complican.

### Librería de validación de Commands

**Opción 1: Validación manual dentro de cada Handler**
- Desventajas: código repetido, fácil de olvidar en un Handler nuevo.

**Opción 2: FluentValidation + `ValidationBehavior` de MediatR**
- Ventajas: cierra el Pipeline Behavior "Validación" que ADR-003 ya preveía desde el MVP sin especificar librería; validadores declarativos, un archivo por Command, se ejecutan automáticamente antes del Handler.

### Propagación de Domain Events

**Opción 1: Event Sourcing / bus de eventos independiente**
- Desventajas: ya descartado por el prompt maestro (sección 38) y por ADR-003; sobreingeniería para el tamaño actual.

**Opción 2: `AggregateRoot` base con lista de eventos pendientes + `SaveChanges` interceptor que los publica vía `IPublisher` de MediatR tras confirmar la transacción**
- Ventajas: reutiliza MediatR, ya decidido en ADR-003, sin introducir infraestructura de mensajería nueva; los eventos solo se publican si la transacción tuvo éxito.

## Decisión

Se decide:
- **IDs:** `Guid` plano para todos los identificadores (Tenant, User, Customer, Job, etc.). No se introducen IDs fuertemente tipados en el MVP.
- **Acceso a datos:** interfaz `IApplicationDbContext` en Application (con los `DbSet<T>` necesarios), implementada por `ApplicationDbContext` en Infrastructure. No se usa repositorio genérico.
- **Aislamiento de tenant:** interfaz `ITenantOwned { Guid TenantId }`, implementada por toda entidad multi-tenant. El Global Query Filter de ADR-002 se configura una sola vez por reflection sobre todas las entidades que implementen `ITenantOwned`, usando el `TenantId` actual provisto por `ITenantContext` (ya definido conceptualmente en ADR-002).
- **Auditoría:** interfaz `IAuditable { CreatedAt, UpdatedAt }` + un `SaveChanges` interceptor que rellena estos campos automáticamente.
- **Borrado:** físico. No se implementa soft delete en el MVP.
- **Estado de `Job`:** enum `JobStatus` (`New, Pending, Scheduled, InProgress, Completed, Cancelled`) + métodos de transición con guard clauses en el aggregate `Job`. No se usa State Pattern.
- **Validación:** FluentValidation + `ValidationBehavior` de MediatR, cerrando el Pipeline Behavior "Validación" ya previsto en ADR-003.
- **Domain Events:** clase base `AggregateRoot` con `DomainEvents` pendientes; un `SaveChanges` interceptor los recoge tras `SaveChangesAsync` y los publica como `INotification` de MediatR. Se distinguen conceptualmente de Integration Events (sección 37 del prompt maestro), aunque estos últimos no se implementan todavía.

## Consecuencias

### Positivas
- Domain y Application permanecen independientes del paquete de EF Core (solo Infrastructure lo referencia).
- El aislamiento de tenant queda garantizado para toda entidad nueva que implemente `ITenantOwned`, sin configuración adicional por entidad.
- Auditoría y Domain Events funcionan de forma automática, sin trabajo manual repetido por Handler.
- Menor cantidad de código a mantener por una sola persona (sin repositorio genérico, sin soft delete, sin State Pattern prematuro).

### Negativas / riesgos aceptados
- Sin IDs fuertemente tipados, un error de programación podría pasar un `Guid` de una entidad donde se espera el de otra; se acepta el riesgo y se mitiga con tests, revisándose si en la práctica se convierte en una fuente real de bugs.
- Sin soft delete, un borrado físico es irreversible salvo por lo que quede en el log de auditoría (sección 36); se acepta porque no hay requisito de negocio ni legal que exija recuperación de datos borrados en esta fase.
- El enum `JobStatus` + guard clauses puede quedarse corto si en el futuro las reglas de transición dependen de rol, tipo de trabajo u otras condiciones; se acepta y se revisará si aparece esa necesidad real.

### Qué queda cerrado (no se debe reabrir sin una razón nueva y explícita)
- No usar IDs fuertemente tipados ni repositorio genérico mientras no exista evidencia real de que su ausencia causa problemas.
- No implementar soft delete, Event Sourcing ni State Pattern para `Job` mientras no exista una necesidad real y justificada.

### Qué queda abierto para revisar más adelante
- Revisar si el enum `JobStatus` sigue siendo suficiente cuando se aborden Scheduling/Quoting/Invoicing (fases 3-4).
- Revisar si conviene introducir IDs fuertemente tipados solo para las entidades donde históricamente se hayan producido errores de mezcla de IDs.
- Diseñar Integration Events cuando se aborden background jobs (WhatsApp, emails) en fases posteriores — no confundir con Domain Events.
- ~~Mecanismo para los tests de integración... contra SQL Server real~~ — **cerrado en ADR-008** (2026-09-22): LocalDB con base de datos de nombre único por ejecución, no Testcontainers.

---

## Relación con otros ADR

- Depende de: ADR-001 (SQL Server + EF Core), ADR-002 (Multi-Tenancy — usa `ITenantContext` y extiende el Global Query Filter con el mecanismo genérico por `ITenantOwned`), ADR-003 (CQRS — cierra la librería de validación del Pipeline Behavior, y reutiliza MediatR para publicar Domain Events), ADR-005 (Modular Monolith — estas convenciones se aplican de forma uniforme en todos los contextos).
