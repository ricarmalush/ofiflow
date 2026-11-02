# Tasks — 017-email-invitations

**Spec relacionada:** [./spec.md](./spec.md)
**ADR relacionadas:** [ADR-013](../../adr/ADR-013-gestion-de-usuarios.md) (se anota), [ADR-009](../../adr/ADR-009-seguridad-base.md), [ADR-011](../../adr/ADR-011-errores-codigos-diccionario.md) (error nuevo) y la **ADR-020** nueva (D12)
**Estado general:** Aprobada (decisiones D1–D13, 2026-11-02). Bloque 1 hecho (sin commitear); bloques 2 a 4 sin empezar.

---

## Convención

- Cada tarea completable y verificable en una sesión.
- Se marca `[x]` solo cuando cumple la Definición de «Done»: compila, tests en verde, mutaciones detectadas y manual al día.
- Las mutaciones se hacen a mano sobre el código real, con copia de seguridad, **dirigidas por número de línea**, con aviso si la mutación no cambió el fichero y con **`touch` al restaurar**.
- Verificación en vivo con la receta de siempre (base desechable, API real, script fuera del repositorio) y, esta vez, **un servidor SMTP de pruebas local** que permita leer el correo recibido.
- **El bloque 1 se puede entregar solo**: no cambia ningún comportamiento visible.

---

## Bloques

### Bloque 1 — El puerto y el transporte (Application + Infrastructure)
- [x] Antes de empezar: versiones comprobadas el 2026-11-02 — MailKit **4.18.2** (la última de su línea 4.x en NuGet; la auditoría de NuGet, con vulnerabilidades como error, no avisa) y Mailpit **v1.33.0** en Docker Hub (publicada el 2026-10-31); ambas fijadas. Anotar en la ADR-020 (bloque 3)
- [x] `IEmailSender`, `EmailMessage` (con el tipo `Email` del dominio como destinatario) y `EmailDeliveryException` con su `EmailFailureCategory`, en Application (`Common/Abstractions` y `Common/Mail`: el espacio de nombres no puede llamarse `Email`, chocaría con el tipo). El mensaje es fijo por categoría y no conserva la excepción original
- [x] `EmailOptions` (+ `SmtpOptions`) con `Validate()` y un `EmailOptionsValidator` propio, para que el arranque diga **qué ajuste** falla (R8). Comprobado en vivo con la API real: «Falta Email:LinkBaseUrl…», «Email:Smtp:Security = None solo se admite con un servidor de esta misma máquina…»
- [x] `SmtpEmailSender` (MailKit): cifrado obligatorio (STARTTLS o TLS implícito, nunca oportunista), un solo presupuesto de tiempo y sin reintentos, destinatario y asunto con caracteres de control rechazados antes de tocar la red, dominio de EHLO y de Message-Id tomados del remitente (sin ellos MailKit publica el nombre de la máquina en la cabecera `Received`), categoría del fallo sin texto del servidor ni dirección
- [x] `AddEmail` en `Program.cs` de la API; con `None`, `NullEmailSender` que lanza si alguien lo llama; la consola de administración no lo recibe (con test)
- [x] Tests (109 de Mail en Infrastructure + 5 en Api): contra **Mailpit en contenedor** (STARTTLS con usuario, y sin cifrar ni usuario): entrega entera con tildes y eñes, remitente y destinatario únicos, sin copias, contraseña incorrecta, servidor que exige usuario y no se puso, certificado en el que no se confía, huella distinta de la esperada, credenciales sin cifrar rechazadas, cuerpo que imita cabeceras sin poder añadir destinatarios; con un **servidor SMTP en proceso**: tiempo agotado en el plazo y sin reintento, destinatario rechazado (el texto del servidor no sale), STARTTLS exigido y no ofrecido (no se envía en claro), conexión cortada, saludo rechazado, nadie escuchando, TLS desde el primer byte
- [x] Tests de las opciones (cada valor inválido rechazado, valores por defecto aprobados, ningún mensaje repite un valor ni la contraseña), del registro y del arranque real del host (`ValidateOnStart`); test de arquitectura: MailKit y MimeKit solo dentro de `Infrastructure/Mail`, nadie construye el transporte a mano, la validación del certificado nunca se desactiva
- [x] Mutaciones (33 en total, todas detectadas salvo 2 que sobreviven **por diseño**: el tiempo máximo está dos veces —el presupuesto total y el del cliente— y quitar uno solo no cambia nada; quitar los dos sí se detecta): STARTTLS opcional; EHLO y Message-Id con el nombre de la máquina; destinatario y asunto sin comprobar; tiempo agotado no reconocido; fallo de TLS, servidor que exige usuario, destinatario rechazado y cancelación mal clasificados; registro con el texto de la excepción; autenticar siempre; sin cifrar en cualquier servidor; «localhost» por contener; barra final, ruta o http admitidos en `LinkBaseUrl`; sin tope de tiempo; usuario y contraseña desemparejados; remitente con caracteres de control; valores por defecto cambiados; validación y validador siempre correctos o sin registrar; sin `ValidateOnStart`; nunca el transporte SMTP; transporte nulo que envía en silencio; y `Program.cs` sin `AddEmail`
- [x] Verificación contra la API publicada de verdad: arranca con el correo apagado y con una configuración válida; se niega a arrancar con `Smtp` sin `LinkBaseUrl` y con `Security = None` fuera de localhost, diciendo qué ajuste. **No hay endpoint que envíe todavía** (llega en el bloque 2), así que el envío real se ejercita con los tests contra Mailpit
- [ ] [externo] Commit del bloque 1 (con orden explícita)

**Hallazgos del bloque 1**
- `Email.Create("a@b.com
")` **acepta** un salto de línea final (la expresión usa `$`, que casa antes de un `
` final). No se corrige aquí (es del dominio y toca a todos los usos); el transporte lo rechaza por su cuenta. Anotado como tarea aparte.
- Mailpit, sin `MP_SMTP_DISABLE_RDNS`, tarda unos 10 s en saludar a la primera conexión dentro de Docker Desktop (búsqueda DNS inversa): agota el tiempo máximo del transporte y da fallos que no son del código. Los contenedores lo llevan desactivado.
- El antivirus de la máquina de desarrollo (AVG) vuelve a firmar las conexiones TLS desde el primer byte con destino a puertos de correo, incluso en bucle local. Por eso el test de TLS implícito no fija la huella del certificado; la validación del certificado la prueban los de STARTTLS.
- Lanzar la solución entera (`dotnet test OfiFlow.slnx`) ejecuta Infrastructure y Api a la vez y los dos levantan contenedores: aparecen cientos de fallos por contención que no son reales. Se lanzan por proyecto, uno detrás de otro.

### Bloque 2 — La invitación por correo (Application + Api)
- [ ] `InvitationEmail`: construye el `EmailMessage` (asunto fijo, texto plano, empresa, rol en palabras, caducidad, enlace con el token tras `#`); función pura, probada con una tabla de casos, incluidos nombres de empresa con caracteres de control
- [ ] `InviteUserCommandHandler`: tras las reglas de la invitación (R13) y antes de guardar, enviar si hay transporte; si falla, `invitation.email_not_sent` y no se guarda nada (R11)
- [ ] `InvitationCreatedDto`: `Token` pasa a ser opcional y se añade `EmailSent` (R10); el endpoint y su documentación (`OfiFlow.Api.http`) al día
- [ ] Error nuevo `invitation.email_not_sent` → 502 en el diccionario de errores (ADR-011) y su mensaje en español
- [ ] Eventos `1012` y `1013` en `SecurityEventIds`; registros sin dirección, sin token, sin enlace, sin texto del servidor
- [ ] Límite de envío por persona y hora para `POST /invitations` (D10): variante del limitador por `UserId`
- [ ] Aviso al arrancar en `Production` con `Provider = None` (D5)
- [ ] Tests (con el `IEmailSender` falso): con `None` todo igual que hoy; con `Smtp` se envía uno y la respuesta no trae token; el enlace sale de `LinkBaseUrl` aunque lleguen `Host` u `Origin` de un tercero; el enlace del correo se acepta de verdad; si falla el envío no existe la invitación; las reglas que rechazan no envían; aislamiento entre empresas; límite → 429; eventos sin datos personales
- [ ] Test de arquitectura: ningún caso de uso usa el transporte concreto, solo el puerto
- [ ] Mutaciones: enviar antes de las reglas; guardar aunque falle el envío; devolver el token con `Smtp`; enlace desde la cabecera `Origin`; token en la query y no en el fragmento; evento con la dirección; etc. (se cierra la lista al implementar)
- [ ] Verificación contra la API publicada de verdad con un SMTP local: se invita, se lee el correo recibido, se acepta con el enlace; se apaga el SMTP y se comprueba el 502 y que no queda invitación; se revisan los logs

### Bloque 3 — Documentación
- [ ] ADR-020: el correo (puerto, SMTP/MailKit, por qué sin cola todavía, el proveedor como encargado del tratamiento, cómo reconocer un proveedor caído por el evento `1013`, versiones elegidas)
- [ ] ADR-013 y spec 009: «Ampliada por ADR-020»; ADR-013 deja de decir «cuando exista el email» y dice qué pasó
- [ ] Manual: capítulos 6 (identidad: el nuevo flujo de invitación) y 8 (seguridad: secretos SMTP, `LinkBaseUrl`, datos personales), glosario, y la sección de **entrega de correo** (SPF, DKIM, DMARC, remite) para el despliegue
- [ ] Capítulo 9 (tests) con las cifras nuevas y la receta del SMTP de pruebas; `docs/README.md` con la fila de la spec y la ADR
- [ ] Preguntas del tribunal: «¿por qué el enlace no sale de la petición?» y «¿por qué no hay cola de correo?»

### Bloque 4 — Frontend (`ofiflow-frontend`)
- [ ] Modelo y servicio: `token` opcional y `emailSent` en la respuesta de invitar
- [ ] Pantalla del equipo: con `emailSent`, «Invitación enviada a …» y sin enlace que copiar; sin él, el enlace manual de hoy; el error `invitation.email_not_sent` se muestra con su mensaje
- [ ] Tests y mutaciones; comprobación en vivo contra la API real con el SMTP local
- [ ] [externo] Commit del frontend (con orden explícita)

---

## Definición de «Done»

- Compila sin avisos nuevos y todos los tests en verde (la suite completa).
- Las mutaciones de cada bloque, detectadas.
- Comprobado en vivo contra la API real y un SMTP local.
- Manual, ADR y spec al día, con las cifras reales (no estimadas).
- Ningún dato personal, token ni enlace en logs ni eventos.
