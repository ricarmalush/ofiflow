# ADR-012 — Autorización por permisos (RBAC)

**Estado:** Propuesto
**Fecha:** 2026-10-06
**Proyecto:** OfiFlow

---

## Contexto

Los roles ya existen: `TenantUser.Role` (`Owner`, `Admin`, `Manager`, `Technician`, `Employee`, ADR-004) y viaja en el token como claim. Pero **no se comprueba en ningún sitio**: cualquier usuario autenticado puede ejecutar cualquier operación de su empresa. Hoy no se nota porque todo usuario que se registra es `Owner` de su empresa y no existen las invitaciones; en cuanto un tenant tenga un segundo usuario, ese usuario tendría los poderes de un `Owner`.

Tres documentos previos dejan este hueco abierto o lo condicionan:

- **ADR-003** prevé un Pipeline Behavior de autorización (no implementado).
- **ADR-004** deja abierto "si se necesitan permisos granulares (`Customers.Read`, `Jobs.Write`...) además del rol, y cómo conviven con `TenantUser.Role`".
- **ADR-009** lo lista como NEXT ("RBAC dentro del tenant").
- La sección 17 del documento maestro exige que la autorización funcione **igual para Angular, MAUI, la API y la IA**, y la sección 16 propone permisos con el formato `Entidad.Acción`.

## Problema

¿Dónde se comprueba la autorización, sobre qué (roles o permisos), de dónde sale el rol del usuario y qué responde el sistema cuando se deniega?

## Opciones consideradas

### Dónde se comprueba

**Opción 1: En los endpoints de ASP.NET (`RequireAuthorization("política")`)**
- Ventajas: mecanismo estándar y visible junto a cada ruta.
- Desventajas: solo protege HTTP. Un cliente que entre por otro camino (la IA, un proceso en segundo plano, una futura app que reutilice Application) se saltaría la comprobación, lo que contradice la sección 17 del documento maestro.

**Opción 2: A mano dentro de cada handler**
- Ventajas: ninguna dependencia nueva.
- Desventajas: depende de que cada autor se acuerde, el mismo riesgo que ADR-002 eliminó para el aislamiento entre empresas.

**Opción 3: Pipeline Behavior en Application, con la declaración en cada Command/Query y un test que falla si falta (fail-closed)**
- Ventajas: protege cualquier punto de entrada; no depende de la memoria del autor; es lo que ya preveía ADR-003.
- Desventajas: un behavior más en cada petición (coste despreciable: no consulta la base de datos).

### Sobre qué se decide

**Opción A: Roles directamente en cada operación (`[Roles(Owner, Admin)]`)**
- Ventajas: sencillo de leer.
- Desventajas: cambiar la política ("el Manager ya puede borrar clientes") obliga a tocar muchas clases y no hay un sitio único donde ver "qué puede hacer cada rol".

**Opción B: Permisos en cada operación (`Customers.Write`) y una tabla única rol → permisos**
- Ventajas: la matriz completa está en un solo fichero, auditable y testeable; evoluciona hacia permisos más finos como pedía el documento maestro; las operaciones no conocen los roles.
- Desventajas: una capa de indirección.

**Opción C: Permisos configurables por empresa, guardados en base de datos**
- Ventajas: máxima flexibilidad.
- Desventajas: sobreingeniería para el tamaño actual (un único desarrollador, sin clientes).

### De dónde sale el rol

**Opción 1: Del token (claim `role`)**: coherente con ADR-002 (el tenant también viaja en el token); coste cero por petición. Desventaja: un cambio de rol o de pertenencia no se nota hasta que el token se renueva (hasta 30 minutos).

**Opción 2: Consultarlo a la base de datos en cada petición**: siempre actual, pero una consulta más por petición.

## Decisión

Se decide **Opción 3 + Opción B + rol del token**, con estas reglas:

### R1 — Permisos y matriz, en Domain
- `Permission` (enumerado en `Domain/Tenancy`) con el formato `Entidad.Acción` del documento maestro, ampliado con las acciones que necesita el producto hoy (por ejemplo `Jobs.Assign`, `Customers.Delete`).
- Una **única tabla** `RolePermissions` (rol → conjunto de permisos). Es la fuente de verdad: ningún código del sistema decide por rol, solo por permiso.
- `Owner` tiene todos los permisos **por la tabla**, no por una excepción en el código.

### R2 — Declaración en cada operación
- Cada Command/Query declara el permiso que exige con un atributo `[RequiresPermission(Permission.X)]`.
- Las operaciones sin sesión (registro, login, renovar) lo declaran explícitamente con `[AllowAnonymousRequest]`.

### R3 — `AuthorizationBehavior`, el primero del pipeline
- Se ejecuta **antes** que la validación: quien no tiene permiso no recibe información sobre la validez de sus datos.
- **Fail-closed:** una operación sin ninguna de las dos declaraciones se deniega. Además, un test de arquitectura falla si aparece una sin declarar.

### R4 — Usuario actual
- `ICurrentUser` (Application) con el `UserId` y el `Role`, implementado en Infrastructure leyendo los claims del token ya validado. Si el rol falta o no es un valor válido, se deniega.

### R5 — Respuesta y trazabilidad
- Denegado → **403** con `code` `auth.forbidden` (ProblemDetails, igual que el resto de errores, ADR-011). El permiso se comprueba **antes** de tocar datos, y depende solo del rol: no revela nada sobre recursos de otras empresas (el aislamiento por tenant sigue actuando después).
- Cada denegación genera un evento de seguridad (`1301`, catálogo de ADR-009 R5) con `UserId`, `TenantId`, el rol y el nombre de la operación. Nunca datos personales.

## Consecuencias

### Positivas
- La matriz de permisos se lee, se revisa y se prueba en un solo sitio.
- La autorización se aplica igual venga la petición de donde venga (HTTP, IA, otro cliente).
- Olvidar protegerla en una operación nueva es imposible: el build falla.

### Negativas / riesgos aceptados
- **El rol puede estar desfasado hasta que caduque el token (30 minutos).** Si a alguien se le baja de rol, seguirá con el anterior hasta renovar. Se acepta mientras no exista la operación de cambiar roles; se mitiga con la revalidación por petición ya anotada como NEXT en ADR-009.
- **No cubre "solo lo que tengo asignado".** Un `Technician` con `Jobs.Execute` puede iniciar o completar cualquier trabajo de su empresa, no solo los suyos. Se acepta de forma explícita y temporal; se cierra con una spec propia junto con la asignación de técnicos (Fase 2).
- La tabla de permisos va compilada: cambiarla exige un despliegue. Se acepta (Opción C descartada por ahora).

### Qué queda cerrado (no se debe reabrir sin una razón nueva y explícita)
- No decidir autorización por rol directamente en el código de las operaciones: siempre por permiso.
- No comprobar la autorización solo en los endpoints.

### Qué queda abierto para revisar más adelante
- **NEXT:** restricción por recurso ("el técnico solo ve y avanza los trabajos que tiene asignados"), que necesita conocer el `TenantUser` del usuario actual.
- **NEXT:** revalidar rol y pertenencia en cada petición (ver ADR-009).
- **NEXT:** el permiso `Users.Manage` cuando exista la gestión de usuarios e invitaciones.
- **LATER:** permisos configurables por empresa (Opción C).

---

## Relación con otros ADR

- Implementa: el NEXT "RBAC dentro del tenant" de ADR-009 y el `AuthorizationBehavior` previsto en ADR-003.
- Responde: el punto abierto de ADR-004 sobre permisos granulares.
- Depende de: ADR-002 (el tenant y el rol viajan en el token), ADR-011 (el 403 usa el contrato de errores con código).
- Afecta a: ADR-009 (nuevo evento de seguridad 1301 en el catálogo de R5).
