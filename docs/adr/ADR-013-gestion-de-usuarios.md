# ADR-013 — Gestión de usuarios de una empresa

**Estado:** Aceptado (2026-10-15)
**Fecha:** 2026-10-14
**Proyecto:** OfiFlow

---

## Contexto

Los cinco roles existen y se comprueban (ADR-012), pero solo se puede crear el `Owner`, al registrar la empresa. No hay forma de dar acceso a un empleado ni de quitárselo. Hay que decidir cuatro cosas que la spec 009 necesita y que condicionan la seguridad:

1. **Cómo entra una persona nueva** cuando no hay servicio de email.
2. **Quién puede gestionar a quién**, para que gestionar usuarios no sea un camino de escalada de privilegios.
3. **Cuándo surte efecto** un cambio de rol o una baja, dado que el token vive 30 minutos.
4. **Qué pasa con los datos** de quien se quita.

## Problema

Cada decisión tiene una opción cómoda que es insegura: crear al usuario con una contraseña que conoce otro, tratar a `Admin` como `Owner`, dejar que el token caduque solo, o desactivar en lugar de borrar.

## Opciones consideradas

### 1. Cómo entra una persona nueva

**A. El invitante crea el usuario con una contraseña temporal.** Simple, pero el invitante conoce la contraseña de otra persona; no hay forma de demostrar quién hizo qué. Descartada.

**B. Invitación con token de un solo uso; el invitado elige su contraseña.** El token se guarda hasheado, caduca y se consume. Sin servicio de email, el token se devuelve al invitante, que se lo pasa a la persona. Cuando exista el email, el token deja de devolverse y se envía.
- Desventaja: el invitante ve el token y podría usarlo él mismo. Es de confianza dentro de su empresa (ya puede ver y cambiar todos sus datos), y deja rastro (evento de aceptación con el `UserId` resultante).

**C. Invitación solo por email.** La correcta a largo plazo, pero exige un proveedor de correo y secretos nuevos antes de poder probar nada. Se deja como NEXT con un puerto previsto.

### 2. Quién gestiona a quién

**A. Quien tenga `Users.Manage` gestiona a cualquiera salvo al `Owner`.** Un `Admin` comprometido puede ascender a cómplices a `Admin`.

**B. Tabla única "qué roles puede gestionar cada rol", además del permiso.** El `Owner` gestiona a todos menos a sí mismo; el `Admin` gestiona `Manager`, `Technician` y `Employee`. Cambiar un rol exige que el rol actual **y** el nuevo estén permitidos. Los roles aparecen solo en tablas de Domain, como en ADR-012 R2. Nadie se cambia a sí mismo.

### 3. Cuándo surte efecto un cambio

**A. Esperar a que el token caduque** (hasta 30 minutos). Barato. Un empleado despedido sigue con acceso a los datos de los clientes.

**B. Revalidar la pertenencia y el rol en cada petición.** Una consulta indexada más por petición; el rol del token deja de importar.

**C. Lista de tokens revocados o versión de sesión.** Más pieza móvil (caché o tabla de revocación) para un problema que la opción B ya resuelve con lo que existe.

### 4. Qué pasa con los datos de quien se quita

**A. Desactivar** (conservar todo con una marca). Mantiene datos personales de alguien que ya no es parte de la empresa y bloquea su email.

**B. Borrar** su pertenencia, credenciales y datos personales; desasignar sus trabajos no terminados y conservar la referencia en los terminados (historial). Coherente con la minimización de datos y con el borrado físico ya usado para clientes (ADR-006).

## Decisión (propuesta)

- **Invitación con token de un solo uso (1.B).** 256 bits aleatorios, solo se guarda el hash SHA-256, caduca a los 7 días, se revoca, se consume en una transacción con control de concurrencia. Todos los fallos de aceptación devuelven la misma respuesta. El invitado fija su contraseña; el invitante fija el email y el rol. Una empresa por cuenta de momento.
- **Jerarquía como tabla (2.B)** `ManageableRoles` en Domain, con test de matriz celda a celda. `Owner` es único, no se concede ni se toca. Nadie se gestiona a sí mismo.
- **Revalidación por petición (3.B).** El `AuthorizationBehavior` consulta la pertenencia por `(TenantId, UserId)` y usa el rol de la base de datos. El del token queda como dato informativo.
- **Quitar es borrar (4.B)**, con revocación de refresh tokens en la misma transacción.
- **Permisos nuevos** `Users.Read` (Owner, Admin, Manager) y `Users.Manage` (Owner, Admin).

## Consecuencias

### Positivas
- Ninguna persona conoce la contraseña de otra.
- Un `Admin` comprometido no puede ascender a cómplices ni tocar al dueño.
- Despedir a alguien o degradarlo surte efecto en la siguiente petición.
- Se cierran dos riesgos aceptados de ADR-009 y ADR-012 (rol y pertenencia desfasados).
- Se guardan menos datos personales.

### Negativas / riesgos aceptados
- **Una consulta más por petición autenticada.** Por el índice único `(TenantId, UserId)` es barata; se mide cuando haya carga real y, si hace falta, se cachea unos segundos (con la contrapartida de ese retraso).
- **El invitante conoce el token** hasta que exista el email (opción 1.B).
- **Invitar revela si un email tiene cuenta** al `Owner`/`Admin` autenticado. Mismo riesgo que ya tiene el registro.
- **Sin multi-empresa:** un email con cuenta no se puede invitar a otra empresa.
- **Borrar es irreversible:** una baja por error obliga a volver a invitar. Se acepta a cambio de no retener datos.
- La revalidación cambia una propiedad que ADR-012 daba por buena ("la autorización no consulta la base de datos"); sus tests se actualizan de forma explícita.

### Qué queda cerrado (no se debe reabrir sin una razón nueva y explícita)
- Nunca se guarda un token de invitación en claro.
- El invitante nunca fija ni conoce la contraseña de otra persona.
- `Owner` no se concede, no se cambia y no se quita por esta vía.
- Nadie gestiona su propio rol.
- La autorización usa el rol de la base de datos, no el del token.

### Qué queda abierto para revisar más adelante
- **NEXT:** envío del enlace por email (puerto previsto, proveedor por decidir).
- **NEXT:** pertenecer a varias empresas y elegir empresa al hacer login.
- **NEXT:** traspasar la propiedad y abandonar la empresa por voluntad propia.
- **NEXT:** recuperar contraseña, confirmar email, bloqueo por intentos (ADR-009).
- **NEXT:** purga de invitaciones antiguas (guardan el email).
- **LATER:** caché de la pertenencia si la consulta por petición llega a medirse como cuello de botella.

---

## Nota de estado (2026-10-15)

Implementada por la spec 009 (bloques 1 a 7), con 770 tests en verde. Lo que la implementación aclaró y que conviene tener presente:

- **La revalidación vive en `ICurrentUser`, no en un puerto aparte.** `VerifyMembershipAsync` hace la consulta (una sola vez por petición, por el índice único `(empresa, usuario)`) y fija `Role`, que pasa a significar el rol **efectivo** y es **nulo hasta verificar**. `TokenRole` conserva lo que dice el token, solo como información. El coste está medido con un test que cuenta las consultas: exactamente una.
- **Aceptar dos veces el mismo token a la vez no falla como se esperaba.** La perdedora no recibía un error de concurrencia, sino una violación del índice único del email (las dos intentan crear una cuenta con el mismo email), que habría acabado en un 500. El handler captura ahora `DbUpdateException` (la base de la de concurrencia) y responde `invitation.invalid`. Lo cazó un test contra SQL Server real; los de base en memoria no podían verlo.
- **Quitar borra las sesiones, no solo las revoca**, porque los refresh tokens guardan el `UserId` sin clave foránea y quedarían huérfanos. El efecto sobre el acceso lo da además, de forma independiente, el propio refresh, que comprueba la pertenencia.
- **Tercer uso justificado de `IgnoreQueryFilters()`** (aceptar, que busca la invitación por el hash antes de conocer la empresa) y **lista blanca de lectura de `Users` con dos ficheros** (listar miembros con un *join* contra `TenantUsers`, y quitar). Los dos están en tests de arquitectura.
- **La respuesta de invitar lleva `Cache-Control: no-store`**: contiene el único valor en claro del token.
- **`TenantUser.ChangeRole` y `Invitation.Create` repiten dentro del dominio** las reglas del Owner (no se concede ni se cambia), como última barrera por debajo de `ManageableRoles`.
- **Riesgos aceptados que se mantienen:** dos invitaciones *distintas* para el mismo email aceptadas a la vez dan `invitation.invalid` a la perdedora en vez de `user.email_already_registered` (ambos 400); dos invitaciones simultáneas del mismo email por el mismo invitante pueden crear dos pendientes (no hay índice único posible, porque la caducidad depende del reloj); y el invitante ve el token y podría aceptarlo él mismo (D1).

---

## Relación con otros ADR

- Amplía: ADR-012 (dos permisos nuevos; revalida el rol en cada petición) y ADR-004 (el rol sigue viviendo en `TenantUser`).
- Usa: ADR-007 (mismo patrón de token aleatorio hasheado que el refresh token) y ADR-011 (errores con código).
- Cierra: los NEXT de ADR-009 ("revalidar la pertenencia al tenant en cada petición") y de ADR-012 (`Users.Manage`).
- Afecta a: ADR-009 (eventos de seguridad `1302` y `1401`–`1406` en el catálogo de R5) y ADR-002 (nuevo uso justificado de `IgnoreQueryFilters()` al aceptar una invitación).
