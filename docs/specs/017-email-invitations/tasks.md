# Tasks — 017-email-invitations

**Spec relacionada:** [./spec.md](./spec.md)
**ADR relacionadas:** [ADR-013](../../adr/ADR-013-gestion-de-usuarios.md) (se anota), [ADR-009](../../adr/ADR-009-seguridad-base.md), [ADR-011](../../adr/ADR-011-errores-codigos-diccionario.md) (error nuevo) y la **ADR-020** nueva (D12)
**Estado general:** Aprobada (decisiones D1–D13, 2026-11-02). Bloques 1 y 2 hechos y commiteados; bloque 3 (documentación) hecho, sin commitear; bloque 4 (frontend) sin empezar.

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
- [x] `InvitationEmail` (función pura): asunto fijo sin ningún dato, texto plano en español con la empresa, el rol (las mismas etiquetas que el frontend), la caducidad y el enlace; el nombre de la empresa se deja en una sola línea y sin caracteres de control
- [x] Dos puertos nuevos en Application: `IEmailSender.IsEnabled` (el caso de uso comprueba antes de enviar; el transporte nulo sigue lanzando si alguien lo olvida) e `IEmailLinks` (el enlace sale **solo** de `Email:LinkBaseUrl`, con el token en el fragmento y codificado); `EmailLinks` en Infrastructure
- [x] `InviteUserCommandHandler`: con correo, envía **después** de todas las reglas (R13) y **antes** de guardar (R11, D3); si falla, `ExternalServiceException` y no se guarda nada. Lee el nombre de la empresa por el `TenantId` del contexto (añadido a la lista de lecturas de entidades globales, con su motivo)
- [x] `InvitationCreatedDto(Id, Token?, ExpiresAt, EmailSent)`: con correo, `token` es `null` y `emailSent` verdadero; sin correo, todo como antes de la spec 017. `OfiFlow.Api.http` al día
- [x] `invitation.email_not_sent` → **502** (nuevo `ExternalServiceException`, título `problem.title.bad_gateway`, entradas en el diccionario; el test del diccionario completo los exige)
- [x] Eventos **`1407`** (enviado) y **`1408`** (fallido, con la categoría) en `SecurityEventIds`. **Cambio respecto al borrador de la spec**: eran `1012` y `1013`, pero esos números son de la familia de autenticación y las invitaciones viven en la `14xx`. La spec está corregida
- [x] Límite de **20 invitaciones por hora y por persona** (`InvitationThrottle`, por `UserId`). **Cambio respecto al borrador**: no es una política más del limitador global, porque ese va *antes* de autenticar (ADR-017) y solo conoce la IP; el endpoint lo consulta justo antes de enviar la orden y responde el mismo 429 de siempre (`RateLimiting.RejectAsync`, compartido). Mover el limitador detrás de la autenticación habría cambiado a todos los demás endpoints
- [x] Aviso al arrancar en `Production` con `Provider = None` (`EmailStartupCheck`), probado con un entorno falso y con un test que impide borrar la llamada de `Program.cs`
- [x] Tests: Application (44 de invitar, 16 de la plantilla): camino feliz, el hash guardado es el del token del correo, se envía antes de guardar, cada categoría de fallo, eventos sin datos personales, ningún envío si la regla rechaza (jerarquía, email con cuenta, ya pendiente, tope de pendientes), empresa propia y no ajena. Infrastructure: `EmailLinks`, `IsEnabled`, el aviso de producción. Api con SQL Server real: respuesta sin token, el enlace del correo **acepta de verdad** la invitación, `Host`/`Origin`/`Referer`/`X-Forwarded-Host` de un tercero no cambian el enlace, 502 y nada creado, logs sin dirección/token/enlace, 429 al 21.º envío y por persona, contrato del 502
- [x] Tests de arquitectura: MailKit/MimeKit solo en `Infrastructure/Mail` (del bloque 1); el enlace solo se escribe en `EmailLinks`; ningún fichero de correo o de invitar lee cabeceras de la petición
- [x] Mutaciones (21, todas detectadas): no enviar nunca; devolver siempre el token; enviar después de guardar; tragarse el fallo; la dirección en el evento de fallo; empresa en el asunto; saltos de línea en el nombre de la empresa y en el cuerpo; etiqueta de rol; la «primera empresa» en vez de la del contexto (esta **sobrevivió** hasta que el test pasó a guardar primero una empresa ajena); token en la consulta y sin codificar; transporte «apagado»; límite compartido, que nunca rechaza, de 21 o sin registrar; sin `Cache-Control`; 502 que pasa a 500; `Retry-After` inútil; aviso de producción que nunca sale
- [x] Verificación contra la API publicada de verdad (base desechable, Mailpit real): se invita → `201` con `token: null`, `emailSent: true` y `Cache-Control: no-store`; el correo recibido tiene remitente, asunto fijo, empresa, rol, caducidad y el enlace con el token en el fragmento; **aceptar con ese enlace da `204` y la invitada entra (`200`)**; con el servidor de correo parado, `502` con el código y sin motivo, y la invitación no existe; el registro tiene `1407` y `1408` y **cero** apariciones de la dirección, el token o el enlace
- [ ] [externo] Commit del bloque 2 (con orden explícita)

**Hallazgos del bloque 2**
- Un test de aislamiento que parecía fuerte no lo era: con la empresa propia guardada *antes* que la ajena, una consulta que cogiera «la primera empresa» daba el mismo resultado. Lo cazó una mutación; ahora la ajena se guarda la primera.
- Dos limitaciones asumidas del límite por persona: es en memoria y por instancia (como los demás límites, ADR-009) y se reinicia al reiniciar la API.

### Bloque 3 — Documentación
- [x] [ADR-020](../../adr/ADR-020-correo-saliente.md): contexto, seis grupos de opciones (transporte, cola, orden de envío, sin correo, origen del enlace, dónde vive el límite), decisión en dos partes, consecuencias con los riesgos aceptados (el proveedor como encargado del tratamiento, «aceptado» no es «entregado», sin cola, buzón comprometido), **lo que cambió al implementar** (números de los eventos y dónde vive el límite), los hallazgos, las versiones fijadas (MailKit 4.18.2, Mailpit v1.33.0, comprobadas el 2026-11-02), la recomendación de despliegue (proveedor, dominio, SPF/DKIM/DMARC, secretos, `LinkBaseUrl`, vigilar el `1408`, primera invitación real) y lo cerrado y lo abierto
- [x] ADR-013, spec 009 y ADR-009 anotadas: ADR-013 ya no dice «cuando exista el email» sino qué pasó (actualización, riesgo resuelto, NEXT tachado y relación «Ampliada por»); spec 009, el NEXT del email; ADR-009, el catálogo de eventos con `1407` y `1408`
- [x] Manual: **capítulo 1** (probar el correo en local con Mailpit, y el error de arranque en la tabla de fallos), **capítulo 6** (el recorrido con y sin correo, la sección «El correo» con las cinco ideas, «Para que el correo llegue de verdad», las lecciones, seis filas en la tabla de amenazas, el límite 10, dos preguntas), **capítulo 8** (los secretos del correo, el límite de invitaciones, los eventos `1407`/`1408`, seis filas en el mapa de amenazas y el límite 6 reescrito), **capítulo 0**, **glosario** (términos nuevos) y la sección de **entrega de correo (SPF, DKIM, DMARC)**, que vive en el capítulo 6 y en la ADR-020 porque el manual no tiene un capítulo de despliegue
- [x] Capítulo 9: cifras reales por proyecto (Domain 244, Application 671, Infrastructure 551, Api 577; 2043 casos), la sección de la spec 017 con las tres formas de probar y por qué, las 54 mutaciones, la verificación en vivo, cinco filas nuevas en «Errores reales», y una advertencia medida sobre lanzar la solución entera
- [x] `docs/README.md` con la ADR-020 y la spec 017 «En implementación»; el glosario y el índice de especificaciones del capítulo 12 **se pusieron al día de paso** (le faltaban las ADR 016, 018 y 019 y las specs 011 a 016)
- [x] Preguntas del tribunal: «¿de dónde sale el enlace y por qué no de la petición?», «¿qué pasa si el correo no se envía y por qué no hay cola?», «¿qué datos nuevos salen con el correo?», «¿cómo evitas llenar de correo a un tercero?», y la respuesta antigua sobre devolver el token, reescrita
- [ ] [externo] Commit del bloque 3 (con orden explícita)

**Pendiente de los hallazgos:** la corrección de `Email.Create` (`$` por `\z`) se hace en otra sesión y se commitea aparte; el capítulo 6 y la ADR-020 la dan por hecha («se corrige allí»). Si no llegara a entrar, hay que retocar esas dos frases.

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
