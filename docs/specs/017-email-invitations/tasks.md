# Tasks — 017-email-invitations

**Spec relacionada:** [./spec.md](./spec.md)
**ADR relacionadas:** [ADR-013](../../adr/ADR-013-gestion-de-usuarios.md) (se anota), [ADR-009](../../adr/ADR-009-seguridad-base.md), [ADR-011](../../adr/ADR-011-errores-codigos-diccionario.md) (error nuevo) y la **ADR-020** nueva (D12)
**Estado general:** Aprobada (decisiones D1–D13, 2026-11-02). Sin empezar.

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
- [ ] Antes de empezar: comprobar la versión actual de MailKit y la imagen del servidor SMTP de pruebas (que existan y estén mantenidas); anotar las versiones elegidas en la ADR-020
- [ ] `IEmailSender`, `EmailMessage` y `EmailDeliveryException` (con su categoría) en Application; el mensaje de la excepción no contiene la dirección ni texto del servidor
- [ ] `EmailOptions` (`Provider`, `From`, `LinkBaseUrl`, `Smtp:*`) con validación al arrancar (R4–R8): cifrado obligatorio salvo bucle local, remitente con forma de dirección, `LinkBaseUrl` en `https` salvo `localhost` y sin ruta ni barra final, tiempo mayor que cero
- [ ] `SmtpEmailSender` (MailKit): tiempo máximo, sin reintentos, asunto y direcciones sin posibilidad de inyección, categoría del fallo sin exponer el texto del servidor
- [ ] Registro en la API (`AddEmail`): con `None` no se registra ningún transporte (un `NullEmailSender` que **falla en voz alta** si algo lo llama, para que un uso olvidado no pase en silencio); la consola de administración no lo recibe (con test)
- [ ] Tests contra un SMTP de pruebas en contenedor: entrega el mensaje; con cifrado; con autenticación correcta e incorrecta; servidor que no contesta (tiempo máximo respetado); servidor que rechaza al destinatario; asunto y cuerpo íntegros con tildes y eñes; un destinatario con saltos de línea no inyecta cabeceras
- [ ] Tests de las opciones (cada valor inválido rechazado, valores por defecto aprobados) y de que la configuración inválida impide arrancar
- [ ] Mutaciones: cifrado desactivado; tiempo máximo ignorado; mensaje de error con la dirección; validación siempre verdadera; reintento añadido; etc. (se cierra la lista al implementar)

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
