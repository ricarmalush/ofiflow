# Spec — 021-crear-cuenta

**Estado:** Implementada (2026-11-06; API y frontend). Decisiones D1–D15 aprobadas tal como estaban escritas; lo que cambió al implementar está en la [ADR-023](../../adr/ADR-023-crear-una-cuenta.md) y en [`tasks.md`](./tasks.md)
**Fecha:** 2026-11-06
**Bounded Context:** Identity (alta de una empresa y de su primer usuario) y Tenancy
**Depende de:** spec 002 (el registro actual y el `Owner`), spec 017 y ADR-020 (el puerto de correo y los enlaces), spec 018 y ADR-021 (el patrón de «respuesta idéntica + suelo de tiempo + cola de correo en memoria + token de un solo uso»), spec 009 y ADR-013 (la invitación: el otro camino para tener cuenta), ADR-019 (la purga en segundo plano), ADR-009 (límites, eventos y datos personales)

> **Numeración:** 021 provisional. La 019 sigue reservada a «cambiar la contraseña estando dentro» (ADR-021, spec 018).

---

## Objetivo

Que **una persona pueda crear la cuenta de su empresa desde la web, sola**, y que para ello tenga que demostrar que el buzón del email es suyo.

Hoy la API tiene `POST /auth/register`, pero el frontend **no tiene pantalla para usarlo** (comprobado el 2026-11-06): la primera persona de una empresa nueva solo puede crearse llamando a la API a mano. Para un primer cliente real eso bloquea todo lo demás del flujo de valor: sin cuenta no hay clientes, trabajos, agenda ni facturas. Y el registro actual tiene el límite conocido 3 del capítulo 6 del manual: **responde «ese email ya está registrado»**, que entrega la lista de clientes a quien pruebe emails.

## Alcance — Qué SÍ incluye

Seis bloques:

1. **La base:** una tabla de **registros pendientes** (lo que la persona escribió hasta que abre el enlace), su servicio (pedir y canjear, como el de recuperar la contraseña), las condiciones de uso aceptadas guardadas en `User`, y la purga de los pendientes caducados.
2. **Pedir el registro:** `POST /auth/registration` (anónimo). Responde **siempre `202` sin cuerpo y con las mismas cabeceras**, exista ya la cuenta o no. Si el email es nuevo, encola el correo con el enlace; si ya tiene cuenta, encola un aviso («ya tienes cuenta»). **No crea ninguna cuenta.**
3. **Completar el registro:** `POST /auth/registration/complete` (anónimo). Con el token del enlace y una **contraseña elegida ahora**, crea de una vez la empresa, el usuario y su pertenencia como `Owner` (lo que hacía el registro), en una sola confirmación. No abre sesión. Y las **condiciones de uso** también al aceptar una invitación.
4. **Retirar el registro inmediato** (`POST /auth/register`) y **migrar los tests, la colección `.http` y el manual de puesta en marcha** a la nueva forma.
5. **Frontend:** «¿No tienes cuenta? Crea una» en el acceso, la pantalla de registro, la de completar (con el token en el fragmento de la URL) y la casilla de condiciones en la de invitación.
6. **Documentación:** ADR-023, anotaciones en ADR-007, ADR-009, ADR-013 y ADR-020, y el manual.

## Alcance — Qué NO incluye

- **NEXT:** **segundo factor obligatorio para `Owner` y `Admin`** (límite 3b de ADR-014). El primer usuario de cada empresa nace sin él; con el registro abierto gana importancia, pero es otra spec con sus pantallas.
- **NEXT:** **cambiar la contraseña estando dentro** (spec 019).
- **NEXT:** **que las cuentas ya existentes acepten las condiciones.** Hoy son cuentas de prueba; el día que haya clientes reales hará falta una pantalla de «hemos actualizado las condiciones» con la versión nueva.
- **NEXT:** **que una cuenta pertenezca a varias empresas** (hoy cada cuenta, a una).
- **LATER:** una comprobación anti-robots (CAPTCHA o similar) y **registrarse con Google o Microsoft**.
- **LATER:** correo en varios idiomas; que el correo diga el nombre de la empresa o de la persona.
- **LATER:** eliminar la cuenta y los derechos del RGPD (acceso, borrado) como funciones de la aplicación.
- **NEVER:** **crear la cuenta antes de que el buzón se haya demostrado** (D1) y **enviar una contraseña por correo**.
- **NEVER:** decir si un email ya tiene cuenta (R1).

## Reglas de negocio / invariantes

**Pedir el registro (`POST /auth/registration`)**

- **R1. Siempre la misma respuesta.** `202` sin cuerpo y con las mismas cabeceras (`Cache-Control: no-store`) en **todos** los casos: email nuevo, email que ya tiene cuenta, email de un operador de la plataforma, límite superado, correo apagado o condiciones sin configurar. Quien llama no averigua nada. Solo cambia lo que ocurre detrás.
- **R2. El tiempo también es el mismo.** Un **suelo de tiempo** aplicado **después** del trabajo (como en la spec 018, D2), con su propia opción `Registration:ResponseFloor` (por defecto 250 ms, validada al arrancar). Se **mide con muchas muestras** (con y sin cuenta) al implementar, antes de darlo por bueno.
- **R3. Entrada.** `companyName` (obligatorio, el máximo de `Tenant.NameMaxLength`), `userName` (obligatorio, el máximo de `User.NameMaxLength`), `email` (formato común y longitud máxima) y `acceptedTermsVersion` (obligatorio, **igual a la versión vigente** configurada). Un formato inválido da `400` de campo: depende solo de lo escrito, nunca de si el email existe. **No hay contraseña en esta petición** (D5).
- **R4. Email nuevo:** se crea un **registro pendiente** (empresa, nombre, email, la versión y la fecha de aceptación, el **hash** de un token de 256 bits y su caducidad) y se **encola** el correo con el enlace `{Email:LinkBaseUrl}/auth/complete-registration#token=…`. El token **no se guarda en claro** y no se devuelve.
- **R5. Un solo pendiente vigente por email.** Pedir otra vez **sustituye** el anterior (queda anulado). **Volver a pedir es reenviar**: no hay un endpoint aparte.
- **R6. Email que ya tiene cuenta** (de una empresa o de un operador de la plataforma): no se crea nada y se **encola un aviso fijo** («alguien ha intentado crear una cuenta con este email; si fuiste tú, inicia sesión o recupera tu contraseña»), con un enlace **sin token** a la pantalla de recuperar la contraseña.
- **R7. Límites.** `3` por hora y por IP (el que ya tiene el registro) y **3 correos por hora y por email, en silencio** (R1: no se entera nadie; lo que sobra no se envía). Valores iniciales que se revisan con datos reales. *Aclarado al implementar el bloque 2:* el tope por email cubre también el **aviso «ya tienes cuenta»** (R6), que no deja ninguna fila que contar; ese se limita **en memoria y por instancia**, como las invitaciones, y su clave es una huella del email.
- **R8. Sin correo o sin condiciones configuradas, el registro está cerrado:** `202`, nada se crea y queda un `Warning`. Es *fail-closed*: abrir el registro exige configurar **a la vez** el correo (ADR-020) y `Legal:TermsVersion`.
- **R9. Caducidad:** el enlace vale **24 horas** (más que el de recuperar, que vale 1 hora: quien se registra puede tardar en abrir el correo, y no hay ninguna cuenta en juego hasta que lo abre).

**Completar el registro (`POST /auth/registration/complete`)**

- **R10.** Con el token y `newPassword` (8 a 128, como siempre): en **una sola confirmación** se **canjea el pendiente** y se crean la empresa, el usuario y su pertenencia como `Owner`, con la versión y la fecha de las condiciones aceptadas. Todo o nada.
- **R11.** **Un solo error para un token que no sirve:** `400 registration.invalid` (desconocido, caducado, ya usado, sustituido, o **el email ya tiene cuenta desde que se pidió**). El motivo real solo queda en el registro.
- **R12.** **No abre sesión:** responde `204` y el frontend lleva al acceso con un aviso. Mismo criterio que restablecer la contraseña y aceptar una invitación.
- **R13.** **Dos peticiones a la vez con el mismo token: gana una** (la versión de fila decide); la otra recibe `registration.invalid` y no deja nada.
- **R14.** La contraseña nueva **nunca** aparece en registros, eventos ni errores; tampoco el token ni el email.
- **R15.** Completar tiene su propio límite: `10` por hora y por IP (hay un hash PBKDF2 que pagar en cada intento).

**Condiciones de uso**

- **R16.** Se guardan en `User` la **versión** y la **fecha** de aceptación. La aceptación va en la petición de pedir el registro (R3) y en la de aceptar una invitación (D8); sin ella, `400`.
- **R17.** Qué dicen las condiciones y la política de privacidad **no lo decide el código**: lo redacta el dueño del proyecto. La API solo compara la versión.

**Datos pendientes**

- **R18.** Los registros pendientes **caducan y se purgan**: la purga que ya existe (ADR-019) borra los pendientes caducados hace más de 7 días, por lotes, con un evento (R20). Son datos personales (nombre y email de alguien que aún no tiene cuenta) y no se guardan más de lo necesario.
- **R19.** Las cuentas que ya existen **no cambian**, y el campo `EmailConfirmed` de Identity **no se usa** (D12): la garantía es otra, que **una cuenta solo nace después de abrir el enlace del buzón**.

**Eventos de seguridad** (familia `10xx`, los números se fijan al implementar cada bloque)

- **R20.** Pedido (con si el email ya tenía cuenta, **solo en el registro**, nunca el email), pedido limitado, registro completado (empresa y usuario), registro rechazado (el motivo real) y purga de pendientes. Ninguno lleva el email, la contraseña, el token ni el enlace.

## Modelo conceptual

```
PendingRegistration  (cuenta global; sin TenantId: aún no hay empresa)
- Id
- Email                  (el normalizado; único entre los vigentes)
- CompanyName, UserName
- TermsVersion, TermsAcceptedAt
- TokenHash              (único; el token no se guarda)
- CreatedAt, ExpiresAt, UsedAt?
- RowVersion             (el canje concurrente)

User (Domain)            + TermsVersion?, TermsAcceptedAt?

Sin cambios en ApplicationUser, Tenant ni TenantUser: el canje usa lo que ya hacía el registro.
```

## Criterios de aceptación

- Dado un email nuevo, cuando se pide el registro, entonces responde `202` sin cuerpo, **no hay cuenta ni empresa**, hay **un registro pendiente** con solo el hash del token, y se encola **un correo** con el enlace al frontend (nunca al `Host` ni al `Origin` de la petición).
- Dado un email que ya tiene cuenta, cuando se pide el registro, entonces responde **exactamente igual** (estado, cuerpo y cabeceras), no se crea ningún pendiente y se encola **el aviso**, sin token.
- Dados los dos casos anteriores, cuando se miden con muchas muestras, entonces el tiempo de respuesta **no es distinguible** (o, si lo era, hay un suelo que lo oculta).
- Dado un pedido repetido del mismo email, entonces el pendiente anterior **deja de valer** y solo sirve el último enlace.
- Dado el cuarto pedido de la hora para un mismo email, entonces responde `202` pero **no se crea ni se envía nada**; y el sexto desde una IP da `429`.
- Dado un correo apagado, o `Legal:TermsVersion` sin configurar, entonces responde `202`, no crea nada y deja un `Warning`.
- Dada una versión de condiciones distinta de la vigente, o ausente, entonces `400` de campo y no se crea nada.
- Dado un token válido y una contraseña de 8 a 128 caracteres, cuando se completa, entonces se crean la empresa, el usuario y su pertenencia como `Owner`, con la versión y la fecha de las condiciones, el pendiente queda canjeado, y **después se puede iniciar sesión** con esa contraseña (y no antes).
- Dado un token desconocido, caducado, ya usado, sustituido por otro pedido, o cuyo email ya tiene cuenta, entonces el **mismo** `400 registration.invalid`, con la misma respuesta completa, y no cambia nada.
- Dada una contraseña corta o larga, entonces `400` de campo **sin gastar el token** (se puede reintentar con el mismo enlace).
- Dadas dos peticiones simultáneas con el mismo token, entonces se crea **exactamente una** cuenta y la otra recibe `registration.invalid`.
- Dado alguien que registró el email de otra persona **sin acceso a su buzón**, entonces **no consigue nada**: no hay cuenta, y la víctima, si abre el enlace, **elige ella la contraseña**.
- Dado un pendiente caducado hace más de 7 días, cuando corre la purga, entonces se borra y los vigentes o recientes no.
- Dada una invitación aceptada sin la versión de condiciones, entonces `400`; con ella, queda guardada en el `User`.
- Dados los registros de todo el flujo, cuando se revisan, entonces **no aparece** ningún email, contraseña, token ni enlace.
- Dada la operación vieja `POST /auth/register`, entonces **ya no existe**.

Caso multi-tenant obligatorio: el pedido y el canje **no leen ni tocan datos de ninguna empresa existente** (el pendiente no tiene `TenantId`), y el token de un pendiente **no sirve** para otro (el hash identifica la fila y la fila, un email).

## Seguridad (ADR-009)

- **Datos personales:** el **nombre**, el **nombre de la empresa** y el **email** de quien se registra se guardan en el pendiente **hasta que se canjea o se purga** (como mucho 24 h de vigencia más 7 días de margen), y la **dirección** sale hacia el proveedor de correo (ya es encargado del tratamiento desde la spec 017). La **contraseña** llega al canjear, por HTTPS, y solo existe en memoria hasta hashearla. La versión y la fecha de las condiciones son datos del usuario. **Ninguno** puede aparecer en logs, eventos ni errores.
- **Autorización:** las dos operaciones son **anónimas** (quien se registra no tiene sesión). No hay permisos nuevos. Quien tiene el token tiene el derecho, por eso es de 256 bits, caduca y se consume.
- **Entrada:** `companyName`, `userName` y `email` con sus `MaximumLength` (las constantes de Domain que usa también EF); `token` con la forma exacta de `OpaqueToken` antes de buscar nada; `newPassword` de 8 a 128 (un máximo evita enviar contraseñas enormes para agotar CPU con PBKDF2); `acceptedTermsVersion` con longitud máxima.
- **Aislamiento:** una entidad nueva, **global** (no es de ninguna empresa todavía). Va a la lista de entidades globales del test de arquitectura (spec 007) con su motivo, y su lectura queda restringida a un solo fichero.
- **SQL crudo:** ninguno.
- **Abuso:** **sí, doble**: el pedido envía correo (se puede usar para inundar un buzón ajeno) y el canje paga un hash. Tres políticas: 3 por hora y por IP para pedir, 10 por hora y por IP para canjear, y el límite **silencioso por email** que protege a la **víctima** (el de IP no basta: un atacante tiene muchas).
- **Eventos de seguridad:** R20.
- **Enumeración de cuentas:** esta spec **cierra** el límite 3 del capítulo 6 (el registro ya no dice «ese email ya está registrado») y **no abre otros**: la respuesta, las cabeceras y el tiempo son los mismos (R1, R2). Queda **otro** hermano que no se toca aquí: invitar a un email que ya tiene cuenta responde un error (`InviteUserCommandHandler`), pero lo ve **quien invita**, que está autenticado y es de la empresa.
- **Modelo de amenazas del buzón:** quien controla el buzón puede abrir el enlace y **elegir la contraseña de una cuenta nueva**: es lo que se quiere. Quien **no** lo controla no obtiene nada (D1). La cuenta que nace así **no tiene segundo factor** (NEXT), con el mismo límite que cualquier cuenta de hoy.
- **Spam de registros falsos:** el límite por IP frena la mayoría; no frena a quien rota IP. Sin cuenta ni empresa creadas hasta abrir el enlace, **lo que se acumula es solo la tabla de pendientes**, que se purga.

## Decisiones técnicas relevantes

- **D1. El flujo es «pedir → enlace → completar»: no existe ninguna cuenta hasta que se abre el enlace.** *Recomendado, y es la decisión que más condiciona la spec.* La alternativa obvia es **crear la cuenta al registrarse, sin confirmar**, y bloquear el login hasta confirmar. Se descarta porque abre el *pre-hijacking*: quien registra el email de **otra persona** deja una cuenta con **su** contraseña, y si la víctima pulsa el enlace de confirmación, el atacante ya controla la cuenta. Además obliga a: bloquear el login (un caso más que no se confunda con «contraseña mala»), purgar empresas y usuarios sin confirmar (con el riesgo de borrar lo que no toca) y migrar el campo `EmailConfirmed`. Con D1 desaparecen las tres.
- **D2. Siempre la misma respuesta, el tiempo medido, y un aviso si el email ya tiene cuenta.** *Recomendado.* Mismo patrón que `forgot-password` (spec 018 D2): `202` sin cuerpo, suelo de tiempo medido y no supuesto. El aviso («ya tienes cuenta») es lo que evita que quien teclea bien su email y ya estaba registrado se quede esperando un correo que no llega.
- **D3. El token: 256 bits, 24 horas, un solo uso, uno vigente por email.** *Recomendado.* Como el de recuperar y el de invitar (ADR-013, ADR-021): solo el hash en la base. 24 h y no 1 h porque no hay ninguna cuenta en juego hasta abrirlo y la persona puede tardar. Volver a pedir sustituye el anterior y hace de «reenviar».
- **D4. Los datos pendientes van en una tabla, no en un token firmado.** *Recomendado.* La alternativa (meter empresa, nombre y email en un token firmado, sin tabla) evita guardar nada, pero **no permite** que el enlace valga una sola vez, ni que haya uno vigente por email, ni medir el límite por email. Se paga guardar 24 h unos datos personales mínimos, con purga.
- **D5. La contraseña se elige al completar, no al pedir.** *Recomendado.* Es lo que mata el *pre-hijacking* aunque alguien abra un enlace que no pidió, y evita guardar una contraseña (ni su hash) de alguien que aún no ha demostrado nada. El coste: una pantalla más (la de completar), que sigue el modelo de la de recuperar la contraseña.
- **D6. Completar no abre sesión.** *Recomendado.* Como restablecer y aceptar una invitación: se entra con el acceso de siempre (con su segundo paso si lo hubiera). Es lo que impide que un enlace de correo sea una llave de entrada.
- **D7. Las condiciones: la versión y la fecha, en `User`; la versión llega en la petición y debe ser la vigente.** *Recomendado.* La API **no** guarda los textos ni decide qué dicen (R17); compara una cadena (`Legal:TermsVersion`, por ejemplo `2026-11`). Una persona que acepta una versión vieja (un formulario antiguo abierto desde ayer) recibe un `400` en vez de aceptar sin saber qué. Las **URL** de los textos las lleva el frontend (`environment`).
- **D8. También al aceptar una invitación.** *Recomendado, y la decisión de alcance a revisar.* Toda persona que crea una cuenta acepta las condiciones, no solo quien registra la empresa. Cuesta un campo más en `AcceptInvitationCommand` y una casilla en su pantalla. Alternativa: dejar las invitaciones para una spec posterior (más corta esta, pero hay cuentas nuevas sin aceptar condiciones).
- **D9. Tres límites: 3 por hora y por IP para pedir, 3 correos por hora y por email en silencio, 10 por hora y por IP para completar.** *Recomendado.* Valores iniciales, a revisar con datos. El de IP puede frenar a una oficina entera que comparta salida a internet (el mismo límite que ya tenía el registro); se anota como riesgo, no se resuelve aquí.
- **D10. Sin correo o sin condiciones configuradas, el registro está cerrado (`202` sin crear nada y un `Warning`).** *Recomendado.* Es *fail-closed*: lo contrario (crear cuentas ya confirmadas cuando no hay correo) dejaría abierta una cuenta sin demostrar nada si en producción se olvida configurar el correo. En desarrollo se prueba con **Mailpit**, como la recuperación de contraseña (capítulo 1).
- **D11. Se retira `POST /auth/register`.** *Recomendado.* Dos formas de crear una cuenta serían dos formas de saltarse la confirmación. Lo usan solo los tests (10 ficheros, 13 llamadas), la colección `.http` y la documentación; el frontend nunca lo usó. Los tests pasan a un **ayudante** que pide el registro, lee el enlace del **correo capturado** (como ya hace `ForgotPasswordEndToEndTests`) y lo completa. Es un cambio de contrato sin clientes que lo sufran, y **el más laborioso de la spec**: por eso es un bloque aparte (4).
- **D12. Las cuentas existentes no cambian y `EmailConfirmed` de Identity no se usa.** *Recomendado.* Hoy vale `false` en todas y ningún código lo lee; ponerlo a `true` no aportaría nada, porque la garantía es **estructural** (una cuenta solo nace tras abrir el enlace). Un límite honesto: una invitación aceptada **sin correo configurado** (el token se le entrega a quien invita, spec 017) tampoco demuestra el buzón; con correo, sí.
- **D13. Eventos de seguridad** en la familia `10xx`, a continuación de `1017`: pedido, pedido limitado, completado, rechazado (motivo real) y purga. *Recomendado.* Los números exactos se fijan al implementar.
- **D14. Frontend:** el enlace «¿No tienes cuenta? Crea una» en el acceso; `/auth/register` (empresa, nombre, email y la casilla de condiciones con sus dos enlaces) que **siempre** dice lo mismo tras pedir («Si el email es válido, te hemos enviado un enlace…», sin afirmar nada); `/auth/complete-registration` (el token se lee del fragmento y se borra de la barra de direcciones; dos campos de contraseña con las reglas de la API); y, tras completar, al acceso con un aviso. *Recomendado.* Es el modelo de las pantallas de la spec 018.
- **D15. Orden de trabajo:** la base (**bloque 1**), pedir (**2**), completar y las condiciones de la invitación (**3**), retirar el registro viejo y migrar tests y documentación de puesta en marcha (**4**), frontend (**5**) y documentación (**6**). *Recomendado.* Los bloques 1 a 3 **añaden** sin quitar nada (el registro viejo sigue funcionando), así que cada uno termina en verde; el 4 es el único que rompe un contrato.
- **¿Requiere ADR nueva? Sí: ADR-023**, «Crear una cuenta: sin cuenta hasta abrir el enlace», con el modelo de amenazas del *pre-hijacking* (D1 y D5), la respuesta uniforme (D2) y qué queda abierto. Con anotaciones «Ampliada por» en ADR-007, ADR-009, ADR-013 y ADR-020.

## Fuera de alcance / Backlog relacionado

- Segundo factor obligatorio para `Owner` y `Admin`.
- Cambiar la contraseña estando dentro (spec 019).
- Que las cuentas existentes acepten las condiciones, y una pantalla de «condiciones nuevas».
- Una cuenta en varias empresas.
- Anti-robots y registrarse con un proveedor externo.
- Eliminar la cuenta y los derechos del RGPD como funciones de la aplicación.
- Invitar a un email que ya tiene cuenta responde un error: lo ve quien invita; se revisa con «una cuenta en varias empresas».

---

## Validación antes de aprobar

> ¿Esta funcionalidad ayuda a conseguir o mantener al primer cliente?

Sí, y **bloquea el piloto**: un cliente que no puede crearse una cuenta por sí mismo no puede ni empezar. Es la primera pantalla del producto, antes de cualquier cliente, trabajo o agenda. Y es la que más se ataca después del acceso: un registro abierto sin cuidado regala la lista de clientes (hoy) y cuentas ajenas (con una confirmación mal pensada). Por eso se hace **antes** de abrir nada a clientes reales, con el modelo de amenazas por delante.
