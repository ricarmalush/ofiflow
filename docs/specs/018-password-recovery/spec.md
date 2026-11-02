# Spec — 018-password-recovery

**Estado:** Aprobada. Decisiones D1–D15 aprobadas el 2026-11-02 tal como estaban escritas
**Fecha:** 2026-11-02
**Bounded Context:** Identity (credenciales y sesiones)
**Depende de:** spec 017 y ADR-020 (el puerto de correo y los enlaces), spec 010 y ADR-014 (el bloqueo de cuenta y el segundo factor), specs 013–015 (cerrar sesiones: `RevokeAllRefreshTokensAsync`, el access token ligado a su sesión), spec 016 y ADR-019 (la purga y el primer servicio en segundo plano), ADR-004 y ADR-007 (credenciales y hash de contraseñas), ADR-009 (datos personales fuera de los logs, eventos, límite de peticiones)

---

## Objetivo

Que quien **olvida su contraseña** pueda volver a entrar **sin que nadie de OfiFlow tenga que intervenir**.

Hoy no hay ninguna vía: una contraseña olvidada es una cuenta perdida. Para un primer cliente real eso es insostenible: cada olvido sería una llamada al desarrollador. Y es el segundo uso del correo (spec 017), el que más importa a la seguridad: **quien controla el buzón de una cuenta puede controlar la cuenta**, así que esta spec es sobre todo un modelo de amenazas bien resuelto, no solo un formulario.

## Alcance — Qué SÍ incluye

Cinco bloques:

1. **La base:** una tabla de tokens de restablecimiento y **una cola de correo en memoria** con su servicio en segundo plano (D3). Sin ningún endpoint todavía.
2. **Pedir el restablecimiento:** `POST /auth/forgot-password` (anónimo). Responde siempre lo mismo, exista o no la cuenta, y si existe, encola el correo con el enlace.
3. **Restablecer:** `POST /auth/reset-password` (anónimo). Con el token y una contraseña nueva: cambia la contraseña, **cierra todas las sesiones**, reinicia el bloqueo de cuenta y avisa por correo.
4. **Frontend:** el enlace «¿Has olvidado tu contraseña?» en el login y las dos pantallas.
5. **Documentación:** ADR-021, anotaciones en ADR-007, ADR-009, ADR-014 y ADR-020, y el manual.

## Alcance — Qué NO incluye

- **NEXT:** **cambiar la contraseña estando dentro** (spec 019). Es otra operación: exige la contraseña actual, no un correo, y tiene sus propias reglas. Comparte con esta la regla de cerrar las demás sesiones.
- **NEXT:** **confirmar el email** al registrarse. Cierra el límite conocido de que el registro revela si un email está en uso.
- **NEXT:** **cola de correo persistida con reintentos.** La de esta spec es en memoria a propósito (D3); la persistida llegará con el primer correo que no lleve un secreto.
- **NEXT:** **recuperar la contraseña de un operador de la plataforma.** Es otra identidad (otra tabla, otra clave: spec 011) y se gestiona con la consola de administración del servidor.
- **LATER:** comprobar la contraseña nueva contra listas de contraseñas filtradas, y una política más exigente que la del registro (8 a 128 caracteres).
- **LATER:** correo en varios idiomas.
- **NEVER:** **preguntas de seguridad** («¿nombre de tu primera mascota?»): son contraseñas débiles que el atacante puede investigar.
- **NEVER:** **enviar la contraseña** (ni una temporal) por correo. Nadie, ni OfiFlow, debe conocer la contraseña de otra persona.
- **NEVER:** decir si un email tiene cuenta (R1).

## Reglas de negocio / invariantes

**Pedir el restablecimiento**
- **R1.** `POST /auth/forgot-password` responde **`202` con el mismo cuerpo vacío en todos los casos**: la cuenta existe, no existe, está bloqueada, es de una empresa suspendida, es de un operador de la plataforma, el correo está apagado o se superó el límite por cuenta. Solo un email **mal formado** da `400`, y eso no revela nada (se decide con el texto recibido, sin consultar nada).
- **R2.** **No se envía el correo dentro de la petición.** Se **encola en memoria** y otra tarea lo envía (D3). Así el tiempo de respuesta no depende de si la cuenta existe: enviar correo en la petición tardaría segundos justo y solo en el caso «existe», y ese tiempo delataría la cuenta. La diferencia de tiempo entre «existe» y «no existe» se **mide** al implementar y, si se distingue, se añade un suelo de tiempo (D2).
- **R3.** El **token** es un `OpaqueToken` (256 bits aleatorios, ADR-013): solo se guarda su **hash SHA-256**, **caduca a la hora** y **se usa una sola vez**. Una cuenta tiene **como mucho un token vigente**: pedir otro anula los anteriores.
- **R4.** **Dos límites**, porque abusan de cosas distintas. Por **IP**, una política más del limitador global: **5 por hora** (es anónima). Y **por cuenta, en silencio**: como mucho **3 tokens por hora y por cuenta**, cuenten desde la IP que sea; las siguientes peticiones reciben el mismo `202` pero **no crean token ni encolan nada**. El primero evita que una IP pruebe muchos emails; el segundo, que alguien llene de correos el buzón de una víctima desde muchas IPs. Que sea silencioso es lo que impide usarlo como oráculo.
- **R5.** El correo lleva el enlace `{Email:LinkBaseUrl}/auth/reset-password#token=…` (el token en el **fragmento**, como en las invitaciones; R3 de la spec 017: nunca se construye con datos de la petición), un **asunto fijo** y un texto fijo **sin ningún dato de la persona** (ni nombre ni empresa), con la frase de siempre: *si no fuiste tú, ignóralo*.
- **R6.** Sin correo configurado (`Email:Provider = None`) la petición responde `202` igual, **no crea el token** (no habría cómo entregarlo) y deja un `Warning` en el registro. No es un error: es el mismo comportamiento de «el correo está apagado» de la spec 017.

**Restablecer**
- **R7.** `POST /auth/reset-password` recibe `{ token, newPassword }`. Valida primero la **forma** del token (`OpaqueToken.IsWellFormed`) y la contraseña con **las mismas reglas del registro** (8 a 128 caracteres): lo que no cumple da `400` de validación, sin consultar nada.
- **R8.** **Un solo error** para un token desconocido, caducado, ya usado o anulado: **`400 password_reset.invalid`**. Quien lo intenta no aprende cuál de los cuatro es (como `invitation.invalid`, spec 009 R7). El motivo real va al registro, sin el token.
- **R9.** Un restablecimiento correcto, **todo o nada en una sola transacción**: (a) guarda el hash de la **contraseña nueva** (el mismo PBKDF2 del registro); (b) **consume el token** y anula los demás de la cuenta; (c) **revoca todos los refresh tokens** de la cuenta, en todas sus empresas (spec 014): sus access tokens dejan de valer en la petición siguiente (spec 015), también el de quien tuviera la contraseña vieja; (d) **pone a cero el contador de intentos y el bloqueo** de la cuenta.
- **R10.** **Dos restablecimientos a la vez con el mismo token: gana uno.** El token lleva una columna de versión de fila (`rowversion`) y la perdedora recibe `password_reset.invalid`, como en aceptar una invitación.
- **R11.** El restablecimiento **no toca el segundo factor** (D7). Una cuenta con MFA sigue pidiendo el código al entrar: **controlar el buzón basta para cambiar la contraseña, no para entrar**. Perder el móvil y los códigos de recuperación sigue resolviéndose como en la spec 010.
- **R12.** Tras restablecer, se **encola un correo de aviso** («tu contraseña se ha cambiado; si no fuiste tú, ...») con asunto y texto fijos. Lo que hace sospechoso un restablecimiento que la persona no pidió es enterarse.
- **R13.** Restablecer **no desbloquea nada que no sea de la cuenta**: una empresa suspendida (spec 011) sigue sin poder entrar aunque su contraseña cambie. Sí se reinicia el **bloqueo de cuenta** (R9d), porque quien demuestra que controla el buzón no es el que probaba contraseñas.

**La cola de correo y su servicio**
- **R14.** Una **cola acotada en memoria** (100 mensajes) y un **único consumidor**, el segundo servicio en segundo plano de OfiFlow (después de la purga, ADR-019). Lo que cumple, como la purga: **no tumba la API** (un fallo se registra y sigue con el siguiente), no arranca justo al iniciar, se detiene limpiamente con la API, tiene opciones validadas al arrancar, y **no corre en la consola de administración**.
- **R15.** **Sin reintentos** y **sin persistencia**. Si la cola se llena, el mensaje nuevo se **descarta y se registra**, sin bloquear la petición ni cambiar la respuesta. Si la API se reinicia, lo pendiente se pierde: la persona vuelve a pedirlo. A cambio, **el token no está en claro en ningún sitio persistente** (solo en la memoria del proceso durante segundos): es la razón de no usar una cola en la base de datos (D3).
- **R16.** Un fallo al enviar (el servidor de correo caído, sin credenciales) deja un evento con la **categoría** del fallo (la de `EmailDeliveryException`, spec 017), **nunca** la dirección ni el texto del servidor.

**Datos y purga**
- **R17.** La tabla de tokens de restablecimiento **no es de una empresa**: la cuenta es global (como `RefreshTokens`). Cada fila guarda el Id de la cuenta, el hash, la creación, la caducidad y el uso; **nunca** el email.
- **R18.** La **purga existente** (ADR-019) borra también los tokens de restablecimiento caducados hace más de los mismos 7 días, en el mismo ciclo. Sin eso la tabla crecería sin límite (D11).

## Modelo conceptual

```
PasswordResetToken (Domain.Identity · cuenta global, no de una empresa)
- Id
- UserId            la cuenta (mismo Id que ApplicationUser y User)
- TokenHash         SHA-256 del token; índice único. El valor en claro no se guarda nunca
- CreatedAt
- ExpiresAt         CreatedAt + 1 hora
- UsedAt?           cuándo se consumió o se anuló (null = vigente)
- RowVersion        control de concurrencia (dos restablecimientos a la vez: gana uno)

Application
- IEmailQueue       Enqueue(EmailMessage) → bool (false si se descartó); sin esperar al envío
- IEmailLinks       + ResetPassword(token), con la misma regla que AcceptInvitation (solo la configuración)
- PasswordResetEmail / PasswordChangedEmail   las dos plantillas (funciones puras)
- ForgotPasswordCommand, ResetPasswordCommand (y sus Validators y Handlers)

Infrastructure
- EmailDispatchService      el consumidor de la cola; usa IEmailSender
- IdentityService           + SetPasswordAsync(userId, password): hash + contador a cero, sin confirmar (lo confirma el Handler)
```

## Criterios de aceptación

- Dado un email **con cuenta**, cuando se pide el restablecimiento, entonces `202` y el servidor de correo recibe **un** mensaje con el enlace; y dado un email **sin cuenta**, entonces `202`, **la misma respuesta byte a byte** y ningún correo.
- Dado un email con cuenta bloqueada, de una empresa suspendida, de un operador o con el correo apagado, entonces `202` idéntico y ninguna diferencia visible.
- Dado un email mal formado, entonces `400` de validación y nada más.
- Dado un correo recibido, cuando se abre su enlace y se envía una contraseña nueva válida, entonces `204`, **se puede entrar con la contraseña nueva**, **la vieja ya no vale**, y todas las sesiones de la cuenta quedan cerradas (sus access tokens dan `401`).
- Dado un token **usado, caducado (a la hora), anulado por otro pedido o inventado**, entonces **el mismo** `400 password_reset.invalid`, sin pista de cuál.
- Dado un token con forma mala o una contraseña de 7 o de 129 caracteres, entonces `400` de validación.
- Dadas dos peticiones simultáneas con el mismo token, entonces gana una (`204`) y la otra recibe `password_reset.invalid`; la contraseña es la de la ganadora.
- Dado el cuarto pedido de una hora para la misma cuenta, desde cualquier IP, entonces `202` idéntico, **ningún token nuevo y ningún correo**.
- Dado el sexto pedido de una hora desde la misma IP, entonces `429`.
- Dado un nuevo pedido, entonces el token anterior de esa cuenta deja de valer (un solo vigente).
- Dada una cuenta **con segundo factor**, cuando restablece su contraseña, entonces el login con la contraseña nueva **sigue pidiendo el código** y no da sesión sin él.
- Dada una cuenta **bloqueada** por intentos fallidos, cuando restablece su contraseña, entonces puede entrar de inmediato con la nueva.
- Dado un restablecimiento correcto, entonces llega un correo de aviso de cambio de contraseña.
- Dada la API sin correo configurado, entonces `forgot-password` responde `202`, no crea ninguna fila y deja un `Warning`.
- Dada la cola llena, entonces la petición sigue respondiendo `202` y el descarte queda registrado.
- Dado un fallo del servidor de correo al enviar, entonces la API sigue funcionando y queda un evento con la categoría.
- Dados los logs, los eventos y las respuestas de todo el flujo, cuando se revisan, entonces **no aparece** ningún email, token, enlace ni contraseña.
- Dada la **purga**, cuando hay tokens de restablecimiento caducados hace más de 7 días, entonces se borran y los vigentes o recientes no.
- Dada la diferencia de **tiempo de respuesta** entre un email con cuenta y otro sin ella, cuando se mide con muchas muestras, entonces no es distinguible (o, si lo era, hay un suelo de tiempo que la oculta).

Caso multi-tenant obligatorio: restablecer la contraseña de una cuenta **no revela ni toca nada de ninguna empresa** (no hay `TenantId` en todo el flujo), y el token de una cuenta **no sirve** para otra (el hash identifica la fila y la fila, la cuenta).

## Seguridad (ADR-009)

- **Datos personales:** la **dirección** llega por HTTP y sale hacia el proveedor de correo (ya es encargado del tratamiento desde la spec 017). La **contraseña nueva** llega por HTTP (por HTTPS) y solo existe en memoria hasta hashearla. El **token** es un secreto. **Ninguno aparece en logs, eventos, errores ni respuestas.**
- **Autorización:** las dos operaciones son **anónimas** (la persona no tiene sesión: no puede). No hay permisos nuevos. Quien tiene el token tiene el derecho; por eso el token es de 256 bits, caduca a la hora y se consume.
- **Entrada:** `email` con `MaximumLength` y la regla común de formato; `token` con la forma exacta de `OpaqueToken` antes de buscar nada; `newPassword` de 8 a 128 caracteres, como el registro (un máximo evita enviar contraseñas enormes para agotar CPU con el hash lento).
- **Aislamiento:** una entidad nueva, **global** (cuenta, no empresa). Va a la lista de entidades globales del test de arquitectura (spec 007) con su motivo, y su lectura queda restringida a un solo fichero.
- **SQL crudo:** ninguno.
- **Abuso:** **sí, doble**: el restablecimiento es anónimo (hay que pagar un hash PBKDF2 por cada intento de `reset-password`) y envía correo (`forgot-password`). Dos políticas por IP (5 por hora para pedir, 10 por hora para restablecer) y el límite silencioso por cuenta (R4).
- **Eventos de seguridad:** pedido (con si la cuenta existía, solo en el registro), pedido limitado, restablecimiento correcto (con las sesiones cerradas), restablecimiento rechazado (con el motivo real), y fallo de envío (con la categoría). Los números, en la familia `10xx`, se fijan al implementar.
- **Enumeración de cuentas:** esta spec **no empeora** nada. `forgot-password` no revela si existe la cuenta (R1, R2, R4). El registro **sigue** revelando «ese email ya está registrado» (límite conocido del capítulo 6): se cerrará con la confirmación de email, no aquí.
- **Modelo de amenazas del buzón:** quien lea el buzón puede cambiar la contraseña (R9) pero, con segundo factor, **no entrar** (R11); y la persona se entera por el correo de aviso (R12) y porque sus sesiones se cierran (R9c). Sin segundo factor, quien controla el buzón controla la cuenta: es la razón de fondo para obligar el MFA a `Owner` y `Admin` (límite 3b de ADR-014, ya anotado).

## Decisiones técnicas relevantes (pendientes de aprobar)

- **D1. Alcance: solo «he olvidado mi contraseña»**, no «cambiar la contraseña estando dentro» (spec 019). *Recomendado.* Son dos amenazas distintas: aquí el atacante puede ser quien controla un buzón; allí, quien tiene una sesión robada, y por eso allí se exige la contraseña actual. Mezclarlas haría la spec demasiado grande para defenderla.
- **D2. Siempre la misma respuesta, y el tiempo se mide.** `202` sin cuerpo en todos los casos (R1). *Recomendado.* El tiempo es la fuga que se olvida: se **mide** con muchas muestras al implementar (con y sin cuenta) y, si se distingue, se añade un **suelo de tiempo** a la petición. Alternativa: poner el suelo ya, sin medir; se descarta porque añade latencia que quizá no hace falta y porque medir es el método de este proyecto.
- **D3. El correo se encola en memoria, no se envía dentro de la petición.** *Recomendado.* Tres opciones y por qué esta: **(a)** enviar dentro de la petición, como las invitaciones (spec 017 D2): la petición tardaría segundos solo cuando la cuenta existe, un **oráculo de tiempo** (lo que no importaba en invitar, donde quien pide ya está autenticado y puede saber si el email tiene cuenta); **(b)** una cola persistida en la base de datos: guardaría el correo con **el token en claro**, que es exactamente lo que ADR-013 evita y la razón de que la spec 017 rechazara el *outbox*; **(c)** una cola **en memoria** con un consumidor: el token solo vive en la memoria del proceso unos segundos y la petición no espera. Coste asumido: lo pendiente se pierde si la API se reinicia, no hay reintentos y con varias instancias cada una envía lo que recibió. Es aceptable aquí porque **pedirlo de nuevo cuesta un clic** y la alternativa guarda un secreto.
- **D4. El token: 256 bits, una hora, un solo uso, uno vigente por cuenta.** *Recomendado.* Una hora (y no 7 días como una invitación) porque el daño de un token robado es mayor —cambia la contraseña de una cuenta existente—, y la persona que lo pide lo usa enseguida. Sin sal, como el de las invitaciones: ya es aleatorio y hace falta un hash determinista para buscarlo.
- **D5. Dos límites: 5 por hora y por IP, y 3 por hora y por cuenta en silencio.** *Recomendado.* Valores iniciales que se revisan con datos reales. El de cuenta es el que protege a la **víctima** de un buzón inundado (el de IP no basta: un atacante tiene muchas); y es silencioso para no ser un oráculo.
- **D6. Restablecer cierra todas las sesiones y reinicia el bloqueo, todo en una transacción** (R9). *Recomendado.* Cerrar todas las sesiones es lo que hace útil el cambio cuando la contraseña vieja estaba comprometida: sin ello, el atacante que ya tenía sesión la conserva. Requiere una variante de «revocar todos los refresh tokens» que **no confirme por sí misma** (como `DeleteUserAsync`, spec 009), para que sea todo o nada con el cambio de contraseña. Alternativa: revocar y cambiar por separado; se descarta porque un fallo entre las dos deja la contraseña cambiada y las sesiones vivas.
- **D7. El segundo factor no se toca.** *Recomendado.* Es la defensa que queda si el buzón está comprometido. Restablecer el MFA por correo la anularía. Quien pierde el móvil sigue por la vía de la spec 010.
- **D8. Un solo error para un token malo: `400 password_reset.invalid`** (R8), y la validación de la contraseña, la del registro. *Recomendado.* Mismo patrón que las invitaciones: el motivo real, solo en el registro.
- **D9. Correo de aviso al cambiar la contraseña** (R12). *Recomendado.* Es barato (ya hay puerto y cola) y es lo que permite a una víctima darse cuenta. Sin datos personales en el texto.
- **D10. Sin correo configurado: `202`, sin token y un `Warning`** (R6). *Recomendado.* Es coherente con el resto de la spec 017 (sin correo, todo sigue como si no existiera la función). Alternativa: devolver un error o el token, como hace la invitación sin correo; se descarta porque devolver el token de restablecimiento en la respuesta **entregaría la cuenta a quien lo pida**.
- **D11. La purga existente borra también los tokens de restablecimiento caducados** (R18). *Recomendado.* Una sola tarea de mantenimiento, el mismo margen (7 días), el mismo borrado por lotes. Un servicio nuevo para una tabla pequeña no se justifica.
- **D12. Eventos de seguridad** en la familia `10xx`: pedido, pedido limitado, restablecimiento correcto, restablecimiento rechazado (motivo real) y fallo de envío (categoría). *Recomendado.* Una ráfaga de «rechazado» desde una IP es alguien probando tokens; una de «pedido limitado» sobre una cuenta, alguien intentando inundar un buzón.
- **D13. Frontend:** el enlace en el login, una pantalla para pedirlo (siempre el mismo mensaje) y otra para restablecer (el token se lee del fragmento y se borra de la barra de direcciones; dos campos de contraseña). *Recomendado.* Tras el éxito, al login con un aviso.
- **D14. Documentación:** una **ADR nueva (ADR-021)** con el modelo de amenazas de la recuperación y la cola en memoria (y por qué no la persistida); anotaciones «Ampliada por» en ADR-007, ADR-009, ADR-014 y ADR-020; capítulos 6 y 8 del manual, glosario y preguntas del tribunal. *Recomendado.*
- **D15. Orden de trabajo:** la base (tabla, cola y servicio, **bloque 1**), pedirlo (**bloque 2**), restablecer (**bloque 3**), frontend (**bloque 4**) y documentación (**bloque 5**). *Recomendado.* El bloque 1 no cambia ningún comportamiento visible y se puede entregar solo.

## Fuera de alcance / Backlog relacionado

- Spec 019: cambiar la contraseña estando dentro.
- Confirmar el email al registrarse (cierra la enumeración por el registro).
- Obligar el segundo factor a `Owner` y `Admin` (límite 3b).
- Cola de correo persistida con reintentos.
- Lista de contraseñas filtradas y política más exigente.
- Recuperación de operadores de la plataforma, con la consola de administración.

---

## Validación antes de aprobar

> ¿Esta funcionalidad ayuda a conseguir o mantener al primer cliente?

Sí, y bloquea el piloto: sin ella, cada contraseña olvidada es una llamada al desarrollador, y un cliente que no puede entrar no cobra, no factura y no vuelve. Es también el flujo más atacado de cualquier sistema con cuentas, así que merece hacerse con el modelo de amenazas completo y no «rápido»: respuesta uniforme, tiempo medido, un secreto que nunca se guarda en claro y todas las sesiones cerradas.
