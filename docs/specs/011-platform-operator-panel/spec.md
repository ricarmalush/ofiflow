# Spec — 011-platform-operator-panel

**Estado:** Aprobada (2026-10-22), con las decisiones D1-D11 tal como se recomendaron
**Fecha:** 2026-10-22
**Bounded Context:** Tenancy (la suspensión vive en `Tenant`) e Identity (el operador es una identidad aparte); transversal a Application, Infrastructure y Api
**Depende de:** ADR-016 (panel de operador, redactada junto a esta spec), ADR-002 (multi-tenancy: es su única excepción deliberada), ADR-007 (credenciales y refresh tokens), ADR-009 (seguridad base), ADR-011 (errores con código), ADR-012 (permisos), ADR-013 (revalidación por petición), ADR-014 (MFA y bloqueo de cuenta: **requisito previo, ya implementado**), ADR-015 (logs a fichero)

---

## Objetivo

Que quien **opera OfiFlow** pueda ver **qué empresas hay** y **suspender o reactivar** una, **sin poder ver nada de lo que hay dentro de ellas**. Hoy nadie puede: el `Owner` es superadministrador solo de **su** empresa y el aislamiento (spec 007) impide que nadie vea otra. Antes de tener clientes de pago hace falta poder cortar el acceso a una empresa (impago, abuso, incidente) y saber cuántas hay.

Es la **excepción más peligrosa al aislamiento**: una cuenta que ve todas las empresas. Por eso esta spec la hace lo más estrecha posible:

1. **Solo metadatos de empresa**: nombre, fecha de alta, número de miembros y estado. Ni clientes, ni trabajos, ni emails, ni nombres de personas.
2. **Una identidad aparte**, que no es un rol de empresa, con su propia sesión, su propia clave y **segundo factor obligatorio** sin excepción.
3. **Cada acción deja rastro**: en el log y, las que cambian algo, también en una tabla que no caduca.

## Alcance — Qué SÍ incluye

1. **Cuenta de operador de la plataforma**, que **no es miembro de ninguna empresa** y se crea **fuera de la API** (un comando del servidor), ya con el segundo factor activado.
2. **Login de plataforma en dos pasos** (contraseña y TOTP) con una sesión propia: otra clave, otra audiencia, 15 minutos y sin refresh token.
3. **Listar las empresas** (nombre, alta, número de miembros, suspendida o no y desde cuándo), paginado.
4. **Suspender y reactivar** una empresa.
5. **Hacer cumplir la suspensión** a los miembros: sin sesión nueva, sin renovarla, sin aceptar invitaciones y con el 403 en la siguiente petición.
6. **Dar de baja a un operador** (también fuera de la API), con efecto en su siguiente petición.
7. **Auditoría persistente** de lo que cambia algo (sesión de operador, suspender, reactivar, alta y baja de operadores).
8. **Rate limiting** de las rutas nuevas y **eventos de seguridad** nuevos, sin datos personales.
9. **Guardarraíles**: un tercer tipo de declaración de autorización (`[RequiresPlatformOperator]`), la lista blanca de lecturas globales ampliada y los tests de arquitectura al día.
10. **Tests de extremo a extremo** de todo el ciclo y de cada ataque descrito, con las mutaciones sobre el código real que ya son costumbre.

## Alcance — Qué NO incluye

- **NOW** (necesario ahora, pero fuera de esta spec concreta): nada pendiente. Los requisitos previos (MFA y bloqueo de cuenta) ya están (spec 010).
- **NEXT** (siguiente fase):
  - **Plan o tarifa** de cada empresa: hoy no existe ningún concepto de plan (D5). Llegará con la facturación.
  - **Motivo de la suspensión** (texto libre o lista cerrada) y **aviso por email** a la empresa. Un texto libre es un dato nuevo que validar y proteger.
  - **Buscar o filtrar por nombre**, y más estadísticas (altas por mes). Hoy: paginar y filtrar por suspendida.
  - **Refresh token** de operador, y **varios operadores con permisos distintos** (solo lectura, suspender).
  - **Vista de soporte** (leer datos de una empresa) e **impersonar**: cuando haya clientes de pago, con base legal RGPD (ver D5). Cada una será su propia spec.
  - **Borrar una empresa** y exportar sus datos (derecho de supresión y portabilidad).
- **LATER**: panel con interfaz; alertas por email sobre acciones del operador; aprobar una suspensión entre dos operadores.
- **NEVER**: que un `TenantRole` (ni siquiera `Owner`) dé acceso a `/platform`; crear un operador por la API; que la cuenta de operador pertenezca también a una empresa; un operador sin segundo factor; ver o exportar datos de clientes desde esta cuenta.

## Reglas de negocio / invariantes

**La identidad del operador**

- **R1.** Un **operador** es una cuenta (misma tabla de credenciales que las demás, para reutilizar hash de contraseña, segundo factor y bloqueo) más una fila en `PlatformOperator`. **Una cuenta es de empresa o de plataforma, nunca de las dos**: la cuenta de operador no tiene ninguna fila en `TenantUser`, y una cuenta con empresa no puede convertirse en operador (D1).
- **R2.** Un operador **no se crea ni se modifica por la API**. Se crea con un **comando del servidor** (D2) que, en una sola ejecución, crea la cuenta, genera su secreto TOTP, **exige un primer código válido** para confirmarlo y muestra los códigos de recuperación **una única vez**. Si no se confirma, **no se crea nada**. Así nunca existe un operador sin segundo factor.
- **R3.** El email de un operador queda ocupado como cualquier otro: no se puede registrar una empresa con él ni invitarlo a ninguna (spec 009 R6).
- **R4.** **Dar de baja** a un operador (comando del servidor) rellena `DisabledAt`. Surte efecto **en su siguiente petición** (R10), no a los 15 minutos.

**La sesión de plataforma**

- **R5.** El login de plataforma **siempre** devuelve un desafío (segundo paso), nunca una sesión: no hay operador sin MFA (D4). El desafío es el de la spec 010 (opaco, 5 minutos, un solo uso, 5 intentos).
- **R6.** Todo fallo (email desconocido, contraseña errónea, cuenta bloqueada, **cuenta que no es de operador**, operador dado de baja, desafío o código inválido) devuelve **exactamente la misma respuesta** (401, mismo cuerpo), hace la misma verificación de contraseña completa y cuenta para el bloqueo. El motivo real solo va al log. Una cuenta de **empresa** que prueba el login de plataforma **no aprende nada**: recibe el mismo 401.
- **R7.** El token de plataforma es **otro mundo** que el de empresa (D3): **audiencia distinta y clave propia** (`Jwt:PlatformSecret`, con el mismo trato que `Jwt:Secret`: la API no arranca si falta o es inválida), con un esquema de autenticación separado. Un token de empresa **no vale** en `/api/v1/platform` y uno de plataforma **no vale** en ninguna otra ruta, aunque la firma fuera correcta. Dura **15 minutos** y **no tiene refresh token**: pasado ese tiempo hay que repetir contraseña y código.
- **R8.** **Solo el login sin MFA, el canje del desafío de empresa y el canje del desafío de plataforma pueden emitir sesiones** (amplía el guardarraíl de la spec 010 R12). Un test de arquitectura lo fija.
- **R9.** El desafío de plataforma **solo se canjea por una sesión de plataforma**, y el de empresa **solo por una de empresa**. Un desafío no cruza de mundo.
- **R10.** **Revalidación en cada petición**, como la pertenencia en la spec 009 R16: cada operación de plataforma comprueba en la base de datos que la cuenta sigue siendo un operador **no dado de baja**. Un token todavía válido de un operador dado de baja **no sirve**.

**Qué ve y qué hace el operador**

- **R11.** **Listar empresas** devuelve por cada una: `Id`, nombre, fecha de alta, **número de miembros** y estado (`SuspendedAt`, nulo si está activa). **Nada más**: ni emails, ni nombres de personas, ni clientes, ni trabajos, ni recuentos de esos. Paginado con tope (`pageSize` ≤ 100), orden fijo (alta descendente, luego `Id`) y filtro opcional por suspendida.
- **R12.** **Suspender** rellena `Tenant.SuspendedAt` (hora del servidor) y **reactivar** lo vacía. Suspender una empresa ya suspendida, o reactivar una activa, es un error de negocio con código (no un éxito silencioso). **No se borra ni se modifica ningún dato** de la empresa.
- **R13.** El `Id` de una empresa inexistente da **404**, el mismo código que cualquier otro «no existe» del sistema.
- **R14.** Todas las rutas de plataforma son `/api/v1/platform/...` y todas sus operaciones declaran `[RequiresPlatformOperator]`. Ninguna operación de empresa es alcanzable desde esa ruta, ni al revés.

**Qué provoca la suspensión en los miembros**

- **R15.** Una empresa **suspendida**: (a) sus miembros **no obtienen sesión nueva**: el login, tras credenciales correctas, devuelve **403** con `tenant.suspended` y **no cuenta como intento fallido**; (b) **no pueden renovar** la sesión (el refresh falla igual que un token caducado); (c) **nadie puede aceptar una invitación** a esa empresa (misma respuesta que un token inválido, spec 009 R7); (d) **cualquier petición con un token todavía válido recibe 403** en la **siguiente** petición: la comprobación de pertenencia incluye el estado de la empresa. Reactivarla lo devuelve todo a como estaba: los refresh tokens no se borran, simplemente no sirven mientras dura la suspensión.
- **R16.** Registrar una empresa nueva **no se ve afectado**.

**Auditoría**

- **R17.** Toda acción de plataforma que **cambia algo** (sesión de operador emitida, empresa suspendida, empresa reactivada, operador creado, operador dado de baja) escribe una fila en `PlatformAuditEntry` **en la misma transacción** que la acción: si falla la acción, no queda fila; si queda la fila, la acción ocurrió. La fila lleva **solo Ids, un código de acción y la hora**, y **no se modifica ni se borra** desde la aplicación (D7). Las **lecturas** (listar) quedan en el log, no en la tabla.

**Operaciones y declaración de autorización**

| Operación | Tipo | Quién |
|---|---|---|
| `PlatformLoginCommand` | anónima + rate limiting | cualquiera (siempre 401 salvo desafío) |
| `VerifyPlatformChallengeCommand` | anónima + rate limiting | quien tenga un desafío de plataforma |
| `ListTenantsQuery` | `[RequiresPlatformOperator]` | operador activo |
| `SuspendTenantCommand` | `[RequiresPlatformOperator]` | operador activo |
| `ReactivateTenantCommand` | `[RequiresPlatformOperator]` | operador activo |

Alta y baja de operadores **no son operaciones de la API**: no tienen ruta ni petición MediatR expuesta. Un test comprueba que ningún endpoint las alcanza.

### Decisiones a confirmar antes de aprobar

| # | Decisión | Mi recomendación | Alternativa descartada y por qué |
|---|---|---|---|
| **D1** | **Qué es un operador** | **Una cuenta normal (mismo hash, MFA y bloqueo) más una fila en `PlatformOperator`**, sin pertenencia a ninguna empresa, nunca las dos cosas | *Un `TenantRole` nuevo (`PlatformAdmin`):* mezclaría el rol con el aislamiento que se quiere romper, y el rol vive dentro de una empresa. *Una columna booleana en la cuenta:* un solo bit mal cambiado daría acceso global; una tabla aparte se puede auditar y proteger por separado |
| **D2** | **Cómo se crea el primer operador** | **Un comando del servidor** (por ejemplo `dotnet OfiFlow.Api.dll create-platform-operator`), con la contraseña leída por consola y no por argumento, que crea la cuenta y **enrola y confirma el TOTP en el mismo acto** | *Un endpoint con un token de arranque:* una ruta HTTP que crea la cuenta más poderosa es superficie de ataque. *SQL a mano:* se salta el hash y el enrolado del segundo factor, y deja un operador sin MFA. *Datos de arranque en la configuración:* dejaría una contraseña en un fichero |
| **D3** | **Sesión del operador** | **Esquema aparte: audiencia y clave propias, 15 minutos, sin refresh token** | *Mismo token con un claim `platform`:* si la clave o la validación fallan, un token de empresa podría valer en plataforma. *Con refresh token:* otra pieza que robar para una cuenta tan sensible; repetir el TOTP cada 15 minutos es un coste aceptable para un uso ocasional |
| **D4** | **Segundo factor** | **Obligatorio y sin excepción** (el login de plataforma siempre da desafío; sin MFA confirmado la cuenta no existe) | *Opcional como en las cuentas de empresa:* la spec 010 D2 dijo que para el operador sería obligatorio |
| **D5** | **Qué ve** | **Solo metadatos de la empresa** (R11) | *Datos de clientes y trabajos "para soporte":* OfiFlow es **encargado del tratamiento** de los datos personales de los clientes de cada empresa; verlos exige base legal y registro de cada acceso. *Impersonar:* aún más grave. Ambos son NEXT, cuando haya clientes de pago. *Plan o tarifa:* hoy no existe en el modelo; no se inventa |
| **D6** | **Qué hace la suspensión** | **Campo `SuspendedAt` en `Tenant`; bloquea el acceso de los miembros y no toca los datos** | *Borrar o desactivar los datos:* irreversible y fuera de lo necesario. *Revocar todos los refresh tokens al suspender:* muchas filas por una operación reversible; no hacen falta, porque la comprobación por petición ya los deja inútiles |
| **D7** | **Auditoría** | **Tabla `PlatformAuditEntry` append-only para lo que cambia algo, además del log** | *Solo el log:* con 14 días de retención (ADR-015) no se puede responder «¿quién suspendió mi empresa el mes pasado?». *Auditar también las lecturas:* mucho volumen para poco valor; quedan en el log |
| **D8** | **Bloqueo y límites del operador** | **El mismo bloqueo (5 fallos, 15 minutos) y límites propios por IP** | *Sin bloqueo para el operador:* es la cuenta más valiosa. **Riesgo aceptado:** quien conozca su email puede bloquearla 15 minutos (negación de servicio sobre una cuenta que solo se usa a ratos), a cambio de que adivinar su contraseña sea inviable |
| **D9** | **Cómo se declara la autorización** | **Un tercer atributo `[RequiresPlatformOperator]`**, excluyente con permiso y con anónima, y fail-closed como hoy | *Un `Permission` más:* los permisos son de empresa y se resuelven con el rol de una pertenencia que el operador no tiene |
| **D10** | **Operador dado de baja** | **Surte efecto en la siguiente petición** (revalidación en cada una, como la spec 009 R16) | *Esperar a que caduque el token (15 min):* es una cuenta con acceso global; la baja debe ser inmediata |
| **D11** | **Qué pasa si el operador pierde el móvil y los códigos** | **Darlo de baja y crear uno nuevo** por el comando del servidor (hay que tener acceso al servidor) | *Reinicio de MFA por otro operador:* exigiría una jerarquía entre operadores; con uno solo no aplica. *Recuperación por email:* el canal más débil sobre la cuenta más fuerte |

Valores iniciales (se revisan con datos reales): sesión de plataforma **15 minutos**, login de plataforma **5 por minuto y por IP** (la misma política que el login), canje del desafío **10 por minuto y por IP** (la misma que el de empresa), operaciones de plataforma **30 por minuto y por IP** (política nueva).

## Modelo conceptual

```
Domain/Tenancy
- Tenant            + SuspendedAt? · IsSuspended · Suspend(utcNow) · Reactivate()
                      (errores: tenant.suspended, tenant.already_suspended, tenant.not_suspended)

Infrastructure/Identity  (globales: no son de una empresa; no ITenantOwned; a TenantModelRules.GlobalEntities)
- PlatformOperator    UserId (único) · CreatedAt · DisabledAt?
- PlatformAuditEntry  Id · OperatorUserId · Action (código estable) · TenantId? · OccurredAt
                      (sin RowVersion: solo se inserta; sin datos personales)
- MfaChallenge        + Audience ("tenant" | "platform"): un desafío solo se canjea en su mundo (R9)

Application (nuevas operaciones)
- PlatformLoginCommand                [anónima + rate limiting]
- VerifyPlatformChallengeCommand      [anónima + rate limiting]
- ListTenantsQuery                    [RequiresPlatformOperator]
- SuspendTenantCommand                [RequiresPlatformOperator]
- ReactivateTenantCommand             [RequiresPlatformOperator]
- Puertos nuevos: IPlatformOperators (¿es operador activo?, alta, baja), IPlatformTokenService (emitir la sesión de plataforma),
                  IPlatformAudit (escribir la fila, sin confirmarla); ICurrentUser gana IsPlatformOperator verificable
- Cambian: ICurrentUser.VerifyMembershipAsync (mira el estado de la empresa), LoginCommand y AcceptInvitation y RefreshToken (rechazan una empresa suspendida)
```

Operaciones tras esta spec: **35** (25 con permiso, 7 anónimas y 3 de plataforma).

## Criterios de aceptación

**El operador y su sesión**
- Dado el comando de alta con una contraseña válida, cuando se confirma con el primer código de su app, entonces existe la cuenta, tiene el segundo factor **confirmado** y recibe 10 códigos de recuperación **una sola vez**; y si **no** se confirma, no queda ninguna fila (ni cuenta, ni secreto, ni operador).
- Dado un email ya registrado (de empresa o de operador), cuando se intenta crear un operador, entonces falla y no se modifica nada (R1, R3).
- Dado un operador, cuando pone la contraseña correcta en el login de plataforma, entonces **no recibe sesión** sino un desafío; cuando lo canjea con un código válido, entonces recibe un token de plataforma que vale 15 minutos y **no trae refresh token**.
- Dado un token de plataforma, cuando se usa en cualquier ruta que no sea `/api/v1/platform`, entonces **401**; dado un token de empresa (incluido el de un `Owner`), cuando se usa en `/api/v1/platform`, entonces **401**.
- Dados email desconocido, contraseña errónea, cuenta bloqueada, **cuenta de empresa con su contraseña correcta**, operador dado de baja y código erróneo, entonces la respuesta es **byte a byte la misma** y **se ejecuta la misma verificación de contraseña** (por conteo de llamadas, no por cronómetro).
- Dado un desafío de empresa, cuando se canjea en la ruta de plataforma, o uno de plataforma en la de empresa, entonces falla igual que uno inventado (R9).
- Dado un operador dado de baja, cuando usa un token que aún no había caducado, entonces **401 o 403 en esa misma petición**, y queda el evento correspondiente (R10).
- Dada una API sin `Jwt:PlatformSecret` o con una clave que no mide 32 bytes, entonces **no arranca** y el mensaje dice qué falta.

**Listar, suspender y reactivar**
- Dados varios tenants, cuando el operador lista, entonces recibe por cada uno **solo** `Id`, nombre, alta, número de miembros y estado; y la respuesta **no contiene** ningún email, nombre de persona, cliente ni trabajo (comprobado sobre el cuerpo completo).
- Dado `pageSize` mayor que 100, o página negativa, entonces 400 con su código; dado un orden, entonces estable entre páginas.
- Dado que suspende una empresa activa, entonces `SuspendedAt` queda con la hora del servidor, se escribe **una** fila de auditoría en la misma transacción y se registra el evento; dado que la suspende de nuevo, entonces error de negocio y **no** se escribe otra fila.
- Dado que reactiva una suspendida, entonces `SuspendedAt` queda vacío y queda la fila y el evento; dado que reactiva una activa, entonces error de negocio.
- Dado el `Id` de una empresa inexistente, entonces el mismo 404 en suspender y reactivar.
- Dado que la acción falla a mitad (por ejemplo, la fila de auditoría no se puede escribir), entonces la empresa **no** queda suspendida (todo o nada).

**Efecto en los miembros (caso multi-tenant obligatorio)**
- Dada una empresa suspendida, cuando uno de sus miembros hace login con credenciales correctas, entonces **403** con `tenant.suspended` y **no suma un intento fallido**; con credenciales incorrectas, el 401 de siempre.
- Dada una empresa suspendida, cuando un miembro con un token todavía válido hace **cualquier** petición, entonces **403 con el código `tenant.suspended`** en esa petición y queda el evento `1303`.
- Dada una empresa suspendida, cuando intenta renovar la sesión, entonces falla como un refresh token caducado; y cuando alguien intenta aceptar una invitación a ella, entonces recibe la misma respuesta que con un token inválido.
- Dada la empresa reactivada, entonces el mismo miembro vuelve a operar con su sesión anterior si no había caducado, y los datos están **intactos**.
- Dadas **dos empresas A y B**, cuando se suspende A, entonces **B no se ve afectada en nada** (login, peticiones, invitaciones).
- Dado un `Owner` de la empresa A, cuando llama a cualquier ruta de `/api/v1/platform`, entonces 401; y dado el operador, cuando llama a cualquier ruta de empresa (`/customers`, `/jobs`, `/users`...) con su token, entonces 401: **el operador no ve ni un cliente ni un trabajo de nadie**.

**Guardarraíles**
- Dado un código nuevo que emita una sesión fuera de los tres sitios permitidos (R8), entonces el test de arquitectura falla.
- Dada una operación sin declarar, o declarada con más de un tipo (permiso, anónima, plataforma), entonces el test de arquitectura de la spec 008 falla y el comportamiento es denegar.
- Dado un handler nuevo que lea `Tenants` o cuente `TenantUsers` ignorando el filtro, fuera de la lista blanca, entonces el test de arquitectura falla; y las dos nuevas entidades están clasificadas como globales en `TenantModelRules`.
- Dado un endpoint que alcance el alta o la baja de operadores, entonces el test de arquitectura falla.
- Dado un cambio en el modelo sin migración, entonces falla `TheMigrations_MatchTheModel`.

## Seguridad (ADR-009)

- **Datos personales:** el operador es una cuenta con **su email** (dato personal, tratado como el de cualquier cuenta) y nada más. La lista de empresas **no muestra ningún dato personal**: el nombre de una empresa es un dato de negocio y el número de miembros es un recuento. La auditoría guarda **solo Ids**. Ningún email, contraseña, secreto ni código aparece en logs.
- **Autorización:** `[RequiresPlatformOperator]` en las tres operaciones de plataforma, con revalidación en la base de datos en cada petición (R10). La identidad sale del **token de plataforma validado**, nunca del cuerpo. El `TenantId` que recibe suspender/reactivar es el **objetivo**, no un contexto de empresa: es el **único** caso del sistema donde un `Id` de empresa entra por la petición, y el test de arquitectura `NoCommandOrQuery_AcceptsTenantIdFromTheClient` lo declara como excepción **nombrada** de estas dos operaciones y de ninguna más.
- **Entrada:** `pageSize` con tope, página ≥ 1, `suspended` booleano; el `Id` es un `Guid`; la contraseña y los códigos siguen las reglas de la spec 010. No hay campos de texto libre nuevos (por eso el motivo de suspensión es NEXT).
- **Aislamiento:** `PlatformOperator` y `PlatformAuditEntry` son **globales**. **Usos nuevos de `IgnoreQueryFilters()`:** el handler de listar necesita **contar miembros de todas las empresas**; se limita a un recuento agrupado por empresa (sin proyectar ninguna columna de personas) y se añade a la lista blanca del test de arquitectura con su motivo. La lectura de `Tenants` de los tres handlers entra en la lista de lecturas globales.
- **SQL crudo:** ninguno previsto. El recuento por empresa se hace con LINQ.
- **Abuso:** el login y el canje de plataforma son anónimos y cuestan CPU y base de datos: **5 y 10 por minuto y por IP**. Las operaciones autenticadas, **30 por minuto y por IP**. **Riesgo aceptado:** el bloqueo permite a quien conozca el email del operador bloquearlo 15 minutos (D8).
- **Eventos de seguridad (nuevos, sin datos personales)** — nueve, con lo que el catálogo pasa de 23 a 32:
  - `1303` acceso denegado por **empresa suspendida**: una petición con un token todavía válido, el login o el canje del segundo factor (con la operación, `UserId` y `TenantId`). Renovar la sesión y aceptar una invitación **no** usan el `1303`: dejan sus eventos de siempre (`1004` y `1403`) con el motivo `TenantSuspended`
  - `1601` sesión de operador emitida · `1602` login de plataforma rechazado (**motivo real**: no es operador, dado de baja, sin MFA, desafío inválido…)
  - `1603` empresas listadas (operador, página y número de resultados) · `1604` empresa suspendida · `1605` empresa reactivada (operador y empresa, solo Ids)
  - `1606` operador creado · `1607` operador dado de baja
  - `1608` token de plataforma rechazado por la revalidación (operador dado de baja)
  - Un analista querría ver, por ejemplo, **un `1604` sin un `1601` reciente del mismo operador**, una ráfaga de `1602` sobre la misma IP, o un `1607` seguido de un `1608`.
- **Riesgos técnicos a verificar con tests reales:** (1) que el **esquema de plataforma y el de empresa no se puedan confundir** (otra clave y otra audiencia, probado con tokens firmados con la clave equivocada y con la audiencia equivocada); (2) que **suspender sea todo o nada** con la fila de auditoría (SQL Server real); (3) que la **revalidación por petición** corte de verdad a un operador dado de baja; (4) que **el recuento no filtre más de lo debido** (inspección del cuerpo completo de la respuesta); (5) que **el desafío no cruce de mundo** (R9).
- **Lo que esta spec NO protege (a decir con claridad):**
  - **Un operador comprometido** puede suspender cualquier empresa, es decir, dejar sin servicio a todos los clientes. Se mitiga con el MFA obligatorio, el bloqueo, la sesión de 15 minutos, la baja inmediata y la auditoría que no caduca; **no se elimina**. Una vez haya clientes de pago, convendrá la aprobación entre dos operadores (LATER).
  - El TOTP **no resiste al *phishing*** (lo resuelven las passkeys, NEXT de la spec 010).
  - **Quien controle el servidor o la base de datos** puede crear un operador: es inherente a que el alta sea fuera de la API.
  - **Un único operador** no tiene respaldo: si se pierden el móvil y los códigos, solo se recupera con acceso al servidor (D11).

## Decisiones técnicas relevantes

- Se apoya en: ADR-002 (esta spec es su **única excepción deliberada** y por eso se limita a metadatos), ADR-007, ADR-009, ADR-011, ADR-012, ADR-013, ADR-014.
- **¿Requiere una ADR nueva?** Sí: **ADR-016 (Propuesto)**, redactada junto a esta spec. Recoge la identidad aparte, la sesión con clave propia, el alta fuera de la API, la suspensión como estado de la empresa y la auditoría persistente.
- **Cambia ADR-009:** se amplía el catálogo de eventos de seguridad y se anota que existe una identidad con acceso global. Se anota al cerrar.
- **Cambia la declaración de autorización (ADR-012):** a «permiso o anónima» se le suma «operador de plataforma». Se anota al cerrar.
- **Cambia el contrato del login de empresa:** una empresa suspendida da **403** `tenant.suspended` tras credenciales correctas. Hoy no hay frontend, así que no rompe a nadie; se anota en la colección `.http` y en el manual.
- **Un detalle a resolver en el plan (bloque 4):** dónde vive el comando del servidor (un modo de arranque del propio host o un proyecto de consola aparte). Lo que la spec fija es el **comportamiento** (R2) y que no sea alcanzable por HTTP.

## Fuera de alcance / Backlog relacionado

- Plan o tarifa, motivo y aviso de suspensión, búsqueda por nombre, estadísticas, refresh token de operador, varios operadores y permisos entre ellos, aprobación entre dos operadores, vista de soporte, impersonar, borrar o exportar una empresa, panel con interfaz.
- Purga de filas de auditoría: **no se purgan** (es el sentido de D7). Si el volumen creciera, se archivarán, no se borrarán.
- La **Fase 2** del producto (agenda y asignación de técnicos) sigue a esta spec.

---

## Validación antes de aprobar

> ¿Esta funcionalidad ayuda a conseguir o mantener al primer cliente? (sección 84 del prompt maestro)

**Solo en parte, y conviene decirlo con honestidad.** Para **conseguir** al primer cliente no hace falta: un piloto no necesita que el operador vea la lista de empresas. Sí hace falta **antes de cobrar**: sin poder suspender una empresa por impago o abuso, y sin saber cuántas hay, no se puede operar un servicio de pago. Además es el único sitio donde el diseño tiene que romper su propia regla de aislamiento, y hacerlo **estrecho, con segundo factor y con rastro** es justo lo que un tribunal o un cliente exigente querrá ver defendido. Se plantea ahora, y no más tarde, porque el MFA y el bloqueo de la spec 010 acaban de dejar la base lista y porque añadirlo cuando ya haya datos reales sería más arriesgado. Si se prefiere priorizar la agenda (lo que ve un cliente), esta spec puede esperar sin perder nada, salvo la comodidad de tener el diseño ya decidido.
