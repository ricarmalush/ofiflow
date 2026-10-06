# Spec — 007-tenant-isolation-hardening

**Estado:** Aprobada (2026-10-06)
**Fecha:** 2026-10-06
**Bounded Context:** Tenancy (transversal: Infrastructure y tests)
**Depende de:** ADR-002 (Multi-Tenancy), ADR-008 (Testcontainers), ADR-009 R2 (aislamiento), spec 004 (guardarraíles)

---

## Objetivo

Que el aislamiento entre empresas deje de depender de que el autor de la próxima entidad o del próximo handler "se acuerde de algo". Hoy el filtro por tenant protege lo más probable (un handler que olvida filtrar), pero quedan huecos que un tribunal, un auditor o el primer cliente podrían señalar: una entidad nueva sin la marca `ITenantOwned` no tendría filtro y ningún test lo detectaría, y el recorrido real (token → `TenantContext` → filtro) solo se ha probado a mano.

Es la garantía central del producto: un autónomo confía sus clientes a OfiFlow solo si nadie más puede verlos.

## Alcance — Qué SÍ incluye

1. **Clasificación obligatoria de entidades (fail-closed).** Un test sobre el modelo de EF Core exige que **toda** entidad mapeada sea `ITenantOwned` (y tenga su filtro aplicado) o esté en una lista explícita de entidades globales, con su justificación. Una entidad nueva sin clasificar rompe el build.
2. **Coherencia de la marca.** Una entidad con propiedad `TenantId` que no sea `ITenantOwned` solo se admite si está en esa lista (hoy: `RefreshToken`).
3. **Tests de escritura entre empresas** contra SQL Server real (Testcontainers): con el `Id` de un recurso de otra empresa, ningún handler de modificación, borrado, transición de estado o asignación lo encuentra (404 `*.not_found`, igual que si no existiera). Incluye `TenantUser` y la asignación de un trabajo a un `TenantUser` ajeno.
4. **Guardarraíl de lectura de entidades globales:** `Tenants` y `Users` no tienen filtro (por diseño) y `IApplicationDbContext` los expone. Un test de arquitectura prohíbe leerlos fuera de una lista blanca (hoy: solo se escriben en `RegisterCommandHandler`).
5. **Lista blanca de `IgnoreQueryFilters` por ruta relativa**, no por nombre de fichero.
6. **Test de extremo a extremo por HTTP** con dos empresas reales (registro → login → peticiones con el token): la empresa B no ve ni toca los datos de la empresa A, y la respuesta ante un `Id` ajeno es idéntica a la de un `Id` inexistente. Cubre el único tramo hoy sin test automático: el claim del token leído por `TenantContext`.
7. **Corregir el comentario obsoleto** de `ApplicationDbContextTenantFilterTests` (dice que el test contra SQL Server real está "pendiente"; ya existe).

## Alcance — Qué NO incluye

- **NOW** (necesario ahora, pero fuera de esta spec concreta): nada pendiente.
- **NEXT** (siguiente fase):
  - Revalidar en cada petición que el usuario sigue perteneciendo al tenant del token (ADR-002 lo prevé; hoy solo se comprueba al renovar la sesión). Necesita decidir el coste (una consulta por petición o una caché) y probablemente una ADR; solo es explotable cuando exista la operación de quitar usuarios de una empresa.
  - Que el login elija entre varias empresas del mismo usuario (hoy toma la primera, y cada usuario solo tiene una). Llega con invitaciones y selector de empresa.
  - Un rol en `AuthorizationBehavior` (RBAC): spec propia (ADR-009 NEXT).
- **LATER** (interesante, no prioritario): *Row-Level Security* de SQL Server como segunda barrera a nivel de base de datos (defensa en profundidad). Requiere ADR propia.
- **NEVER**: base de datos o esquema por empresa (ADR-002 lo descarta).

## Reglas de negocio / invariantes

- **R1.** Toda entidad mapeada en `ApplicationDbContext` es de una de dos clases, y la clase se declara, no se deduce: *de una empresa* (`ITenantOwned` + filtro) o *global* (en la lista de entidades globales con motivo). Lo que no esté en ninguna falla.
- **R2.** Entidades globales a día de hoy y su motivo:
  - `Tenant`: es la propia empresa.
  - `User`: una persona puede pertenecer a varias empresas (ADR-004).
  - `ApplicationUser`: credenciales de una persona (ADR-007).
  - `RefreshToken`: guarda el `TenantId` de la sesión pero se busca solo por el hash del token (ADR-007).
- **R3.** Ante el `Id` de un recurso de otra empresa, las operaciones responden exactamente igual que ante un `Id` inexistente (mismo código de error y mismo HTTP 404): no se revela que existe (ADR-002).
- **R4.** Las entidades globales con datos de otras empresas (`Tenants`, `Users`) no se leen desde casos de uso salvo los de una lista blanca justificada.
- **R5.** Cada excepción al filtro (`IgnoreQueryFilters`) está en una lista blanca de **rutas**, con motivo.

## Modelo conceptual

No se añade ninguna entidad ni se cambia el esquema de la base de datos. Se añaden tests y guardarraíles; el código de producción solo cambia si algún test descubre un fallo real.

```
Lista de entidades globales (en un test, con motivo)
- Tenant · User · ApplicationUser · RefreshToken

Regla: entidades mapeadas = ITenantOwned (con filtro) ∪ lista de globales
```

## Criterios de aceptación

- Dado el modelo de EF Core actual, cuando se ejecuta el test de clasificación, entonces pasa y cada entidad aparece como *de una empresa* o *global*.
- Dada una entidad nueva con `TenantId` pero sin `ITenantOwned` (simulada en el propio test), cuando se evalúa la regla, entonces el test falla con un mensaje que indica qué entidad y qué hacer.
- Dada una entidad nueva sin `TenantId` y sin estar en la lista de globales, cuando se evalúa la regla, entonces el test falla (obliga a decidir).
- Dado un trabajo de la empresa A, cuando la empresa B intenta modificarlo, empezarlo, completarlo, cancelarlo o asignarlo, entonces recibe `job.not_found` y el trabajo no cambia.
- Dado un cliente de la empresa A, cuando la empresa B intenta modificarlo o borrarlo, entonces recibe `customer.not_found` y el cliente sigue intacto.
- Dado un `TenantUser` de la empresa A, cuando la empresa B asigna un trabajo propio a ese `TenantUser`, entonces recibe `tenant_user.not_found`.
- Dado un `TenantUser` de la empresa A, cuando la empresa B lo consulta, entonces no lo ve.
- Dado un fichero nuevo en `src/` que lee `Users` o `Tenants` fuera de la lista blanca, cuando se ejecutan los tests de arquitectura, entonces fallan.
- Dado un fichero con el mismo nombre que uno de la lista blanca de `IgnoreQueryFilters` pero en otra ruta, cuando se ejecutan los tests de arquitectura, entonces fallan.
- Dadas dos empresas registradas por HTTP, cuando B pide por su `Id` un cliente y un trabajo de A (GET, PUT, DELETE y acciones de estado), entonces recibe 404 con el mismo `code` que para un `Id` inexistente, y A sigue viendo sus datos intactos.
- Dada una petición sin token a esas mismas rutas, entonces recibe 401.

**Caso multi-tenant obligatorio:** es el objeto de esta spec. Los criterios anteriores son la versión ampliada del test de la sección 42 del prompt maestro.

## Seguridad (ADR-009)

- **Datos personales:** los tests usan datos ficticios (nombres y emails inventados con dominio reservado `example.com`). Ningún dato personal real, ninguno en logs.
- **Autorización:** no se añade ningún Command ni Query. No cambia nada.
- **Entrada:** no hay campos nuevos.
- **Aislamiento:** no hay entidades nuevas. Esta spec **añade** guardarraíles al aislamiento. No se añade ningún `IgnoreQueryFilters()`; se endurece su lista blanca.
- **SQL crudo:** ninguno.
- **Abuso:** el test HTTP registra empresas, y `register` está limitado a 3 por hora y por IP. El test se diseña para registrar solo 2 empresas por instancia de la API (el límite vive en memoria de cada instancia, así que no se arrastra entre ejecuciones ni entre clases de test).
- **Eventos de seguridad:** ninguno nuevo.

## Decisiones técnicas relevantes

- Se apoya en: ADR-002 (filtro global por reflexión), ADR-008 (tests contra SQL Server real con Testcontainers), ADR-009 R2.
- **¿Requiere una ADR nueva?** No. Amplía ADR-009 R2 con una nota de revisión ("toda entidad mapeada está clasificada").
- Decisión de diseño: el test de clasificación se hace sobre el **modelo de EF Core** (lo que realmente se mapea) y no sobre un escaneo de código, porque no puede esquivarse cambiando un nombre de variable. Se ejecuta con el proveedor en memoria, sin Docker, así que es rápido.
- Decisión de diseño: el test de extremo a extremo por HTTP necesita una base de datos real, por lo que `OfiFlow.Api.Tests` incorporará Testcontainers (ya usado por `OfiFlow.Infrastructure.Tests`). El coste es un segundo contenedor en CI; se acepta por ser el único test que cubre el token → `TenantContext` → filtro.

## Fuera de alcance / Backlog relacionado

- Revalidación de la pertenencia al tenant por petición (NEXT, probable ADR).
- Elección de empresa en el login con varias pertenencias (NEXT).
- Row-Level Security en SQL Server (LATER, ADR).
- RBAC por rol (`AuthorizationBehavior`), spec propia.

---

## Validación antes de aprobar

> ¿Esta funcionalidad ayuda a conseguir o mantener al primer cliente? (sección 84 del prompt maestro)

Sí. Una fuga de datos entre empresas acabaría con la confianza del primer cliente (y tendría consecuencias legales por RGPD). Esta spec no añade funcionalidad visible, pero convierte en verificable automáticamente la promesa central del producto.
