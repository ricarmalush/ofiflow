# Spec — 009-user-management

**Estado:** Borrador (pendiente de tu revisión; las decisiones a confirmar están al final de "Reglas de negocio")
**Fecha:** 2026-10-14
**Bounded Context:** Tenancy + Identity (transversal: Domain, Application, Infrastructure y Api)
**Depende de:** ADR-013 (gestión de usuarios, redactada junto a esta spec), ADR-012 (permisos por rol), ADR-004 (rol en `TenantUser`), ADR-007 (credenciales y refresh tokens), ADR-009 (seguridad base), ADR-011 (errores con código), spec 007 (aislamiento entre empresas), spec 008 (RBAC)

---

## Objetivo

Que el dueño de una empresa pueda **dar acceso a su equipo con el rol adecuado, cambiarlo y quitarlo**. Hoy solo existe el `Owner` que se crea al registrar la empresa: no hay forma de que entre un técnico o una persona de oficina, así que los cinco roles de la spec 008 existen pero nadie puede usarlos fuera de los tests.

Es el requisito previo de la Fase 2 (agenda y asignación de técnicos): no se puede asignar un trabajo a un técnico que no puede existir.

## Alcance — Qué SÍ incluye

1. **Invitar** a una persona a la empresa con un rol (`Admin`, `Manager`, `Technician` o `Employee`). La invitación genera un **token de un solo uso con caducidad**; el invitante se lo entrega a la persona (ver D1).
2. **Aceptar** una invitación (operación anónima): la persona elige su nombre y su contraseña; se crean sus credenciales, su `User` y su `TenantUser` en una sola transacción.
3. **Ver y revocar** las invitaciones pendientes.
4. **Listar** los miembros de la empresa.
5. **Cambiar el rol** de un miembro.
6. **Quitar** a un miembro de la empresa, con efecto **inmediato**.
7. **Reglas de jerarquía** (quién puede gestionar a quién), en una tabla única en Domain, igual que la matriz de permisos (ADR-012 R2).
8. **Permisos nuevos** `Users.Read` y `Users.Manage` en la matriz.
9. **Revalidar el rol y la pertenencia en cada petición** (el NEXT de ADR-009 y ADR-012): imprescindible ahora que existen "cambiar rol" y "quitar usuario" (ver D3).
10. **Rate limiting** de la aceptación (es anónima y cuesta CPU: hashea una contraseña).
11. **Eventos de seguridad** para cada acción sobre usuarios, sin datos personales.
12. **Tests de extremo a extremo** de todo el ciclo y de cada intento de escalada de privilegios, con las mutaciones sobre el código real que ya es costumbre.

## Alcance — Qué NO incluye

- **NOW** (necesario ahora, pero fuera de esta spec concreta): nada pendiente.
- **NEXT** (siguiente fase):
  - **Envío del enlace por email.** Requiere un servicio de correo (nuevo proveedor externo y sus secretos). Se diseña el puerto para que sea un cambio pequeño, pero no se construye aquí. Mientras tanto el token se devuelve al invitante.
  - **Pertenecer a varias empresas** y elegir empresa al hacer login (ADR-009). Esta spec mantiene una empresa por cuenta (D4).
  - **Restricción por recurso:** que el técnico vea solo sus trabajos (Fase 2).
  - **Recuperar contraseña**, confirmar email, bloqueo de cuenta tras intentos fallidos.
  - **Que un miembro abandone la empresa por sí mismo** y **traspasar la propiedad** (`Owner`).
- **LATER** (interesante, no prioritario): roles personalizados por empresa; invitaciones masivas; historial de auditoría consultable de quién cambió qué.
- **NEVER**: que el invitante conozca o elija la contraseña de otra persona; guardar el token de invitación en claro.

## Reglas de negocio / invariantes

**Invitaciones**

- **R1.** Solo se puede invitar con un rol de `{Admin, Manager, Technician, Employee}`. **`Owner` no se concede nunca** por esta vía: cada empresa tiene exactamente un `Owner`, el que la registró.
- **R2.** El token es aleatorio criptográfico de 32 bytes (256 bits). **Solo se guarda su hash (SHA-256)**; el valor en claro se devuelve **una única vez**, al crear la invitación. Mismo patrón que el refresh token (ADR-007).
- **R3.** Una invitación caduca a los **7 días**, se usa **una sola vez** y se puede revocar mientras esté pendiente.
- **R4.** El **email lo fija el invitante**, no el invitado: quien acepta no puede elegir otro. Se normaliza igual que en el registro.
- **R5.** Como mucho **una invitación pendiente por (empresa, email)** y como mucho **25 pendientes por empresa** (límite de abuso).
- **R6.** No se puede invitar a un email que **ya tiene cuenta** (D4). Se vuelve a comprobar al aceptar, porque puede haberse registrado mientras tanto.
- **R7.** Aceptar con un token desconocido, caducado, revocado o ya usado devuelve **exactamente la misma respuesta** (error `invitation.invalid`): quien lo intenta no aprende cuál de los cuatro casos es. El motivo real solo va al log.
- **R8.** Aceptar es **atómico**: o se crean credenciales, `User` y `TenantUser` y se consume el token, o no pasa nada. Dos aceptaciones simultáneas del mismo token: solo una gana.

**Jerarquía (quién puede gestionar a quién)**

- **R9.** Existe una tabla única, `ManageableRoles`, que dice qué roles puede **conceder, cambiar y quitar** cada rol:

| El que actúa ↓ / el rol objetivo → | Owner | Admin | Manager | Technician | Employee |
|---|:-:|:-:|:-:|:-:|:-:|
| **Owner** | ❌ | ✅ | ✅ | ✅ | ✅ |
| **Admin** | ❌ | ❌ | ✅ | ✅ | ✅ |
| Manager, Technician, Employee | ❌ | ❌ | ❌ | ❌ | ❌ |

- **R10.** Para **cambiar un rol** hacen falta las dos cosas: que el rol actual del objetivo **y** el nuevo estén en la fila del que actúa. Así un `Admin` no puede ascender a nadie a `Admin` ni tocar a otro `Admin`.
- **R11.** Nadie puede **cambiar su propio rol ni quitarse a sí mismo** (evita la autoescalada y dejar la empresa sin gestor).
- **R12.** El `Owner` **no se puede cambiar ni quitar** por esta vía. Por tanto toda empresa tiene siempre su `Owner`.
- **R13.** Incumplir R9–R12 es un **403** con código propio; el `Id` objetivo de otra empresa o inexistente es **404** `tenant_user.not_found`, igual en ambos casos (aislamiento, spec 007).

**Quitar a un miembro**

- **R14.** Quitar a un miembro: (a) borra su pertenencia (`TenantUser`); (b) **revoca todos sus refresh tokens**; (c) **desasigna** los trabajos suyos que no estén terminados (`New` o `InProgress`); los terminados conservan la referencia como historial; (d) como la cuenta no pertenece a ninguna otra empresa (D4), **borra también sus credenciales y su `User`** (datos personales), de modo que el email vuelve a estar libre. Todo en una transacción.
- **R15.** El efecto es **inmediato** gracias a la revalidación por petición (R16), no a los 30 minutos de vida del token.

**Revalidación por petición**

- **R16.** En cada operación autenticada, antes de comprobar el permiso, se consulta la pertenencia (`TenantUser` del usuario en la empresa del token) y se usa **el rol de la base de datos, no el del token**. Sin pertenencia → 403 y evento `1302`. El rol del token deja de tener efecto de autorización (sigue viajando por compatibilidad).
- **R17.** La consulta es una sola, por el índice único `(TenantId, UserId)`, y no depende de nada que mande el cliente.

**Permisos nuevos**

| Permiso | Operaciones | Owner | Admin | Manager | Employee | Technician |
|---|---|:-:|:-:|:-:|:-:|:-:|
| `Users.Read` | Listar miembros | ✅ | ✅ | ✅ | ❌ | ❌ |
| `Users.Manage` | Invitar, ver y revocar invitaciones, cambiar rol, quitar | ✅ | ✅ | ❌ | ❌ | ❌ |

`Manager` puede **ver** quién hay (necesita identificar técnicos para asignarles trabajos) pero no gestionar.

### Decisiones a confirmar antes de aprobar

| # | Decisión | Mi recomendación | Alternativa descartada y por qué |
|---|---|---|---|
| **D1** | **Cómo llega el acceso a la persona sin email:** el token se devuelve al invitante, que lo pasa por su canal (WhatsApp, en persona...) | Enlace con token de un solo uso; la persona **elige su propia contraseña** | *Crear el usuario con una contraseña temporal:* el invitante conocería la contraseña de otra persona (rompe el NEVER). Riesgo aceptado: el invitante ve el token y podría aceptarlo él mismo; ya es de confianza en su empresa y podría hacer lo mismo con cualquier dato de ella |
| **D2** | **Jerarquía:** un `Admin` no puede crear, cambiar ni quitar a otros `Admin` | Solo el `Owner` gestiona `Admin`s | *Owner y Admin iguales:* un Admin comprometido o desleal podría ascender a cómplices y bloquear al dueño |
| **D3** | **Revalidación por petición incluida** en esta spec | Sí: una consulta indexada más por petición | *Dejarlo para después:* un empleado despedido seguiría con acceso hasta 30 minutos, y un `Admin` degradado seguiría con sus poderes. Es el riesgo que ADR-009/ADR-012 aceptaron "mientras no exista quitar usuarios"; esta spec lo hace existir |
| **D4** | **Una empresa por cuenta** de momento | Rechazar invitar a un email que ya tiene cuenta | *Multi-empresa:* exige elegir empresa al hacer login y cambiar de empresa; otra spec (NEXT en ADR-009) |
| **D5** | **Quitar = borrar** credenciales y datos personales, no desactivar | Borrar (menos datos personales guardados, email liberado) | *Desactivar:* conserva datos de alguien que ya no es parte de la empresa y bloquea su email |
| **D6** | **`Manager` puede listar** miembros (`Users.Read`) | Sí | *Solo Owner/Admin:* el Manager no podría elegir a quién asignar un trabajo |
| **D7** | **Límites:** caducidad 7 días, 25 invitaciones pendientes, aceptación 10 por hora y por IP | Valores iniciales, se revisan con datos reales | — |

## Modelo conceptual

```
Domain/Tenancy
- Invitation  (aggregate, ITenantOwned)
    Id · TenantId · Email · Role · TokenHash · ExpiresAt
    CreatedAt · CreatedByUserId · AcceptedAt? · RevokedAt?
    Estado derivado: Pendiente | Aceptada | Revocada | Caducada
- Permission      + UsersRead, UsersManage
- RolePermissions   (tabla única, ampliada)
- ManageableRoles   (tabla única nueva: rol que actúa → roles que puede gestionar)

Application (nuevas operaciones)
- InviteUserCommand            [Users.Manage]
- GetInvitationsQuery          [Users.Manage]
- RevokeInvitationCommand      [Users.Manage]
- AcceptInvitationCommand      [anónima + rate limiting]
- GetUsersQuery                [Users.Read]
- ChangeUserRoleCommand        [Users.Manage]
- RemoveUserCommand            [Users.Manage]
- IIdentityService  + DeleteUserAsync
- ITokenService     + revocar tokens de un usuario
- Revalidación de pertenencia en AuthorizationBehavior

Infrastructure
- InvitationConfiguration + migración (índice único por TokenHash, índice (TenantId, Email))
```

Operaciones tras esta spec: **23** (19 con permiso, 4 anónimas).

## Criterios de aceptación

**Ciclo completo**
- Dado un `Owner`, cuando invita a `ana@…` como `Technician`, entonces recibe un token y la invitación aparece pendiente **sin el token**; cuando Ana lo acepta con su nombre y su contraseña, entonces puede hacer login y su token lleva el rol `Technician` en la empresa del `Owner`.
- Dado un `Admin`, cuando invita a un `Manager`, `Technician` o `Employee`, entonces funciona; y cuando invita a un `Admin` o a un `Owner`, entonces recibe 403.
- Dado cualquier rol, cuando invita con rol `Owner`, entonces nunca se crea la invitación (ni para el `Owner`).

**Token y aceptación**
- Dado un token ya usado, caducado, revocado o inventado, cuando se acepta, entonces la respuesta es **idéntica** en los cuatro casos (R7) y no se crea nada.
- Dados dos intentos simultáneos con el mismo token, entonces exactamente uno crea la cuenta (R8).
- Dado que se acepta con un nombre vacío o una contraseña que incumple `PasswordRules`, entonces 400 de validación y el token **sigue sin consumirse**.
- Dado que el email ya tiene cuenta al aceptar, entonces falla con `user.email_already_registered` y no se crea nada.
- Dada la base de datos, entonces el token en claro no está en ninguna columna ni en ningún log (solo el hash).
- Dadas 11 aceptaciones desde la misma IP en una hora, entonces la 11.ª recibe 429.

**Jerarquía**
- Dado un `Admin`, cuando intenta cambiar o quitar a otro `Admin`, o ascender a alguien a `Admin`, entonces 403 (R9, R10).
- Dado un `Admin` que intenta cambiar su propio rol o quitarse, entonces 403; y lo mismo para el `Owner` (R11).
- Dado cualquiera que intenta cambiar o quitar al `Owner`, entonces 403 (R12).
- Dada la tabla `ManageableRoles`, entonces un test de matriz coincide celda a celda con la tabla de arriba (25 celdas).
- Dado un `Manager`, `Employee` o `Technician`, cuando ejecuta cualquier operación de gestión, entonces 403; y un `Manager` sí puede listar miembros.

**Quitar y revalidar**
- Dado un miembro quitado, cuando usa su **access token todavía no caducado**, entonces recibe 403 y se registra `1302` (R15, R16); cuando intenta renovar la sesión o hacer login, entonces falla.
- Dado un miembro con un trabajo `InProgress` asignado, cuando se le quita, entonces el trabajo queda sin asignar y los terminados conservan la referencia (R14c).
- Dado un miembro quitado, entonces su fila de `User` y sus credenciales ya no existen y su email puede volver a invitarse (R14d).
- Dado un `Admin` degradado a `Employee`, cuando usa su access token anterior, entonces **ya tiene los permisos de `Employee`**, no los de `Admin` (R16).
- Dado el rol del token distinto del de la base de datos, entonces manda el de la base de datos.

**Aislamiento (caso multi-tenant obligatorio)**
- Dado un `Owner` de la empresa A, cuando usa el `Id` de un miembro o de una invitación de la empresa B para cambiar rol, quitar o revocar, entonces recibe el mismo 404 que con un `Id` inexistente; y no ve miembros ni invitaciones de B al listar.
- Dado que se acepta una invitación, entonces el nuevo `TenantUser` pertenece a la empresa **de la invitación** y a ninguna otra.
- Dados los tests de las specs 007 y 008, entonces siguen pasando; los de 008 que fijan "no consulta la base de datos" se actualizan de forma explícita por R16/R17.

**Guardarraíles**
- Dada una operación nueva sin declarar, entonces el test de arquitectura sigue fallando (spec 008).
- Dado el uso de `IgnoreQueryFilters()` y de los DbSet globales (`Users`, `ApplicationUsers`) en ficheros nuevos, entonces solo pasan los que están en la lista blanca, con motivo escrito.

## Seguridad (ADR-009)

- **Datos personales:** sí. Se guardan el **email del invitado** (en `Invitation`) y, al aceptar, su **nombre** y credenciales. Ninguno aparece en logs: los eventos llevan solo Ids, rol y nombre de operación. Al quitar a un miembro se borran (R14d). Las invitaciones aceptadas o caducadas conservan el email: se decide su purga en el plan (tarea abierta).
- **Autorización:** matrices de R9 y de permisos nuevos, más revalidación por petición (R16). Es el centro de la spec.
- **Entrada:** `Email` (validador del registro, longitud de la constante de Domain), `Name` (`User.NameMaxLength`), `Password` (`PasswordRules`), `Role` (debe ser un valor definido del enumerado, y no `Owner`), token (longitud exacta esperada, para no hashear texto arbitrario enorme).
- **Aislamiento:** `Invitation` es `ITenantOwned`. **Un uso nuevo y justificado de `IgnoreQueryFilters()`**: la aceptación busca la invitación por el hash del token **antes de conocer la empresa** (igual que el login); va a la lista blanca con su motivo. Acceso a `Users` y `ApplicationUsers` desde ficheros nuevos: lista blanca igualmente.
- **SQL crudo:** ninguno.
- **Abuso:** la aceptación es anónima y cara (PBKDF2): rate limiting por IP (D7). La creación de invitaciones está limitada por empresa (R5). Enumerar emails: invitar revela si un email tiene cuenta (R6) a un `Owner`/`Admin` autenticado; es el mismo riesgo que ya tiene el registro, aceptado y anotado.
- **Eventos de seguridad (nuevos, sin datos personales):**
  - `1302` token válido sin pertenencia activa (miembro quitado)
  - `1401` invitación creada · `1402` invitación aceptada · `1403` aceptación rechazada (con el motivo real) · `1404` invitación revocada
  - `1405` rol cambiado (anterior y nuevo) · `1406` miembro quitado
  - Un analista querría ver, por ejemplo, una ráfaga de `1403` desde una IP.
- **Riesgos técnicos a verificar con tests reales:** (1) la carrera de dos aceptaciones simultáneas (debe haber un control de concurrencia, no solo un `if`); (2) que la comparación del token no filtre información por tiempo (se busca por hash en el índice, no se compara texto); (3) que el borrado de credenciales no deje filas huérfanas.

## Decisiones técnicas relevantes

- Se apoya en: ADR-002, ADR-004, ADR-007, ADR-009, ADR-011, ADR-012.
- **¿Requiere una ADR nueva?** Sí: **ADR-013 (Propuesto)**, redactada junto a esta spec. Recoge el mecanismo de invitación sin email, la jerarquía de roles como tabla, la revalidación por petición y el borrado al quitar.
- **Cambia ADR-012:** su riesgo aceptado "el rol puede estar desfasado hasta que caduque el token" desaparece con R16; se anota en su nota de estado al cerrar.

## Fuera de alcance / Backlog relacionado

- Envío por email, multi-empresa, recuperar contraseña, bloqueo por intentos, abandonar la empresa, traspasar la propiedad.
- Purga de invitaciones antiguas (decidir en el plan).
- Restricción por recurso del técnico (Fase 2).

---

## Validación antes de aprobar

> ¿Esta funcionalidad ayuda a conseguir o mantener al primer cliente? (sección 84 del prompt maestro)

**Sí, directamente.** Un autónomo con un empleado o un técnico no puede usar OfiFlow sin esto: hoy solo existe el dueño. Es también el requisito de la Fase 2 (asignar trabajos a técnicos) y es lo que hace *visible* el control de roles de la spec 008. Y cierra, antes de que alguien pueda explotarlo, el riesgo que ADR-009 y ADR-012 dejaron anotado: quitar o degradar a una persona tiene que funcionar **al instante**, no a los 30 minutos.
