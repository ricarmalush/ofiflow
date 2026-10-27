# ADR-016 — Panel de operador de la plataforma

**Estado:** Aceptado (2026-10-26)
**Fecha:** 2026-10-22
**Proyecto:** OfiFlow

---

## Contexto

OfiFlow aísla las empresas entre sí con una regla que no admite excepciones cómodas: ninguna cuenta ve datos de otra empresa (ADR-002, spec 007). Eso es lo que se quiere, pero deja un hueco operativo: **nadie puede ver qué empresas hay ni cortar el acceso a una**. Antes de cobrar a nadie hace falta poder suspender una empresa por impago, abuso o incidente, y saber cuántas existen.

Eso exige una cuenta que **sí** vea todas las empresas. Es la excepción más peligrosa del sistema: si se roba esa cuenta, se ven todas, y OfiFlow es **encargado del tratamiento** de los datos personales de los clientes de cada empresa. La MFA y el bloqueo de cuenta (ADR-014) existen precisamente como requisito previo de este panel.

Hay que decidir seis cosas:

1. **Qué es un operador** y cómo se relaciona con las cuentas y los roles que ya existen.
2. **Cómo nace** la primera cuenta con poder global.
3. **Cómo es su sesión**, de modo que no se confunda con la de una empresa.
4. **Qué puede ver y hacer.**
5. **Cómo se suspende una empresa** y qué provoca.
6. **Qué rastro deja cada acción.**

## Problema

Cada decisión tiene una opción cómoda que es peligrosa:

- Darle el poder a un **rol de empresa** (por ejemplo un `Owner` especial) mezcla la excepción con el aislamiento que protege y obliga a que cada comprobación de empresa tenga que acordarse de la excepción.
- **Crear el operador por la API** (aunque sea con un token de arranque) abre una ruta HTTP que fabrica la cuenta más poderosa.
- **Reutilizar el token de empresa** con un claim extra hace que un fallo de validación convierta un token de empresa en uno de plataforma.
- **Dejar ver «lo que haga falta para soporte»** convierte al operador en un acceso universal a datos personales sin base legal ni registro.
- **Fiarse solo del log** para la auditoría: con 14 días de retención (ADR-015) no se puede responder «¿quién suspendió mi empresa el mes pasado?».

## Opciones consideradas

### 1. Qué es un operador

**A. Un `TenantRole` nuevo.** Descartada: el rol vive dentro de una empresa (ADR-004) y el operador no pertenece a ninguna.

**B. Una columna booleana en la cuenta.** Descartada: un solo bit mal cambiado da acceso global y no se puede auditar ni proteger por separado.

**C. Una cuenta normal más una fila en una tabla aparte (`PlatformOperator`), sin pertenencia a ninguna empresa.** Reutiliza el hash de contraseña, el segundo factor y el bloqueo (todos sobre la cuenta), y la tabla se puede auditar y proteger por separado. **Una cuenta es de empresa o de plataforma, nunca de las dos.**

### 2. Cómo se crea

**A. Un endpoint con un token de arranque.** Descartada: una ruta HTTP que crea la cuenta más poderosa es superficie de ataque.

**B. SQL a mano.** Descartada: se salta el hash y el enrolado del segundo factor, y deja un operador sin MFA.

**C. Datos de arranque en la configuración.** Descartada: dejaría una contraseña en un fichero.

**D. Un comando del servidor** que crea la cuenta, enrola el TOTP y **exige un primer código válido**, todo o nada. Elegida: nunca existe un operador sin segundo factor y no hay ninguna ruta HTTP que lo cree.

**Dónde vive ese comando (decidido en el bloque 4, 2026-10-25).** Había dos formas: un modo de arranque de la propia API (`OfiFlow.Api --crear-operador`) o un programa de consola aparte. Se eligió el **proyecto aparte, `OfiFlow.Admin`**: la API no lo referencia, así que el alta es inalcanzable por HTTP sin depender de que nadie recuerde excluirla, y puede ejecutarse en un sitio distinto del que atiende peticiones públicas. Un modo de arranque habría dejado el código del alta en el mismo ejecutable que escucha en internet. La lógica vive en `Application` (`PlatformOperatorAdministration`, que no es una petición de MediatR) para probarla como el resto; el programa solo lee la terminal. Recibe la clave del segundo factor pero **no** la que firma las sesiones. Para hacer cierto el «todo o nada» sin transacciones manuales, el primer código TOTP se comprueba en memoria antes de guardar nada y todo se confirma en un único `SaveChanges`.

### 3. La sesión

**A. El mismo token con un claim `platform`.** Descartada: si la clave o la validación fallan, un token de empresa podría valer en plataforma.

**B. Un esquema de autenticación aparte, con otra audiencia y otra clave** (`Jwt:PlatformSecret`, sin valor por defecto: la API no arranca sin ella), de 15 minutos y sin refresh token. Elegida: los dos mundos no se pueden confundir ni siquiera con una firma correcta, y robar una sesión de operador da 15 minutos de acceso, no una renovación indefinida.

### 4. Qué ve y qué hace

**A. Datos de clientes y trabajos «para soporte», o impersonar.** Descartada por ahora: exige base legal RGPD y registro de cada acceso; es una spec propia cuando haya clientes de pago.

**B. Solo metadatos de empresa**: nombre, fecha de alta, número de miembros y estado, más suspender y reactivar. Elegida. No hay plan ni tarifa porque no existen en el modelo.

### 5. La suspensión

**A. Borrar o desactivar los datos.** Descartada: irreversible y más de lo necesario.

**B. Un estado de la empresa (`Tenant.SuspendedAt`) que se comprueba en cada petición** con la misma consulta que ya revalida la pertenencia (ADR-013). Elegida: surte efecto en la siguiente petición de cada miembro, no toca ningún dato y es reversible. Los refresh tokens no se borran: mientras dura la suspensión no sirven.

### 6. La auditoría

**A. Solo el log.** Descartada por la retención.

**B. Una tabla `PlatformAuditEntry` append-only** para lo que cambia algo (sesión de operador, suspender, reactivar, alta y baja), escrita **en la misma transacción** que la acción, con solo Ids, un código de acción y la hora; las lecturas quedan en el log. Elegida.

## Decisión

Se decide:

- Un **operador** es una cuenta más una fila en `PlatformOperator`, **sin** pertenencia a ninguna empresa.
- Se crea y se da de baja con un **programa del servidor aparte (`OfiFlow.Admin`)**, nunca por la API. El alta enrola y confirma el segundo factor en el mismo acto.
- Su sesión usa **esquema, audiencia y clave propios**, dura **15 minutos** y **no tiene refresh token**. El **segundo factor es obligatorio sin excepción**: el login de plataforma siempre devuelve un desafío.
- La autorización gana un **tercer tipo de declaración**, `[RequiresPlatformOperator]`, excluyente con permiso y con anónima y fail-closed como hoy, con **revalidación en la base de datos en cada petición**: dar de baja a un operador surte efecto en su siguiente petición.
- Ve **solo metadatos de empresa** y puede **suspender y reactivar**. La suspensión es un estado de la empresa que se comprueba en cada petición, en el login, en el refresh y al aceptar invitaciones.
- Cada acción que cambia algo deja una fila de **auditoría persistente** y un **evento de seguridad**; las lecturas, solo el evento.
- Detalle de la spec 011.

## Consecuencias

### Positivas
- Se puede operar un servicio de pago: cortar una empresa y contarlas.
- La excepción al aislamiento es **estrecha, con segundo factor y con rastro**, y está confinada a tres operaciones y a una ruta.
- Un token de empresa nunca vale en plataforma ni al revés.
- Un operador dado de baja deja de funcionar de inmediato.

### Negativas / riesgos aceptados
- **Un operador comprometido** puede suspender cualquier empresa, es decir, dejar sin servicio a todos los clientes. Se mitiga con el MFA, el bloqueo, la sesión corta, la baja inmediata y la auditoría que no caduca; no se elimina.
- **El bloqueo de cuenta** (ADR-014) permite a quien conozca el email del operador bloquearlo 15 minutos. Se acepta a cambio de que adivinar su contraseña sea inviable.
- **Un único operador no tiene respaldo**: si pierde el móvil y los códigos, solo se recupera con acceso al servidor (dar de baja y crear uno nuevo).
- **Quien controle el servidor o la base de datos** puede crear un operador: es inherente a que el alta sea fuera de la API.
- El **TOTP no resiste al *phishing*** (lo resuelven las passkeys, NEXT del ADR-014).
- La **auditoría no se purga**: si el volumen crece, se archiva.

### Qué queda cerrado (no se debe reabrir sin una razón nueva y explícita)
- Un operador **no** es un rol de empresa, **no** se crea por la API y **no** ve datos de clientes ni trabajos.

### Qué queda abierto para revisar más adelante (NEXT)
- Plan o tarifa, motivo de la suspensión y aviso por email.
- Vista de soporte e impersonar, con base legal RGPD y registro de cada acceso.
- Varios operadores con permisos distintos y aprobación entre dos para suspender.
- Refresh token de operador, si el uso lo justifica.
- Passkeys para el operador.

---

## Nota de estado (2026-10-26)

Implementada por la spec 011 (bloques 1 a 7). Lo que la implementación aclaró y que conviene tener presente:

- **El alta y la baja viven en un proyecto de consola aparte (`OfiFlow.Admin`)**, no en un modo de arranque de la API: la API no lo referencia, así que no hace falta acordarse de excluir nada para que no sea alcanzable por HTTP. No recibe `Jwt:Secret`. Su lógica está en `Application` (`PlatformOperatorAdministration`), que a propósito **no** es una petición de MediatR.
- **Tres sitios emiten sesiones** (el login sin segundo factor y los dos canjes de desafío) y un test de arquitectura lo fija. Los desafíos llevan una **audiencia** (empresa o plataforma) y uno de otro mundo se trata como inventado, antes de mirar el código y sin gastar intentos.
- **`Tenant` gana una versión de fila** (migración `AddTenantRowVersion`), no prevista en la spec: sin ella, dos suspensiones simultáneas de la misma empresa dejaban dos filas de auditoría. El perdedor recibe el error de negocio de siempre.
- **`TenantId` entra por la ruta** en suspender y reactivar (es el objetivo, un operador no tiene empresa): el test que prohíbe `TenantId` en las peticiones lo declara como excepción **nombrada** de esas dos operaciones y comprueba que son de plataforma.
- **El recuento de miembros es una subconsulta por empresa de la página** (hasta 100), con `IgnoreQueryFilters()` en la lista blanca; es el único sitio que cuenta miembros ajenos.
- **El mismo bloqueo de cuenta** protege al operador (50 intentos simultáneos hacen cinco comprobaciones reales de contraseña) y todos los fallos del login son el mismo 401 con el mismo coste.
- **Verificado contra la API publicada** (Kestrel, SQL Server en Docker, TOTP real calculado fuera de la aplicación): 77 comprobaciones, incluida la suspensión con el 403 del miembro, la auditoría consultada en la base de datos y la API negándose a arrancar sin `Jwt:PlatformSecret`.
- **Mutaciones sobre el código real** en todos los bloques (121 entre los bloques 2 y 7); las que sobrevivieron la primera vez (el desempate por `Id` del listado) obligaron a escribir el test que faltaba.
- **Riesgos aceptados que se mantienen:** un operador comprometido puede suspender cualquier empresa durante la vida de su sesión; el TOTP no resiste al *phishing*; quien controle el servidor o la base de datos puede crear un operador; la auditoría no tiene ruta de consulta y solo la protege la aplicación, no la base de datos.

---

## Relación con otros ADR

- Depende de: ADR-002 (multi-tenancy: esta es su única excepción deliberada), ADR-004 (el rol vive en la pertenencia), ADR-007 (credenciales y refresh tokens), ADR-009 (seguridad base), ADR-012 (autorización: se amplía con un tercer tipo de declaración), ADR-013 (revalidación por petición), ADR-014 (MFA y bloqueo: requisito previo).
- Relacionada con: ADR-015 (la retención del log es la razón de la auditoría persistente).
