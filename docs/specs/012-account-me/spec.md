# Spec — 012-account-me

**Estado:** Implementada (2026-10-27)
**Fecha:** 2026-10-27
**Bounded Context:** Identity (la cuenta de quien consulta) con una lectura de Tenancy (su empresa y su pertenencia)
**Depende de:** ADR-012 (autorización por permisos), ADR-013 (rol y pertenencia revalidados en cada petición), ADR-002 (aislamiento entre empresas), ADR-009 (seguridad base), ADR-016 (el operador de la plataforma es otro mundo)

---

## Objetivo

Que quien tiene una sesión pueda preguntarle a la API **quién es, en qué empresa está y qué puede hacer**, con la respuesta que la API da **ahora** y no la que dijo el token al emitirse. Hoy el frontend lo deduce decodificando el JWT y copiando a mano la tabla de permisos (`RolePermissions`), con tres defectos que esta spec cierra:

1. **El rol del token puede estar desfasado hasta 30 minutos.** La API ya revalida el rol en la base de datos en cada petición (spec 009 R16), pero el frontend sigue pintando con el del token: tras un cambio de rol ve botones de más o de menos.
2. **La copia de los permisos puede desincronizarse** de la tabla real. Lo peor que pasa es un botón de más (que da 403) o de menos, pero es una duplicación que nadie vigila.
3. **Un técnico no puede saber qué trabajos son «suyos».** El `sub` del token es el Id de su cuenta, no el de su pertenencia (`TenantUser`), que es el que lleva `Job.AssignedTenantUserId`; y sin `Users.Read` no puede listar el equipo para averiguarlo.

## Alcance — Qué SÍ incluye

1. **`GET /api/v1/account/me`**: los datos de la propia cuenta y empresa, el rol **efectivo** (el de la base de datos) y la lista de permisos de ese rol.
2. Un **permiso nuevo, `Account.Read`**, que tienen los cinco roles (no toca datos de la empresa: solo dice a cada persona lo que ya es suyo).
3. El **nombre estable de cada permiso** (`Customers.Read`, `Jobs.Assign`...) en un solo sitio, para que la API y el frontend hablen igual sin que el frontend tenga que copiar la tabla.
4. Que la respuesta **no se guarde en cachés** y que **solo la pida una sesión de empresa**.

## Alcance — Qué NO incluye

- **NOW** (fuera de esta spec concreta): quitar la copia de `RolePermissions` del frontend y usar `/me` allí. Es el siguiente paso, en el repositorio del frontend; esta spec deja la API lista.
- **NEXT:** editar el perfil (cambiar nombre o contraseña), el estado del segundo factor dentro de `/me` (ya hay `GET /account/mfa`), más de una empresa por cuenta (ADR-004; hoy cada cuenta es de una sola).
- **NEVER:** que `/me` devuelva algo de **otra** persona o de otra empresa, secretos de cualquier tipo (hash de contraseña, secreto TOTP, códigos de recuperación, tokens), o que acepte un Id por la petición.

## Reglas de negocio / invariantes

- **R1.** `/me` habla **solo de quien lo pide**: la cuenta sale del token validado, nunca de la ruta, la consulta o el cuerpo. No tiene parámetros.
- **R2.** El **rol y los permisos** son los de la base de datos en esa petición (la verificación que ya hace el `AuthorizationBehavior`), **no** los del token. Si el rol cambió después de emitir el token, `/me` ya dice el nuevo.
- **R3.** Los **permisos** son exactamente `RolePermissions.For(rol)`: no hay una segunda lista. Cada uno se nombra `Entidad.Acción` (`Customers.Read`), el mismo formato del documento maestro.
- **R4.** Se devuelve la **pertenencia** (`MembershipId`, el Id del `TenantUser`): es el que se usa para asignar trabajos y el que lleva `Job.AssignedTenantUserId`.
- **R5.** La persona y la empresa se leen **a través de la pertenencia filtrada por la empresa del token**: ninguna consulta puede devolver una empresa ni una ficha que no sea la de esa pertenencia.
- **R6.** Una empresa **suspendida** no llega aquí: la comprobación de pertenencia de cada petición ya responde `403 tenant.suspended` (spec 011 R15d). Un token de **plataforma** no vale (otro esquema: `401`).
- **R7.** La respuesta lleva `Cache-Control: no-store`: es personal.

## Modelo conceptual

```
Domain/Tenancy
- Permission         + AccountRead  (Account.Read: ver los datos de la propia cuenta, su empresa y su rol)
- RolePermissions    + AccountRead para los cinco roles
- PermissionNames    nombre estable de cada permiso: "Customers.Read"... (único sitio)

Application/Identity/Queries/GetCurrentAccount
- GetCurrentAccountQuery   [RequiresPermission(Permission.AccountRead)]   → CurrentAccountDto
- CurrentAccountDto(UserId, MembershipId, Name, Email, TenantId, TenantName, Role, Permissions)

Api
- GET /api/v1/account/me   (RequireAuthorization, Cache-Control: no-store)
```

Operaciones tras esta spec: **36** (26 con permiso, 7 anónimas y 3 de plataforma).

## Criterios de aceptación

- Dado un miembro con sesión, cuando pide `/me`, entonces recibe su nombre, su email, su empresa (Id y nombre), el Id de su pertenencia, su rol y los permisos de ese rol, y **ningún** secreto.
- Dado un miembro al que un gestor **cambió el rol** después de emitirse su token, cuando pide `/me` con ese mismo token, entonces recibe el **rol nuevo** y sus permisos, no los del token.
- Dado un `Technician` (sin `Users.Read`), cuando pide `/me`, entonces recibe su `MembershipId`, y ese Id coincide con el `assignedTenantUserId` de un trabajo que se le asignó.
- Dadas **dos empresas A y B**, cuando un miembro de A pide `/me`, entonces recibe la empresa A y nunca nada de B (y lo mismo al revés).
- Dados los cinco roles, cuando piden `/me`, entonces todos reciben `200` y la lista de permisos coincide con `RolePermissions` (la fuente única).
- Dado un token de **empresa suspendida**, entonces `403` con `tenant.suspended`; dado un token de **plataforma**, un token sin firma válida o sin token, entonces `401`.
- Dada una petición con un `userId`, `tenantId` o cualquier parámetro, entonces se **ignora**: la respuesta es la de quien lo pide.
- La respuesta lleva `Cache-Control: no-store`.

**Guardarraíles**
- Dado un permiso nuevo que ningún rol tiene, o un nombre de permiso sin el formato `Entidad.Acción` o repetido, entonces falla un test.
- Dada una lectura nueva de `Tenants` o de `Users` fuera de la lista blanca, entonces falla el test de arquitectura (el handler entra con su motivo).

## Seguridad (ADR-009)

- **Datos personales:** devuelve el email y el nombre **de quien lo pide**, nada más. No se registra en el log (es una lectura de uno mismo; no lleva evento propio).
- **Autorización:** `[RequiresPermission(Permission.AccountRead)]`, fail-closed como todas las operaciones; el test de la tabla de permisos y el de arquitectura lo cubren.
- **Aislamiento:** la consulta parte de `TenantUsers` (con el filtro de empresa) y llega a `Users` y `Tenants` solo desde esa fila. Se añade el handler a las listas blancas de lectura de `Users` y de `Tenants`, con su motivo.
- **Entrada:** ninguna. No hay Id que validar ni mass assignment posible.
- **Abuso:** una lectura barata de una fila, tras la autenticación; sin límite propio (como el resto de lecturas autenticadas, límite conocido del ADR-009).
- **Lo que esta spec NO protege:** el rol y los permisos que devuelve son **informativos para pintar la interfaz**; quien decide es siempre la API en cada operación. Un cliente que los ignore o los falsifique solo consigue botones que dan 403.

## Decisiones técnicas relevantes

- **Un permiso propio (`Account.Read`) y no reutilizar `Mfa.Manage`.** Los permisos dicen *qué se hace*; que cinco roles tengan los dos no los hace lo mismo, y atar «ver quién soy» a «gestionar el segundo factor» confundiría a quien lea la tabla dentro de un año. Coste: una fila más en la tabla y en su test.
- **El rol de la base de datos, no el del token** (R2). Es lo que la API ya usa para decidir; así `/me` y el `403` no pueden contradecirse.
- **Nombres de permiso derivados del enum** (`CustomersRead` → `Customers.Read`) y no una tabla de textos a mano: un permiso nuevo no puede quedarse sin nombre ni con otro formato (un test lo comprueba para todos).
- **`MembershipId` en lugar de un endpoint aparte** para «mis trabajos»: una línea más en una respuesta que ya existe, sin ruta nueva ni superficie nueva.
- No requiere ADR nueva: amplía ADR-012 (un permiso más) sin cambiar la decisión. Se anota en su «Relación con otros ADR».

## Fuera de alcance / Backlog relacionado

- Frontend: usar `/me` en lugar de decodificar el token y de copiar `RolePermissions` (repositorio `ofiflow-frontend`).
- Que el token deje de llevar el rol (hoy lo lleva por compatibilidad y para el menú inicial).
- `logout` en el servidor y «desasignar» un trabajo: otros huecos conocidos de la API.

---

## Validación antes de aprobar

> ¿Esta funcionalidad ayuda a conseguir o mantener al primer cliente? (sección 84 del prompt maestro)

**Sí, de forma directa.** Un cliente al que le cambian el rol y sigue viendo botones que ya no puede usar, o un técnico que no ve «sus» trabajos, nota un producto a medio hacer. Es pequeña (una ruta, una consulta), no añade superficie de ataque (solo se habla de uno mismo) y retira una duplicación frágil entre el frontend y la API.
