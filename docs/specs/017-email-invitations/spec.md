# Spec — 017-email-invitations

**Estado:** Aprobada. Decisiones D1–D13 aprobadas el 2026-11-02 tal como estaban escritas
**Fecha:** 2026-11-02
**Bounded Context:** Identity / Tenancy (invitaciones) y un puerto común de correo
**Depende de:** spec 009 y ADR-013 (la invitación con token de un solo uso y la nota «cuando exista el email, el token deja de devolverse y se envía»), ADR-009 (datos personales fuera de los logs, eventos de seguridad, límite de peticiones), ADR-017 (la lista cerrada de orígenes del frontend), ADR-008 (tests de integración con contenedores)

---

## Objetivo

Que **OfiFlow pueda enviar correo**, y que la primera cosa que lo use sea la **invitación de un miembro al equipo**.

Hoy, al invitar, la API devuelve el token al invitante y este tiene que pasárselo a la persona por otro medio (ADR-013, opción 1.B, aceptada «mientras no haya servicio de email»). Eso funciona para probar, pero **no se puede ofrecer a un cliente real**, y además deja un riesgo aceptado: el invitante conoce el token y podría aceptarlo él mismo.

El correo es también el **prerrequisito común** de lo que viene después hacia el primer piloto: recuperar la contraseña, avisar de presupuestos y facturas, recordar citas. Por eso esta spec no solo envía una invitación: deja un **puerto de correo** (`IEmailSender`) pequeño, seguro y probado, para que cada uso nuevo sea añadir un mensaje y no repensar el envío.

## Alcance — Qué SÍ incluye

Cuatro bloques, de menor a mayor dependencia:

1. **El puerto y el transporte.** `IEmailSender` en Application (enviar un mensaje de texto a una dirección) y su implementación por **SMTP** en Infrastructure, con la configuración validada al arrancar. Sin ningún caso de uso todavía.
2. **La invitación por correo.** Al invitar, si el correo está configurado, la API **envía el enlace** a la persona invitada y **ya no devuelve el token** al invitante. Plantilla fija, enlace construido solo con la configuración, límite de envíos, eventos de seguridad sin datos personales.
3. **Documentación.** ADR-020 (el correo: puerto, SMTP, por qué sin cola todavía), anotación en ADR-013, capítulos del manual, y lo que hay que hacer **fuera del código** para que el correo llegue de verdad (SPF, DKIM, DMARC).
4. **Frontend.** La pantalla del equipo dice «Invitación enviada a …» cuando no hay token que copiar, y conserva el enlace manual cuando el correo no está configurado.

## Alcance — Qué NO incluye

- **NEXT:** **recuperar contraseña** (spec 018). Es el segundo uso del puerto y tiene su propio modelo de amenazas (enumeración de cuentas, tokens de restablecimiento); mezclarlo aquí haría la spec demasiado grande para defenderla.
- **NEXT:** **confirmar el email** al registrarse, y avisos de seguridad («se inició sesión en un dispositivo nuevo», spec 016).
- **NEXT:** **cola de envío persistida (*outbox*) con reintentos**. Se justifica cuando haya correos *no interactivos* (recordatorios, avisos); la invitación es interactiva, y guardar el enlace en una tabla guardaría el token en claro (D2).
- **NEXT:** **rebotes y quejas** (qué hacer cuando un correo no llega o lo marcan como spam). Depende del proveedor que se elija.
- **NEXT:** **reenviar** una invitación. Hoy ya se puede revocar y volver a invitar; un botón «reenviar» sería solo comodidad.
- **NEXT:** plantillas con formato (HTML), logotipo y personalización por empresa.
- **NEXT:** escribir a los **clientes finales** (`Customer.Email`): son datos de terceros que no han firmado nada con OfiFlow; exige base legal y baja (idea ya anotada en `docs/ideas.md`).
- **LATER:** correo en varios idiomas; varios remitentes o dominios por empresa; adjuntos (llegarán con presupuestos y facturas).
- **NEVER:** píxeles de seguimiento o enlaces con redirección para medir aperturas. No aportan valor al piloto y son datos personales.
- **NEVER:** construir el enlace a partir de la cabecera `Host`, `Origin` o `Referer` de la petición (R3).

## Reglas de negocio / invariantes

**El puerto**
- **R1.** `IEmailSender.SendAsync(EmailMessage, ct)` vive en **Application** (`Common/Abstractions`); `EmailMessage` lleva **un destinatario, un asunto y un cuerpo de texto plano**. Si el envío falla, lanza una **`EmailDeliveryException`** cuyo mensaje **nunca contiene la dirección** ni el texto del servidor de correo (ADR-009 R5), solo una categoría (`Timeout`, `Authentication`, `Rejected`, `Unavailable`, `Unknown`).
- **R2.** Un solo destinatario por mensaje, sin copias ni copias ocultas: ningún caso de uso previsto las necesita y cada destinatario extra es una vía de fuga.
- **R3.** El **enlace** de un correo se construye **solo** con `Email:LinkBaseUrl` (configuración validada al arrancar) y nunca con datos de la petición. Si se construyera con la cabecera `Host` u `Origin`, quien envía la petición decidiría adónde apunta el enlace que recibe la víctima (*envenenamiento por cabecera Host*).

**El transporte SMTP**
- **R4.** `Email:Provider` vale `None` (por defecto) o `Smtp`. Con `None` **no se envía nada** y los casos de uso se comportan como hoy.
- **R5.** SMTP exige **conexión cifrada** (TLS implícito o STARTTLS) y validación normal del certificado. Solo se admite sin cifrar un servidor de **bucle local** (`localhost`, `127.0.0.1`, `::1`), para desarrollo y pruebas. Usuario y contraseña, si los hay, vienen de **secretos** (variables de entorno o *user-secrets*), nunca del repositorio.
- **R6.** Cada envío tiene un **tiempo máximo** (10 segundos por defecto) y **no se reintenta** dentro de la petición: si el servidor de correo no contesta, la persona lo sabe ya, y reintentar en silencio multiplicaría la espera.
- **R7.** Las cabeceras del mensaje no pueden inyectarse: ningún dato de fuera (nombre de empresa, nombre de persona) va en el asunto ni en las direcciones. El asunto es **fijo**.
- **R8.** La configuración se **valida al arrancar** (servidor y puerto válidos, remitente con forma de dirección, modo de cifrado coherente con el servidor, tiempo mayor que cero): una configuración que no vale impide arrancar en vez de fallar al primer envío.

**La invitación**
- **R9.** Con `Email:Provider = Smtp`, `POST /invitations` **envía** a la dirección invitada un correo de texto plano con: el nombre de la empresa, el rol en palabras, la fecha de caducidad y el enlace `{LinkBaseUrl}/auth/accept-invitation#token=…` (**el token en el fragmento**, como ya hace el frontend: el navegador no lo envía al servidor ni a la cabecera `Referer`).
- **R10.** Con `Smtp`, la respuesta **no incluye el token** (`token` es `null`) y añade `emailSent: true`. Con `None`, todo sigue como hoy: el token se devuelve y `emailSent: false`. Así desaparece el riesgo «el invitante conoce el token» exactamente cuando hay otra vía de entrega.
- **R11.** El correo se envía **antes de guardar** la invitación. Si el envío falla, **no se guarda nada** y la respuesta es un error claro (`invitation.email_not_sent`): nunca queda una invitación pendiente que nadie pudo recibir. Si el envío sale bien y guardar falla, la persona recibe un enlace que no existe (falla cerrado, es inofensivo) y quien invitó ve el error y puede repetir.
- **R12.** Nada de lo que escribe el invitante entra en el cuerpo, salvo la dirección (que es el destinatario) y el rol (lista cerrada). El nombre de la empresa sale de la base de datos y ya está limitado en longitud. Así un correo de OfiFlow no se puede usar para escribir un texto cualquiera a un tercero (*phishing*).
- **R13.** Las reglas de la invitación (rol gestionable, una pendiente por email, máximo 25 pendientes, email no registrado) **se comprueban antes de enviar**: no se envía correo por una invitación que se iba a rechazar.
- **R14.** Enviar correo es **costoso y abusable** (un `Admin` podría usar OfiFlow para llenar de correo a un tercero revocando y volviendo a invitar). `POST /invitations` lleva un **límite por persona y hora** (D10), además del tope de 25 pendientes.
- **R15.** Eventos de seguridad (D9): `1407 InvitationEmailSent` (`Information`: `InvitationId`, `TenantId`, quién invitó) y `1408 InvitationEmailFailed` (`Warning`: `TenantId`, quién, la categoría del fallo). **Nunca** la dirección, el token, el enlace ni el texto de error del servidor de correo.

## Modelo conceptual

**No hay entidad ni migración nuevas.** La invitación (`Invitation`) ya guarda el hash del token, la caducidad y el estado; el correo es un efecto de crearla.

```
Application
- IEmailSender            SendAsync(EmailMessage, ct)
- EmailMessage            To, Subject, TextBody
- EmailDeliveryException  Category (Timeout | Authentication | Rejected | Unavailable | Unknown)
- InvitationEmail         construye el EmailMessage de una invitación (función pura, probada con una tabla de casos)

Infrastructure
- EmailOptions            Provider, From, LinkBaseUrl, Smtp:{Host, Port, Security, UserName, Password, Timeout}
- SmtpEmailSender         el transporte (D1)

Api
- InvitationCreatedDto    Id, Token? (nulo si se envió por correo), ExpiresAt, EmailSent
```

## Criterios de aceptación

- Dado `Provider = None`, cuando un `Admin` invita, entonces la respuesta trae el token y `emailSent: false`, y no se contacta con ningún servidor de correo (comportamiento de hoy, sin regresiones).
- Dado `Provider = Smtp`, cuando un `Admin` invita, entonces el servidor de correo recibe **un** mensaje para esa dirección con el enlace, y la respuesta trae `token: null` y `emailSent: true`.
- Dado ese correo, cuando se lee el enlace, entonces empieza por `Email:LinkBaseUrl`, el token va tras `#`, y **aceptar la invitación con ese token funciona**.
- Dado `Smtp` y un servidor de correo que no contesta, cuando se invita, entonces la respuesta es `invitation.email_not_sent` en menos del tiempo máximo más un margen, **no existe ninguna invitación nueva** y se registra el evento `1408`.
- Dado un servidor que rechaza la autenticación, cuando se invita, entonces mismo resultado y la categoría del evento es `Authentication`.
- Dado un rol no gestionable, un email ya registrado, una invitación ya pendiente o 25 pendientes, cuando se invita, entonces se responde lo de siempre y **no se envía ningún correo**.
- Dada una petición con cabecera `Host` u `Origin` de un tercero, cuando se invita, entonces el enlace del correo sigue empezando por `Email:LinkBaseUrl`.
- Dado un nombre de empresa con saltos de línea o caracteres de control, cuando se invita, entonces el asunto es el fijo y el cuerpo no puede añadir cabeceras.
- Dados los logs y los eventos de un envío (correcto o fallido), cuando se revisan, entonces **no aparece** la dirección, el token, el enlace ni el texto de error del servidor.
- Dado `POST /invitations` repetido por la misma persona por encima del límite, cuando se supera, entonces `429` y no se envía más correo.
- Dada una configuración inválida (servidor sin cifrar que no es de bucle local, remitente sin forma de dirección, `LinkBaseUrl` con `http` fuera de `localhost`, tiempo cero), cuando arranca la API, entonces **no arranca** y el mensaje dice qué ajuste.
- Dada una configuración de `Smtp` sin usuario, cuando el servidor lo admite sin autenticación (como en desarrollo), entonces envía sin autenticar.

Caso multi-tenant obligatorio: la empresa que aparece en el correo es la del invitante; un `Admin` de la empresa A nunca puede provocar un correo con el nombre, el rol o el enlace de la empresa B, ni ver las invitaciones de B (el filtro de empresa ya cubre la consulta; se añade un test que lo fija).

## Seguridad (ADR-009)

- **Datos personales:** la **dirección del invitado** sale hacia un tercero (el proveedor de correo) y aparece en el cuerpo del mensaje. Ya se guardaba en `Invitation`. **Ninguna** aparece en logs, eventos ni mensajes de error (R1, R15). El proveedor de correo pasa a ser un **encargado del tratamiento**: queda anotado en la ADR-020 y en el manual como pendiente de resolver al elegirlo (contrato y país de tratamiento).
- **Autorización:** sin permisos nuevos. Invitar sigue exigiendo `Users.Manage` y poder gestionar el rol (spec 009). El puerto no tiene endpoint.
- **Entrada:** no hay campos nuevos. El correo del invitado ya está validado y limitado; el asunto es fijo y el cuerpo solo incluye el nombre de la empresa (limitado) y el rol (lista cerrada) (R7, R12).
- **Aislamiento:** no hay entidad nueva; no se usa `IgnoreQueryFilters()`.
- **SQL crudo:** ninguno.
- **Abuso:** **sí**: enviar correo es la vía clásica de abuso. Límite por persona y hora, tope de pendientes, el correo solo se envía tras superar todas las reglas (R13, R14) y no hay texto libre en el cuerpo (R12).
- **Eventos de seguridad:** `1407` y `1408` (R15). Un `1408` repetido es la señal de que el proveedor está caído o mal configurado; la ADR-020 dice cómo reconocerlo.
- **Secretos nuevos:** usuario y contraseña SMTP. Mismas reglas que `Jwt:Secret` y `Mfa:EncryptionKey`: fuera del repositorio, no se escriben en logs, y quedan documentados para el despliegue.

## Decisiones técnicas relevantes (pendientes de aprobar)

- **D1. Cómo se envía:** un **puerto `IEmailSender`** con una implementación **SMTP** (biblioteca MailKit). *Recomendado.* SMTP lo hablan todos los proveedores (y los de pago y los gratuitos), así que **no hay que elegir proveedor para escribir ni para probar el código**: se elige al desplegar y se cambia con configuración. En local y en los tests se usa un servidor SMTP de pruebas. `System.Net.Mail.SmtpClient`, que viene con .NET, está marcada por Microsoft como no recomendada para código nuevo. Alternativa: la API HTTP de un proveedor concreto (más funciones: rebotes, estadísticas), a costa de elegirlo ya y atarse a él; se puede añadir después como **otra implementación del mismo puerto**.
- **D2. Envío directo, sin cola.** *Recomendado.* La invitación es interactiva: quien invita espera y necesita saber si salió. Una cola persistida (*outbox*) obligaría a guardar el correo hasta enviarlo, y ese correo **contiene el token en claro**, justo lo que ADR-013 evita (solo se guarda el hash). La cola se justifica con el primer correo no interactivo (recordatorios), y entonces habrá que decidir cómo no guardar secretos en ella. Coste asumido: si el proveedor tarda, la petición espera hasta el tiempo máximo (R6).
- **D3. Orden: enviar y después guardar** (R11). *Recomendado.* Sin compensación que escribir: si falla el envío, no hay nada que deshacer; si falla guardar tras enviar, el enlace recibido no existe y no sirve. Alternativa: guardar y luego enviar, con revocación si falla (dos escrituras, y una ventana en la que existe una invitación que nadie recibirá).
- **D4. Qué responde si falla:** `invitation.email_not_sent`, **502** (la API funciona; falla un servicio del que depende), con mensaje en español que no revela el motivo del servidor de correo, y entrada nueva en el diccionario de errores (ADR-011). *Recomendado.* Alternativa: 503; se descarta porque 503 sugiere que es la propia API la que no puede atender.
- **D5. Sin correo configurado, como hoy** (`Provider = None` por defecto) y **con correo, sin token** (R10). *Recomendado.* Los tests, el desarrollo y las demos siguen funcionando sin servidor de correo; en producción se activa `Smtp` y el riesgo «el invitante conoce el token» desaparece. Se añade un aviso en el registro al arrancar en `Production` con `None` (no es un error: puede ser una demo, pero conviene que se vea).
- **D6. El enlace sale de `Email:LinkBaseUrl`** (R3), un valor propio y validado (`https`, salvo `localhost`; sin ruta ni barra final), **no** de `Cors:AllowedOrigins`. *Recomendado.* Son dos ajustes que hoy valen lo mismo, pero significan cosas distintas (a quién se deja leer respuestas / adónde apunta un enlace en un correo); acoplarlos haría que tocar uno rompiera el otro en silencio.
- **D7. Contenido:** **texto plano, en español, plantilla fija**, sin HTML (R7, R12). *Recomendado.* Es lo que no se puede inyectar ni se ve distinto en cada cliente de correo. Un HTML con marca llegará con presupuestos y facturas, donde sí importa la imagen.
- **D8. Configuración y secretos:** sección `Email` con `Provider`, `From`, `LinkBaseUrl` y `Smtp:*` (R4–R8); contraseña por secretos. Cifrado obligatorio salvo bucle local. *Recomendado.* El mismo patrón que `RefreshTokenPurge` y `Cors`: se valida al arrancar y falla en voz alta.
- **D9. Eventos:** `1407` y `1408` (R15), sin datos personales (en la familia `14xx` de usuarios e invitaciones; el borrador decía `1012`/`1013`, números de la familia de autenticación, y se cambió al implementar). *Recomendado.* El fallo es de nivel `Warning` y no `Error`: el que falla es un servicio externo, no un bug nuestro, y un `Error` despertaría a quien no puede arreglarlo.
- **D10. Límite de envío:** `POST /invitations` limitado a **20 por hora y por persona** (por `UserId`, no por IP), valor inicial. *Recomendado.* Hasta ahora todos los límites son por IP; aquí el actor ya está autenticado y es más justo y más difícil de esquivar limitar por persona (varias personas detrás de una misma oficina no se estorban). Hace falta una pequeña variante del helper del limitador.
- **D11. Cómo se prueba:** el handler y la API con un **`IEmailSender` falso** que guarda lo enviado (rápido, determinista); el transporte SMTP con un **servidor SMTP de pruebas en contenedor** (Testcontainers, como SQL Server en ADR-008), para que el cifrado, la autenticación, los tiempos máximos y el formato real del mensaje se prueben de verdad. *Recomendado.* Un falso no puede cazar un fallo de MailKit. Coste: el contenedor se añade a lo que ya necesita Docker.
- **D12. Documentación:** **ADR-020** nueva (el correo: puerto, SMTP, por qué sin cola todavía, el proveedor como encargado del tratamiento, qué hacer cuando falla); anotación «Ampliada por ADR-020» en ADR-013 (cierra su nota sobre el email) y en la spec 009; capítulos 6 y 8 del manual; y una sección de **entrega de correo** (SPF, DKIM, DMARC, dirección de remite) para el despliegue. *Recomendado.* Sin esa sección el código estaría «hecho» y los correos acabarían en la carpeta de spam.
- **D13. Orden de trabajo:** **puerto y SMTP** (bloque 1), **invitación** (bloque 2), **documentación** (bloque 3), **frontend** (bloque 4). *Recomendado.* El bloque 1 no cambia ningún comportamiento visible y se puede entregar y fusionar solo.

## Fuera de alcance / Backlog relacionado

- **Lo que debe hacer el dueño del proyecto fuera del código** para enviar correo real: elegir proveedor SMTP, tener un dominio propio para el remitente y configurar SPF, DKIM y DMARC. No bloquea escribir ni probar esta spec (se prueba contra un SMTP local), pero **sí bloquea el piloto**. Se anota en el manual (D12).
- Spec 018: recuperar contraseña (usa este puerto).
- Cola persistida con reintentos, cuando haya el primer correo no interactivo (D2).
- Rebotes y quejas, según el proveedor elegido.
- Purga de invitaciones antiguas (ADR-013, ya anotada: guardan el email).

---

## Validación antes de aprobar

> ¿Esta funcionalidad ayuda a conseguir o mantener al primer cliente?

Sí, y es de las que **bloquean** el piloto: hoy un autónomo no puede dar de alta a su primer empleado sin copiar un enlace a mano y mandarlo por otro canal, y no existe ninguna vía para recuperar una contraseña. El correo desbloquea esas dos cosas y, más adelante, los avisos de presupuestos y facturas. Se mantiene pequeña a propósito: un puerto, un transporte, un caso de uso, sin tablas nuevas.
