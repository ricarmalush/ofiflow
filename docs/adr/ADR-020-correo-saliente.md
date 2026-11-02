# ADR-020 — Correo saliente: un puerto, un transporte SMTP y la invitación por correo

**Estado:** Aceptado (2026-11-02). Las decisiones D1–D13 de la [spec 017](../specs/017-email-invitations/spec.md) se aprobaron tal como estaban escritas; los dos cambios que se hicieron al implementar (números de los eventos y dónde vive el límite de invitaciones) están en [«Lo que cambió al implementar»](#lo-que-cambió-al-implementar).
**Fecha:** 2026-11-02
**Proyecto:** OfiFlow

---

## Contexto

Hasta ahora **OfiFlow no podía escribir a nadie**. Todo lo que hacía la API era responder a quien le hablaba. La primera consecuencia visible estaba en las invitaciones (ADR-013, opción 1.B): al invitar a un miembro al equipo, la API **devolvía el token a quien invitaba** y este tenía que pasárselo a la persona por otro canal. Funcionaba para probar, pero:

- **No se puede ofrecer a un cliente real.** Un autónomo que da de alta a su primer empleado no debería copiar un enlace a mano.
- **Dejaba un riesgo aceptado:** quien invita conoce el token y podría aceptar la invitación en nombre de otro (ADR-013, «riesgos aceptados»).

Y no es solo la invitación. El correo es el **prerrequisito común** de lo que viene después hacia el primer piloto: recuperar la contraseña, confirmar el email, avisar de presupuestos y facturas, recordar citas. Por eso esta decisión no es «enviar una invitación» sino «**dejar el envío de correo bien hecho, una vez**», para que cada uso nuevo sea añadir un mensaje y no repensar el transporte.

## Problema

- ¿Cómo se envía el correo, sin atar el código a un proveedor que todavía no está elegido?
- ¿Se envía dentro de la petición o por una cola? (Un correo de invitación **contiene un secreto**.)
- ¿Qué pasa si el envío falla? ¿Y si no hay correo configurado?
- ¿De dónde sale el enlace que lleva el correo? (Quien envía la petición controla varias cabeceras.)
- ¿Cómo se evita que OfiFlow sirva para llenar de correo a un tercero?
- ¿Qué se guarda y se registra, si la dirección de un invitado ahora **sale hacia otra empresa** (el proveedor de correo)?

## Opciones consideradas

### A. El transporte

**A1. Un puerto `IEmailSender` y un transporte SMTP con MailKit.** *(elegida)* Todos los proveedores de correo hablan SMTP, así que **no hay que elegir proveedor para escribir ni para probar el código**: se elige al desplegar y se cambia con configuración. En local y en los tests se usa un servidor SMTP de pruebas.

**A2. La API HTTP de un proveedor concreto.** Más funciones (rebotes, estadísticas), a costa de elegir ya y atarse. Sigue siendo posible **más adelante como otra implementación del mismo puerto**.

**A3. `System.Net.Mail.SmtpClient`**, que viene con .NET. Microsoft la marca como no recomendada para código nuevo (no soporta bien los protocolos modernos).

### B. Envío directo o cola

**B1. Envío directo, dentro de la petición.** *(elegida)* La invitación es interactiva: quien invita espera y necesita saber si salió.

**B2. Cola persistida (*outbox*) con reintentos.** Obligaría a **guardar el correo hasta enviarlo, y ese correo contiene el token en claro**: justo lo que ADR-013 evita (solo se guarda el hash). Se justifica con el primer correo *no interactivo* (un recordatorio), y entonces habrá que decidir cómo no guardar secretos en ella.

### C. El orden: enviar y guardar

**C1. Enviar antes de guardar.** *(elegida)* Sin compensación que escribir: si falla el envío no hay nada que deshacer; si falla guardar tras enviar, el enlace recibido apunta a una invitación que no existe, falla cerrado y es inofensivo.

**C2. Guardar y luego enviar, revocando si falla.** Dos escrituras, y una ventana en la que existe una invitación que nadie recibirá.

### D. Sin correo configurado

**D1. Se comporta como hoy** (`Email:Provider = None`, el valor por defecto): la invitación devuelve el token. *(elegida)* Los tests, el desarrollo y las demos siguen funcionando sin servidor de correo.

**D2. Es un error.** Obligaría a tener un servidor de correo para arrancar en desarrollo.

### E. De dónde sale el enlace

**E1. De la configuración del despliegue** (`Email:LinkBaseUrl`). *(elegida)*

**E2. De la petición (`Host`, `Origin`).** Cómodo, y un agujero clásico: *envenenamiento por cabecera Host*. Quien envía la petición decidiría adónde apunta el enlace que recibe la víctima.

### F. El límite de envíos

**F1. Por persona (`UserId`), consultado en el endpoint.** *(elegida)*

**F2. Una política más del limitador global.** Esas se evalúan **antes** de autenticar (ADR-017) y solo conocen la IP. Todos los clientes de una oficina comparten IP.

**F3. Mover el limitador global detrás de la autenticación.** Resolvería el caso de uno cambiando el comportamiento de todos los demás endpoints.

## Decisión

### 1. El puerto y el transporte

1. **`IEmailSender`** (Application): `SendAsync(EmailMessage)` y `IsEnabled`. Un mensaje tiene **un destinatario, un asunto y un cuerpo de texto plano**. Sin copias, sin copias ocultas, sin HTML y sin adjuntos: ningún uso previsto los necesita y cada uno es una vía más de fuga o de inyección.
2. **`SmtpEmailSender`** (Infrastructure, MailKit 4.18.2). Una conexión por mensaje. Lo que garantiza:
   - **Cifrado obligatorio**, STARTTLS o TLS desde el primer byte, **nunca oportunista**: si el servidor no ofrece cifrado, falla en vez de enviar en claro. Solo se admite sin cifrar un servidor de **bucle local** (desarrollo y tests). La validación del certificado es la normal del sistema y **ningún fichero del transporte la desactiva** (un test de arquitectura lo vigila).
   - **Un solo presupuesto de tiempo** para conectar, autenticar y enviar (10 segundos por defecto) y **sin reintentos**: si el servidor no contesta, la persona lo sabe ya.
   - **Cabeceras a salvo:** un destinatario o un asunto con caracteres de control se rechazan **antes de tocar la red**; el asunto lo escribe siempre el código, nunca un dato de fuera.
   - **No se publica el nombre de la máquina:** el dominio con el que el cliente se presenta (EHLO) y el del identificador del mensaje (`Message-Id`) son los del remitente. Por defecto la biblioteca usa el nombre del equipo, que acaba en la cabecera `Received` que ve el destinatario.
   - **Errores sin datos:** `EmailDeliveryException` lleva solo una **categoría de una lista cerrada** (`Timeout`, `Authentication`, `Rejected`, `Unavailable`, `Unknown`) y un mensaje fijo por categoría. El texto del servidor, que suele repetir la dirección, se descarta y la excepción original no se conserva.
3. **Configuración** (`Email:*`), validada **al arrancar** por un validador propio que dice *qué ajuste* falla y nunca repite un valor (podría ser la contraseña). `Provider` (`None` por defecto o `Smtp`), `From`, `FromName`, `LinkBaseUrl` y `Smtp:{Host, Port, Security, UserName, Password, Timeout}`. La contraseña vive en secretos o variables de entorno, nunca en el repositorio.
4. **`AddEmail` solo en la API.** La consola de administración no recibe el puerto (un test lo fija). Con `None` se registra un transporte que **lanza si alguien lo llama**: un caso de uso nuevo que se olvide de comprobar `IsEnabled` falla en las pruebas en vez de «enviar» a ninguna parte.

### 2. La invitación por correo

1. Con correo configurado, `POST /invitations` **envía** el enlace y la respuesta lleva `token: null` y `emailSent: true`. **Quien invita no llega a conocer el token**: se cierra el riesgo aceptado de ADR-013. Sin correo, todo como antes (`emailSent: false` y el token).
2. **Se envía después de todas las reglas** (jerarquía de roles, email sin cuenta, una pendiente por email, máximo de pendientes) **y antes de guardar**. No se manda correo por una invitación que se iba a rechazar.
3. **Si el envío falla, no se crea nada** y la respuesta es **`502 invitation.email_not_sent`**, sin el motivo. 502 y no 503 porque la API funciona: falla un servicio del que depende. Es un tipo de excepción nuevo (`ExternalServiceException`), distinto de un error de negocio (400) y de un bug (500).
4. **El enlace** es `{Email:LinkBaseUrl}/auth/accept-invitation#token=…`, construido en **un solo sitio** (`EmailLinks`) y **solo** con la configuración. El token va en el **fragmento** (`#`), no en la consulta (`?`): el navegador no envía el fragmento al servidor, así que no acaba en logs de servidores ni de proxies ni en la cabecera `Referer`. `LinkBaseUrl` se valida al arrancar: `https` salvo en localhost, sin ruta, sin barra final.
5. **Plantilla fija** (`InvitationEmail`, una función pura): asunto sin ningún dato; el cuerpo lleva el nombre de la empresa (de la base de datos, en una sola línea), el rol (lista cerrada) y la caducidad. **Nada que escriba quien invita**, de modo que un correo de OfiFlow no sirve para escribir un texto cualquiera a un tercero (*phishing*).
6. **Eventos de seguridad** `1407` (enviado) y `1408` (fallido, con la categoría): solo Ids. **Nunca** la dirección, el token, el enlace ni el texto del servidor.
7. **Límite de 20 invitaciones por hora y por persona** (`InvitationThrottle`), además del tope de 25 pendientes por empresa, que no frena el abuso porque revocar libera cupo.
8. **Aviso al arrancar** en `Production` con `Provider = None`: no impide arrancar (puede ser una demo), pero tiene que verse.

### Versiones fijadas

Comprobadas el 2026-11-02: **MailKit 4.18.2** (la última de su línea 4.x; la auditoría de NuGet, con las vulnerabilidades como error, no avisa) y **Mailpit v1.33.0** (servidor SMTP de pruebas, publicada en Docker Hub el 2026-10-31). Ambas fijadas, como la imagen de SQL Server.

## Consecuencias

### Positivas

- **La invitación es entregable a un cliente real** y desaparece el riesgo de que quien invita acepte en nombre de otro.
- **El correo se escribe una vez.** Recuperar contraseña, confirmar email y los avisos serán un `EmailMessage` y una plantilla, no un transporte nuevo.
- **Sin proveedor elegido todavía** y sin cambiar código al elegirlo: SMTP lo hablan todos.
- **Un fallo es ruidoso y no deja basura:** `502` y ninguna invitación a medias.
- **Sin tablas ni migraciones nuevas.**

### Negativas / riesgos aceptados

- **El proveedor de correo pasa a ser un encargado del tratamiento.** La dirección del invitado, y el nombre de la empresa en el cuerpo, **salen hacia un tercero**. Hay que resolver al elegirlo (contrato de tratamiento y país donde trata los datos) y anotarlo. Ya era un dato que se guardaba (`Invitation`); lo nuevo es que sale.
- **«El servidor lo aceptó» no es «llegó a la bandeja».** El evento `1407` dice lo primero. Sin SPF, DKIM y DMARC bien puestos (ver la recomendación de despliegue), los correos acabarán en la carpeta de spam.
- **Sin cola, la petición espera al proveedor** hasta el tiempo máximo (10 s por defecto). Si el proveedor está caído, `POST /invitations` tarda ese tiempo en devolver un `502`.
- **Sin reintentos y sin bandeja de salida:** si el envío falla, quien invita lo repite. Es lo que se quería (el fallo se ve), pero un fallo transitorio cuesta un clic.
- **Rebotes y quejas no se gestionan:** un correo que el servidor de destino rechaza después de aceptarlo el proveedor no vuelve a OfiFlow. Depende del proveedor elegido.
- **El límite por persona es en memoria y por instancia**, como los demás (ADR-009), y se reinicia al reiniciar la API. Con varias instancias, el límite efectivo sería su suma.
- **Quien tiene un buzón comprometido tiene la invitación.** El correo es ahora la vía de entrega: quien lea el buzón del invitado puede aceptar. Es el modelo de amenazas de cualquier enlace por correo (recuperar contraseña tendrá el mismo) y se mitiga con la caducidad a 7 días y el uso único.
- **Un enlace sirve para una persona, pero un programa que abre enlaces puede verlo.** Los escáneres de correo abren los enlaces; como aceptar es un `POST` con el token, **abrir el enlace no consume la invitación** (la página lee el fragmento y es el usuario quien envía el formulario).
- **Un `Admin` puede llenar de correo a un tercero hasta el límite:** 20 por hora, uno por cada invitación aceptada por las reglas (una pendiente por email) y siempre con el texto fijo.
- **El envío real contra un proveedor concreto no está probado.** Los tests usan un servidor SMTP de pruebas (Mailpit) y uno falso en proceso. Lo primero que hay que hacer al elegir proveedor es una invitación real de punta a punta.

### Lo que cambió al implementar

1. **Los eventos son `1407` y `1408`, no `1012` y `1013`.** Los números del borrador eran de la familia de autenticación; las invitaciones viven en la `14xx` (ADR-009). Corregido en la spec.
2. **El límite de invitaciones no es una política del limitador global**, como el resto, sino que lo consulta el endpoint (opción F1). Ese limitador va antes de autenticar y solo conoce la IP; mover el limitador habría cambiado a todos los demás endpoints. La respuesta es la misma `429` de siempre (el cuerpo se comparte).

### Hallazgos de la implementación (útiles para el tribunal)

- **`Email.Create("a@b.com\n")` aceptaba un salto de línea final**: la expresión del dominio terminaba en `$`, que en .NET también casa justo antes de un `\n` final. Lo vi al probar que el transporte rechaza destinatarios con caracteres de control, y el transporte lo rechaza por su cuenta (defensa en profundidad), pero el origen estaba en el dominio. Se corrige aparte (`\z`).
- **Un servidor de pruebas puede tardar diez segundos por una búsqueda DNS.** Mailpit, dentro de Docker Desktop, hacía una búsqueda DNS inversa de cada cliente antes de saludar, y la primera conexión tardaba unos 10 s: justo el tiempo máximo del transporte. Daba fallos que no eran del código. Los contenedores de test la llevan desactivada.
- **Un antivirus puede interponerse en TLS.** En la máquina de desarrollo, AVG vuelve a firmar las conexiones TLS desde el primer byte con destino a puertos de correo, incluso en bucle local. El test de TLS implícito no fija el certificado por eso; la validación del certificado la prueban los de STARTTLS, que el antivirus no toca.
- **Un test de aislamiento parecía fuerte y no lo era.** Con la empresa propia guardada antes que la ajena, una consulta que cogiera «la primera empresa» daba el mismo resultado. Lo cazó una mutación.

### Recomendación de despliegue (no es código)

Para que el correo llegue de verdad hace falta, **fuera del código**:

1. **Un proveedor SMTP** y **un dominio propio** para el remitente (no una cuenta gratuita genérica). Puerto 587 con `Security = StartTls`, o 465 con `SslOnConnect`.
2. **SPF, DKIM y DMARC** en el DNS del dominio:
   - **SPF** dice qué servidores pueden enviar correo en nombre del dominio.
   - **DKIM** firma cada mensaje con una clave cuya parte pública se publica en el DNS.
   - **DMARC** dice qué hacer con lo que falle SPF o DKIM y a dónde enviar los informes.
   Los registros exactos los da el proveedor elegido. Sin ellos, los correos se marcarán como spam o se rechazarán.
3. **Los secretos** (`Email__Smtp__UserName` y `Email__Smtp__Password`) por variables de entorno o el gestor de secretos del despliegue, como `Jwt:Secret` y `Mfa:EncryptionKey`.
4. **`Email__LinkBaseUrl`** con el origen público del frontend (`https://…`, sin barra final). Es lo que decide adónde apunta el enlace de cada correo.
5. **Vigilar el evento `1408`.** Varios seguidos con la categoría `Authentication` (credenciales mal puestas) o `Unavailable` (proveedor caído o sin red) son la señal; `Timeout` repetido, un proveedor lento. Un `1408` suelto con `Rejected` no es una avería.
6. **Una primera invitación real de punta a punta** con el proveedor elegido, antes de dar nada por hecho.

### Qué queda cerrado (no se debe reabrir sin una razón nueva y explícita)

- **El enlace sale solo de la configuración**, nunca de la petición.
- **Sin cifrado solo en bucle local**, y nunca se desactiva la validación del certificado.
- **Nada que escriba quien invita entra en el correo**, y el asunto es fijo.
- **Ni la dirección, ni el token, ni el enlace, ni el texto del servidor** en logs, eventos ni errores.
- **El token no se guarda ni se devuelve** cuando se envía por correo, y **no se guarda en claro nunca** (por eso no hay cola de momento).
- **MailKit y MimeKit solo dentro de `Infrastructure/Mail`**; el resto solo conoce el puerto.

### Qué queda abierto para revisar más adelante

- **Cola de envío persistida con reintentos**, cuando haya el primer correo no interactivo, y cómo no guardar secretos en ella.
- **Recuperar contraseña** (spec 018) y **confirmar el email**: segundo y tercer uso del puerto.
- **Rebotes y quejas**, según el proveedor.
- **Reenviar una invitación** como acción propia (hoy: revocar y volver a invitar).
- **HTML con marca** para presupuestos y facturas, y **adjuntos**.
- **Escribir a clientes finales** (`Customer.Email`): datos de terceros que no han firmado nada con OfiFlow; exige base legal y una baja.
- **Una implementación por la API HTTP** de un proveedor, si hacen falta sus funciones.
- **Varios idiomas** y **remitentes por empresa**.
- **El contrato de encargado del tratamiento** con el proveedor que se elija.
- **El límite por persona compartido entre instancias**, si se despliega más de una.

---

## Relación con otros ADR

- Resuelve: la nota de ADR-013 («cuando exista el email, el token deja de devolverse y se envía») y su riesgo aceptado («el invitante conoce el token»).
- Amplía: ADR-009 (eventos `1407` y `1408`; la dirección del invitado, un dato personal, sale hacia un encargado del tratamiento y sigue sin aparecer en ningún registro; límite de peticiones que no es por IP).
- Relacionada con: ADR-017 (el limitador va antes de autenticar, y por eso el límite por persona no es una política suya), ADR-011 (código de error nuevo y estado 502), ADR-008 (servidor SMTP de pruebas en contenedor, como SQL Server), ADR-010 (versiones fijadas y auditoría de dependencias), ADR-012 (sin permisos nuevos: invitar sigue exigiendo `Users.Manage`).
- Prepara: la spec 018 (recuperar contraseña) y los avisos de presupuestos y facturas.
