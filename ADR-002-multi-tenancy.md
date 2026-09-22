# ADR-002 — Multi-Tenancy

**Estado:** Aceptado
**Fecha:** 2027-01 (ajustar a la fecha exacta en que se cierre)
**Proyecto:** OfiFlow

---

## Contexto

OfiFlow es multi-tenant desde el primer commit. Un `User` puede pertenecer a varios `Tenant` con un rol distinto en cada uno (`TenantUser.Role`, decidido en ADR-004). El aislamiento entre tenants debe ser obligatorio: un usuario de Tenant A nunca puede acceder a datos de Tenant B, ni siquiera por error de implementación.

## Problema

¿Qué estrategia de almacenamiento multi-tenant usar, cómo garantizar que ninguna consulta se olvide de filtrar por tenant, y cómo sabe la API en qué tenant está operando el usuario en cada request?

## Opciones consideradas

### Estrategia de almacenamiento

**Opción 1: Base de datos separada por tenant**
- Ventajas: aislamiento total a nivel de infraestructura.
- Desventajas: coste operativo alto (una BD por cliente) desproporcionado para el tamaño actual del proyecto; migraciones y mantenimiento se multiplican por cada tenant.

**Opción 2: Esquema separado por tenant en la misma base de datos**
- Ventajas: aislamiento a nivel de esquema sin multiplicar bases de datos completas.
- Desventajas: sigue añadiendo complejidad operativa (migraciones por esquema) sin necesidad real en esta fase.

**Opción 3: Una única base de datos con TenantId en las entidades**
- Ventajas: máxima simplicidad operativa; una sola base de datos, un solo conjunto de migraciones; adecuado para el volumen de clientes esperado en el MVP y primeros años.
- Desventajas: el aislamiento depende enteramente de que el filtro por `TenantId` se aplique correctamente en cada consulta — es responsabilidad del código, no de la infraestructura.

### Filtro por TenantId en las consultas

**Opción 1: Global Query Filter de EF Core**
- Ventajas: se configura una vez en el `DbContext` y se aplica automáticamente a toda query sobre esa entidad; elimina el riesgo de que un Handler nuevo olvide filtrar por tenant.
- Desventajas: hay que tener cuidado con operaciones que deliberadamente necesiten ignorar el filtro (raras, pero deben marcarse explícitamente con `IgnoreQueryFilters()`).

**Opción 2: Revisión manual del TenantId en cada Handler**
- Ventajas: control explícito en cada punto.
- Desventajas: depende de que el desarrollador se acuerde de añadirlo en cada Handler nuevo — el propio riesgo que este ADR busca eliminar.

### Tenant activo en cada request

**Opción 1: Tenant activo elegido tras login, guardado en el JWT**
- Ventajas: simple de razonar; el cliente no puede manipular en qué tenant opera sin pasar por un nuevo login/emisión de token; funciona igual para Angular, MAUI e IA/WhatsApp.
- Desventajas: cambiar de tenant activo exige emitir un nuevo JWT (aceptable: no es una operación frecuente).

**Opción 2: Cabecera propia por request (X-Tenant-Id)**
- Ventajas: cambio de tenant inmediato sin nuevo login.
- Desventajas: el valor lo envía el cliente en cada request, lo que obliga a validar en cada llamada que el usuario realmente pertenece a ese tenant — más superficie de error que confiar en el JWT ya validado.

## Decisión

Se decide:
- Estrategia de almacenamiento: **una única base de datos SQL Server con `TenantId` en las entidades de negocio**.
- Filtro de aislamiento: **Global Query Filter de EF Core**, configurado en el `DbContext` para todas las entidades multi-tenant.
- Tenant activo: **elegido tras el login y guardado en el JWT**; cambiar de tenant activo requiere una nueva emisión de token.
- El backend siempre verifica que el usuario autenticado pertenece realmente al tenant indicado en el JWT antes de ejecutar cualquier Command o Query — el `TenantId` nunca se acepta como dato de entrada sin validar.

## Consecuencias

### Positivas
- El riesgo más peligroso del proyecto (una query sin filtrar por tenant) queda mitigado a nivel de infraestructura de EF Core, no depende de que el desarrollador se acuerde caso por caso.
- Simplicidad operativa: una sola base de datos, un solo conjunto de migraciones.
- El tenant activo no puede ser manipulado libremente por el cliente en cada request.

### Negativas / riesgos aceptados
- Cualquier operación que necesite deliberadamente cruzar tenants (poco frecuente, pero puede existir para soporte/administración interna) debe usar `IgnoreQueryFilters()` de forma explícita y auditada — nunca por defecto.
- Cambiar de tenant activo exige un nuevo JWT, lo que añade una llamada extra en ese flujo concreto.

### Qué queda cerrado (no se debe reabrir sin una razón nueva y explícita)
- No usar base de datos ni esquema separado por tenant mientras el volumen de clientes no lo justifique.
- No aceptar el `TenantId` como dato de entrada sin validar contra el usuario autenticado.

### Qué queda abierto para revisar más adelante
- Mecanismo y proceso de auditoría para los casos excepcionales que usen `IgnoreQueryFilters()`.
- Revisar si el aislamiento por `TenantId` sigue siendo suficiente si algún cliente grande exige garantías contractuales de aislamiento físico (esto empujaría de nuevo hacia base de datos separada, pero solo si aparece esa necesidad real).

---

## Relación con otros ADR

- Depende de: ADR-004 (Identity) — usa el modelo `User / Tenant / TenantUser` y el JWT ya decididos allí.
- Depende de: ADR-005 (Modular Monolith) — el `TenantContext` y el Global Query Filter viven dentro de la misma estructura de Modular Monolith.
- Afecta a: ADR-003 (CQRS) — Commands y Queries deben recibir el `TenantContext` de forma consistente.
