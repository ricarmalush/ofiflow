# Spec — 008-rbac-authorization

**Estado:** Implementada (2026-10-06)
**Fecha:** 2026-10-06
**Bounded Context:** Tenancy (transversal: Domain, Application, Infrastructure y Api)
**Depende de:** ADR-012 (autorización por permisos), ADR-003 (CQRS y behaviors), ADR-004 (roles en `TenantUser`), ADR-009 (seguridad base, evento 1301), ADR-011 (errores con código), spec 007 (aislamiento entre empresas)

---

## Objetivo

Que el rol de cada usuario **se compruebe de verdad**. Hoy los roles (`Owner`, `Admin`, `Manager`, `Technician`, `Employee`) se guardan y viajan en el token, pero ninguna operación los mira: cualquier usuario autenticado puede hacerlo todo, incluido borrar clientes.

Para una empresa con un solo dueño no se nota. En cuanto el dueño dé acceso a un técnico o a una persona de oficina, esa persona tendría sus mismos poderes. Esta spec cierra ese hueco **antes** de que existan las invitaciones y los técnicos reales (Fase 2).

## Alcance — Qué SÍ incluye

1. **Permisos y matriz rol → permisos** en Domain, en un único sitio (ADR-012 R1).
2. **Declaración del permiso en cada operación** (Command/Query) con un atributo, y marca explícita para las operaciones sin sesión (registro, login, renovar).
3. **`AuthorizationBehavior`**, el primero del pipeline de MediatR, que deniega si el rol no tiene el permiso.
4. **Usuario actual** (`ICurrentUser`): el `UserId` y el rol, leídos del token ya validado.
5. **Respuesta 403** con `code` `auth.forbidden` (contrato de errores de ADR-011), con su mensaje en el diccionario.
6. **Evento de seguridad `1301`** en cada denegación, sin datos personales.
7. **Guardarraíl fail-closed:** un test de arquitectura que falla si existe una operación sin declarar, y el propio behavior deniega las no declaradas.
8. **Tests de extremo a extremo por HTTP** con usuarios de cada rol sembrados directamente en la base de datos de prueba (todavía no hay forma de crearlos por la API).

## Alcance — Qué NO incluye

- **NOW** (necesario ahora, pero fuera de esta spec concreta): nada pendiente.
- **NEXT** (siguiente fase):
  - **Invitar y gestionar usuarios** de una empresa (crear usuarios que no sean `Owner`, cambiar roles, quitarlos). Es lo que hará *visible* este control; con esta spec solo se puede ejercer con datos sembrados. Spec propia, y es el siguiente paso natural.
  - **Restricción por recurso:** que un técnico solo vea y avance **los trabajos que tiene asignados**. Necesita conocer el `TenantUser` del usuario actual. Spec propia, junto con la asignación de técnicos de la Fase 2.
  - **Revalidar rol y pertenencia en cada petición** (ver ADR-009).
  - El permiso `Users.Manage` (y `Settings.Manage`) cuando existan esas funciones.
- **LATER** (interesante, no prioritario): permisos configurables por empresa, guardados en base de datos.
- **NEVER**: comprobar la autorización solo en los endpoints (rompería el requisito de la sección 17 del documento maestro).

## Reglas de negocio / invariantes

- **R1.** Toda operación exige un permiso concreto o está marcada explícitamente como anónima. **Una operación sin declarar se deniega.**
- **R2.** Las decisiones se toman por **permiso**, nunca por rol dentro del código de las operaciones. El único sitio donde aparecen los roles es la tabla rol → permisos.
- **R3.** `Owner` tiene todos los permisos por la tabla, sin excepciones en el código.
- **R4.** Un usuario sin rol válido en el token no puede ejecutar ninguna operación autenticada.
- **R5.** La comprobación ocurre **antes** de la validación de datos y de cualquier acceso a la base de datos.
- **R6.** Una denegación nunca revela nada sobre recursos de otras empresas: depende solo del rol del usuario, no de qué `Id` pida.

## Matriz de permisos propuesta (para que la revises)

**Permisos** (formato `Entidad.Acción` del documento maestro, ampliado con lo que el producto necesita hoy):

| Permiso | Operaciones que cubre |
|---|---|
| `Customers.Read` | Ver un cliente, listar clientes |
| `Customers.Write` | Crear y modificar clientes |
| `Customers.Delete` | Eliminar clientes (borrado físico, irreversible: ADR-006) |
| `Jobs.Read` | Ver un trabajo, listar trabajos |
| `Jobs.Write` | Crear y modificar trabajos |
| `Jobs.Assign` | Asignar un trabajo a un `TenantUser` |
| `Jobs.Cancel` | Cancelar un trabajo |
| `Jobs.Execute` | Iniciar y completar un trabajo |

**Qué puede cada rol:**

| Permiso | Owner | Admin | Manager | Employee | Technician |
|---|:-:|:-:|:-:|:-:|:-:|
| `Customers.Read` | ✅ | ✅ | ✅ | ✅ | ✅ |
| `Customers.Write` | ✅ | ✅ | ✅ | ✅ | ❌ |
| `Customers.Delete` | ✅ | ✅ | ❌ | ❌ | ❌ |
| `Jobs.Read` | ✅ | ✅ | ✅ | ✅ | ✅ |
| `Jobs.Write` | ✅ | ✅ | ✅ | ✅ | ❌ |
| `Jobs.Assign` | ✅ | ✅ | ✅ | ❌ | ❌ |
| `Jobs.Cancel` | ✅ | ✅ | ✅ | ❌ | ❌ |
| `Jobs.Execute` | ✅ | ✅ | ✅ | ❌ | ✅ |

**Operaciones y su permiso (16 en total):**

| Operación | Permiso |
|---|---|
| `GetCustomerQuery`, `GetCustomersQuery` | `Customers.Read` |
| `CreateCustomerCommand`, `UpdateCustomerCommand` | `Customers.Write` |
| `DeleteCustomerCommand` | `Customers.Delete` |
| `GetJobQuery`, `GetJobsQuery` | `Jobs.Read` |
| `CreateJobCommand`, `UpdateJobCommand` | `Jobs.Write` |
| `AssignJobCommand` | `Jobs.Assign` |
| `CancelJobCommand` | `Jobs.Cancel` |
| `StartJobCommand`, `CompleteJobCommand` | `Jobs.Execute` |
| `RegisterCommand`, `LoginCommand`, `RefreshTokenCommand` | *(anónimas)* |

**Criterios detrás de la propuesta** (para que puedas contradecirlos):

- **Mínimo privilegio:** cada rol recibe solo lo que necesita para su trabajo.
- **Lo destructivo e irreversible se reserva** a `Owner` y `Admin` (borrar clientes, que es borrado físico).
- `Owner` y `Admin` son hoy idénticos. Se diferenciarán cuando existan funciones que solo debe tener el dueño (facturación, gestión de usuarios).
- `Manager` gestiona el trabajo diario (clientes, trabajos, asignar, cancelar, ejecutar) pero no borra clientes.
- `Employee` es el personal de oficina: da de alta y modifica clientes y trabajos, pero no asigna, ni cancela, ni ejecuta.
- `Technician` ve clientes y trabajos y los **ejecuta** (iniciar y completar), pero no crea ni modifica.

**Limitación conocida de esta matriz** (riesgo aceptado, ADR-012): un `Technician` puede ver todos los clientes y todos los trabajos de su empresa, e iniciar o completar cualquiera, no solo los asignados a él. Se cierra con la restricción por recurso (NEXT).

## Modelo conceptual

No se añade ninguna entidad ni se cambia el esquema de la base de datos.

```
Domain/Tenancy
- Permission              (enumerado)
- RolePermissions         (tabla única: TenantRole → permisos)

Application
- RequiresPermissionAttribute · AllowAnonymousRequestAttribute
- ICurrentUser            (UserId, Role)
- AuthorizationBehavior   (primero del pipeline)
- ForbiddenException      (código auth.forbidden)

Infrastructure
- CurrentUser             (lee los claims del token ya validado)
```

## Criterios de aceptación

- Dado el contenido de `RolePermissions`, cuando se ejecuta el test de matriz, entonces coincide exactamente con la tabla de arriba (cada celda).
- Dado un usuario con el rol `Technician`, cuando intenta crear un cliente, entonces recibe **403** con `code` `auth.forbidden` y el cliente no se crea.
- Dado un usuario con el rol `Technician`, cuando consulta clientes o trabajos o inicia un trabajo, entonces la operación se ejecuta con normalidad.
- Dado un usuario `Employee`, cuando intenta asignar, cancelar, iniciar o completar un trabajo, entonces recibe 403.
- Dado un usuario `Manager`, cuando intenta eliminar un cliente, entonces recibe 403; y cuando lo modifica o asigna un trabajo, entonces funciona.
- Dado un `Owner` y un `Admin`, cuando ejecutan cualquiera de las 13 operaciones autenticadas, entonces ninguna es denegada.
- Dada una operación sin `[RequiresPermission]` ni `[AllowAnonymousRequest]`, cuando se ejecuta el behavior, entonces se deniega; y cuando se ejecutan los tests de arquitectura, entonces falla (con el nombre de la operación).
- Dada una petición con un token sin claim de rol, o con un rol que no existe, cuando ejecuta una operación autenticada, entonces recibe 403.
- Dado un usuario sin permiso con datos que el validador rechazaría (por ejemplo, un título vacío), cuando ejecuta la operación, entonces recibe 403 y **no** un 400 de validación (la autorización va primero). Un JSON mal formado sigue dando 400 en el endpoint, antes de llegar a Application.
- Dado un usuario sin permiso que pide el `Id` de un recurso de **otra** empresa y otro que pide uno inexistente, cuando se ejecuta la operación, entonces ambos reciben el mismo 403 (R6).
- Dada cualquier denegación, entonces se registra el evento `1301` con `UserId`, `TenantId`, rol y nombre de la operación, y sin ningún dato personal.
- Dadas las operaciones anónimas (registro, login, renovar), cuando se ejecutan sin token, entonces funcionan como hasta ahora.
- Dado el aislamiento entre empresas (spec 007), entonces todos sus tests siguen pasando sin cambios.

**Caso multi-tenant obligatorio:** un usuario de la empresa A con permiso para una operación nunca puede ejecutarla sobre datos de la empresa B (lo garantiza el filtro de tenant, que sigue actuando después de la autorización); y un usuario de B con rol `Owner` no hereda nada de un rol que tenga en A (el rol viaja en el token de cada empresa).

## Seguridad (ADR-009)

- **Datos personales:** no se guardan nuevos. El evento `1301` registra solo Ids, el rol y el nombre de la operación. Nunca el cuerpo de la petición ni datos del cliente.
- **Autorización:** es el objeto de la spec. Matriz arriba, a confirmar.
- **Entrada:** no hay campos nuevos.
- **Aislamiento:** no hay entidades nuevas. No se añade ningún `IgnoreQueryFilters()`.
- **SQL crudo:** ninguno.
- **Abuso:** no se expone ningún endpoint nuevo. La comprobación no consulta la base de datos, así que no es una vía de agotamiento de recursos.
- **Eventos de seguridad:** sí: `1301` (acceso denegado). Un analista querría ver, por ejemplo, un técnico intentando borrar clientes repetidamente.
- **Riesgo técnico a verificar:** el nombre del claim del rol dentro del token. `TokenService` lo escribe como `ClaimTypes.Role` y la librería de JWT lo acorta a `role`; al leerlo hay que usar el nombre real. Lo cubren los tests de extremo a extremo con un token real.

## Decisiones técnicas relevantes

- Se apoya en: ADR-003, ADR-004, ADR-002, ADR-009, ADR-011 y la sección 17 del documento maestro.
- **¿Requiere una ADR nueva?** Sí: **ADR-012 (Propuesto)**, redactada junto a esta spec. Recoge dónde se comprueba, sobre qué (permisos y no roles), de dónde sale el rol y qué se responde.

## Fuera de alcance / Backlog relacionado

- Invitar y gestionar usuarios (spec siguiente natural).
- Restricción por recurso para técnicos (con la Fase 2).
- Revalidación de rol y pertenencia por petición.
- Permisos configurables por empresa.

---

## Validación antes de aprobar

> ¿Esta funcionalidad ayuda a conseguir o mantener al primer cliente? (sección 84 del prompt maestro)

**De forma indirecta, y conviene decirlo con honestidad.** Por sí sola no añade nada que un cliente vea: hoy todos los usuarios son `Owner`. Su valor es de **requisito previo**: ningún autónomo con un empleado o un técnico puede usar OfiFlow si dar acceso al empleado significa darle control total, incluido borrar clientes. Esta spec evita que la primera empresa con dos usuarios nazca con ese agujero, y es la base de la Fase 2 (asignación de técnicos). Mientras no existan las invitaciones, su efecto solo es verificable con datos sembrados en los tests.
