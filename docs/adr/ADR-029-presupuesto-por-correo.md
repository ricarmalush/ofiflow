# ADR-029 — Presupuesto por correo: el primer correo a un tercero y el primer adjunto

**Estado:** Aceptado (2026-11-14). Las decisiones D1–D16 de la [spec 026](../specs/026-presupuesto-por-correo/spec.md) se aprobaron tal como estaban escritas (D1, D2, D3 y D4 confirmadas expresamente con la persona dueña del proyecto: **una acción aparte, con la dirección editable y precargada, envío síncrono y anotando solo el último envío**). Lo que cambió al implementar está más abajo.
**Fecha:** 2026-11-14
**Proyecto:** OfiFlow

---

## Contexto

Tras la spec 025 un presupuesto se podía descargar como PDF, pero para mandárselo a un cliente había que abrir otro programa de correo y adjuntarlo a mano: un paso más donde se manda el documento equivocado, a la dirección equivocada o, simplemente, no se manda. Esta spec lo manda desde OfiFlow, con el PDF adjunto, y deja anotado cuándo.

Es el **primer correo que OfiFlow manda a un tercero que no es usuario suyo** (el cliente de la empresa) y el **primero con adjunto**. Hasta ahora todos los correos (invitación, recuperar contraseña, confirmar el registro) iban a alguien que había pedido recibirlos, con un texto que escribía solo OfiFlow y sin adjuntos: la [ADR-020](ADR-020-correo-saliente.md) dejó cerradas dos reglas por eso («sin adjuntos» y «nada que escriba la persona entra en el correo»). Esta ADR enmienda la primera, con motivo, y **mantiene la segunda**.

Y es la operación **más abusable** de la plataforma: manda un mensaje a una dirección que **elige quien lo pide**, con un adjunto cuyo texto (título, descripciones, notas) también lo elige, **desde el dominio y con la reputación de OfiFlow**.

## Problema

- ¿Cómo se dispara: **acoplado** a «Enviar» (Draft → Sent) o como **acción aparte**? ¿Y si el correo falla, qué pasa con el estado?
- ¿A **quién** se manda: solo la dirección de la ficha del cliente, o una que escribe quien manda?
- ¿El envío es **síncrono** (la persona ve si salió) o por la **cola** que ya existe?
- ¿Qué **queda anotado** de cada envío, sabiendo que la dirección es un dato personal de un tercero?
- ¿Qué lleva el **correo**: puede quien manda escribir un mensaje propio? ¿Quién es el **remitente**?
- ¿Cómo entra un **adjunto** en un puerto que lo prohibía, sin abrir la puerta a «cualquier fichero»?
- ¿Qué **permiso** pide: uno nuevo o uno que ya existe?
- ¿Cómo se frena el **abuso**: *spam* y *phishing* desde el dominio de la plataforma con una sesión robada?
- ¿Qué pasa **si falla** el servidor de correo, **si no hay** servidor configurado o **si anotar falla** con el correo ya enviado?
- ¿Cómo se **prueba** que ni la dirección ni el documento llegan a un registro?

## Opciones consideradas

### A. El disparo

**A1. Una acción aparte, «Enviar por correo» (`POST /quotes/{id}/email`), independiente del estado.** *(elegida; D1)* Se puede **reenviar**, mandar un borrador para que el cliente lo vea antes de comprometerlo y, sobre todo, un fallo del correo no deja el estado a medias: no hay estado que deshacer. El coste es un clic más si se quieren las dos cosas.
**A2. Acoplado a «Enviar» (Draft → Sent).** Un solo clic, pero si el correo falla hay que decidir si el estado cambia o no, y no hay forma de reenviar. Se descarta.
**A3. La acción aparte con una casilla «marcar como enviado».** Más cómodo y más superficie (dos efectos en una operación). Queda para cuando se pida.

### B. La dirección

**B1. Editable, precargada con la de la ficha del cliente.** *(elegida; D2)* Cubre al cliente sin email guardado y al contacto de administración distinto del habitual. **Coste asumido:** la dirección la elige quien envía.
**B2. Solo la de la ficha.** Más simple y sin riesgo de mandar el documento a una dirección arbitraria, pero un cliente sin email no recibe nada hasta editar su ficha. **Es el endurecimiento que queda preparado** si se ve abuso.

### C. La entrega

**C1. Síncrona: la petición espera al servidor de correo.** *(elegida; D3)* Quien pulsa necesita saber si salió; y un adjunto de cientos de KB no debe vivir en una cola en memoria que se pierde al reiniciar. Se acota con el tiempo máximo que ya existe (10 s) y un límite de peticiones. Es el patrón de la invitación (ADR-020 B1).
**C2. Por la cola de la spec 018** (en memoria, sin reintentos ni persistencia). Respuesta inmediata, pero la persona no sabe si llegó. Se descarta.
**C3. Una cola persistida con reintentos.** Sigue abierta (ADR-020 B2); un adjunto con datos fiscales y precios sería, además, lo primero con datos delicados en ella.

### D. Qué se anota

**D1. El último envío en el propio presupuesto: cuándo y quién, nunca a quién.** *(elegida; D4)* `EmailedAt` y `EmailedByUserId`, una migración con dos columnas nulables. La dirección no se guarda.
**D2. Un historial de envíos** (una tabla: fecha, usuario, resultado, destinatario). Útil para «no me llegó», pero guarda emails de terceros y exige retención, purga y permisos de lectura. Se descarta por ahora.
**D3. Solo el evento de seguridad.** Nada en el presupuesto. Pierde lo único que la persona quiere ver («enviado por correo el…»).

### E. El contenido del correo

**E1. Plantilla fija: asunto «Presupuesto P-0012» y un cuerpo con el nombre de la empresa, el número, el total y la validez.** *(elegida; D9)* **Ni el título, ni las notas, ni el nombre del cliente, ni un mensaje propio.** Se mantiene la regla cerrada de la ADR-020.
**E2. Un campo «mensaje» para quien manda.** Convertiría a OfiFlow en un enviador de correo libre desde su dominio. Se descarta.

### F. El remitente

**F1. Fijo (el de la configuración) y la dirección de respuesta, la de la empresa.** *(elegida; D10)* La plataforma solo puede firmar (SPF/DKIM) con **su** dominio; el cliente que responde escribe a la empresa por `Reply-To`.
**F2. El nombre de la empresa como nombre del remitente.** Cualquiera podría llamarse «Banco X» y salir con la dirección de OfiFlow. Se descarta.
**F3. Un remitente por empresa (su propio dominio).** Es la solución de fondo; exige SPF/DKIM por empresa. Queda abierto.

### G. El adjunto en el puerto

**G1. Un adjunto controlado: uno solo, tipo de una lista cerrada (`application/pdf`), nombre restringido, de 1 byte a 5 MB, validado al construirse.** *(elegida; D12)*
**G2. Adjuntos en general.** Una vía de fuga y de inyección por cada tipo y cada nombre. Se descarta.

### H. El permiso

**H1. `Quotes.Issue`, sin permiso nuevo.** *(elegida; D7)* Es la acción que compromete un precio ante un tercero (la de «Enviar», «Aceptar» y «Rechazar»): Owner, Admin y Manager. Un `Employee` prepara y descarga el PDF, pero no lo manda.
**H2. `Quotes.Read`** (como el PDF). Mandarlo a un tercero no es leerlo. Se descarta.
**H3. Un permiso aparte (`Quotes.Email`).** Duplicaría la matriz sin proteger nada más.

### I. Si el servidor de correo no está configurado

**I1. `400 quote.email_disabled`.** *(elegida; D8)* A diferencia de la invitación, que podía devolver el token, aquí no hay alternativa útil. Es un error de **configuración del despliegue**, no de la persona.

## Decisión

### 1. El puerto de correo con adjunto

- **`EmailAttachment`** (Application): `(FileName, ContentType, Content)`, validado **al construirse**: tipo de una lista cerrada (hoy solo `application/pdf`), nombre `[A-Za-z0-9._-]{1,100}` y sin `..`, y de 1 byte a 5 MB. Un adjunto que no cumple **no llega a existir**; el transporte no tiene que fiarse de quien lo llama. Su `ToString()` no imprime nada.
- **`EmailMessage`** gana `ReplyTo` y `Attachment`, opcionales y al final: **los usos existentes no cambian** (un test fija que el mensaje sin ellos sale como antes, una sola parte de texto). Su `ToString()` ya no imprime destinatario ni cuerpo (el del `record` los imprimía).
- **`SmtpEmailSender`** monta `multipart/mixed` (texto más el PDF en base64 como `attachment`) y `Reply-To`, y este pasa por la **misma comprobación que el destinatario** antes de tocar la red. Solo la plantilla del presupuesto construye un adjunto y solo el transporte lo monta (un test de arquitectura lo fija).

### 2. La plantilla (`QuoteEmail`)

Una función pura. El **asunto** es «Presupuesto P-0012» (solo el número). El **cuerpo**, texto plano: el nombre fiscal de la empresa **en una sola línea** (espacios de cualquier clase, control y formato —como U+202E— fuera), el número, el total (con el mismo `MoneyFormat` del PDF, **desde céntimos**), la validez en `dd/MM/yyyy` **con cultura fija**, la indicación de que el PDF va adjunto y de a quién escribir. Su firma **no admite** título, notas ni nombre de cliente (un test lo fija: si alguien añade un parámetro, lo decide a propósito). La dirección de respuesta es el email del perfil fiscal, si lo tiene.

### 3. La operación

- **`POST /api/v1/quotes/{id}/email`** con `{ recipient }`; `200 { emailedAt }`: solo cuándo, ni la dirección ni el nombre del fichero. `Cache-Control: no-store`.
- **Orden de comprobaciones:** sesión (`401`) → permiso `Quotes.Issue` (`403`) → **límite por persona** (`429`; solo gasta cupo quien tiene el permiso) → forma del cuerpo (`400`, antes de leer nada) → correo configurado (`400 quote.email_disabled`) → presupuesto (`404`) → líneas (`400 quote.email_requires_items`) → perfil (`400 quote.company_profile_incomplete`) → generar el PDF → **enviar** → anotar.
- **Un solo constructor del PDF** (`QuotePdfBuilder`): la descarga y el correo llaman al mismo servicio, así que el documento que recibe el cliente es **el mismo** que descarga la persona. Va en dos pasos (`LoadAsync` y `RenderAsync`) porque la comprobación de las líneas va **entre** el `404` y el perfil.
- **La dirección** la valida un `Validator` estricto, más que `Email`: **una sola**, de 254 caracteres como mucho, sin espacios, sin caracteres de control y sin coma, punto y coma, ángulos, comillas, paréntesis, corchetes ni barra invertida (con los espacios de los extremos recortados). `Email.IsValid` deja pasar, por ejemplo, `<ana@ejemplo.es>` o `ana,otra@ejemplo.es`: sirve para un campo de una ficha, no para una cabecera. Es la misma lista que el transporte rechaza antes de la red.
- **Se envía antes de anotar** (ADR-020 C1) y **anotar no puede estropear un envío ya hecho**: si el servidor aceptó el mensaje, la respuesta es `200` aunque guardar la fecha falle (un conflicto de concurrencia, o un borrador eliminado entre medias). No se reintenta; el evento `1410` dice si se anotó.
- **Si el servidor de correo no acepta:** `502 quote.email_not_sent` **sin el motivo ni la dirección** (`ExternalServiceException`, como la invitación); no se anota nada y la categoría del fallo va al evento `1411`.

### 4. Qué se anota y qué se registra

- **`Quote.EmailedAt` y `EmailedByUserId`** (migración `AddQuoteEmail`, dos columnas nulables): el último envío. `RecordEmailSent` vale en **cualquier estado** y **no es un cambio del contenido**: no sube `Revision`, no toca líneas ni totales y un presupuesto enviado sigue siendo inmutable. Anotar cambia la versión de fila, así que **una edición abierta recibe el conflicto de siempre**: el frontend vuelve a leer el presupuesto tras mandarlo. `EmailedByUserId` queda para auditoría y no sale por la API; el `QuoteDto` gana `emailedAt`.
- **Eventos de seguridad `1410` (enviado) y `1411` (fallido, con la categoría)**: **solo Ids** (presupuesto, empresa, persona) y la categoría. **Nunca** la dirección, el asunto, el cuerpo, el nombre del fichero ni el texto del servidor. Un escaneo fija que el handler solo registra esos dos eventos y solo esos datos, y otro que la dirección no tiene dónde guardarse (ni propiedad, ni columna, ni DTO). Son **56 eventos**.
- **Tres códigos de error nuevos** (ya hay **70**): `quote.email_disabled` y `quote.email_requires_items` (`400`, de negocio) y `quote.email_not_sent` (`502`).

### 5. Quién, cuánto y con qué límites

- **`Quotes.Issue`**, sin permiso nuevo: la matriz sigue en 20 permisos y 100 celdas; la tabla de operaciones pasa a **59** y los endpoints de presupuestos, a **10**. Una **invariante comprobada** por un test de Domain y otro por HTTP: todo rol con `Quotes.Issue` tiene `Quotes.Read` y `Company.Read`.
- **La puerta del segundo factor** ([ADR-024](ADR-024-segundo-factor-obligatorio.md)) se aplica sola: la operación no está en la lista cerrada.
- **Dos límites:** **20 por hora y por persona** (`QuoteEmailThrottle`, como las invitaciones: la clave es la persona, que solo se conoce ya autenticada) y **30 por minuto y por IP** (política `quote-email`, antes de autenticar, para frenar un bucle). El de la persona **solo gasta cupo quien tiene el permiso**: un `Employee` o un `Technician` recibe su `403` sin gastar uno. Un `429` lleva `Retry-After`.

### 6. El frontend

Un botón **«Enviar por correo»** (con `Quotes.Issue`) y un diálogo con la dirección del cliente precargada y editable. Mientras envía, el botón se desactiva y el diálogo no se cierra. Con éxito se cierra, avisa con el número del presupuesto (**nunca la dirección**) y vuelve a leer el presupuesto, que muestra «Enviado por correo el…». Cada error tiene su texto, el de la API (que no repite la dirección): correo sin configurar, sin líneas, servidor que no acepta, dirección inválida; un `429` añade cuánto esperar (`Retry-After`, leído en la utilidad común de resultados); y la falta de datos fiscales se convierte en el aviso de siempre, con su enlace. La dirección vive solo mientras el diálogo está abierto: ni almacenamiento, ni URL, ni estado de la ruta. La misma regla de una sola dirección vive en `quote.model` para no hacer un viaje de ida y vuelta, pero quien manda es siempre la API.

## Consecuencias

### Positivas

- **El cliente recibe el mismo documento que descarga la persona**, con los mismos importes: un solo constructor del PDF y los importes nunca se recalculan.
- **El correo no puede escribir texto libre a un tercero**: plantilla fija, asunto con solo el número y remitente que no controla la empresa.
- **Un adjunto que no es un PDF de nombre y tamaño razonables no existe:** se rechaza al construirse, antes de tocar la red.
- **Ni la dirección ni el documento llegan a un registro,** ni a un evento ni a una respuesta, y la dirección **no se guarda**.
- **Quien no puede mandar no gasta el límite** de quien sí puede.
- **Un fallo del servidor de correo no deja nada a medias:** no se anota nada y la persona lo sabe (`502`) y puede descargar el PDF y mandarlo ella.
- **El backend puede desplegarse solo:** una operación nueva, una migración con dos columnas nulables y ningún uso existente cambia.

### Negativas / riesgos aceptados

- **El principal: *phishing* o *spam* desde el dominio de OfiFlow.** Una cuenta con `Quotes.Issue` (robada o maliciosa) puede mandar un correo con un PDF cuyo **texto es libre** (notas de hasta 2000 caracteres, descripciones) a **cualquier** dirección, hasta 20 por hora. Se mitiga, no se elimina: cuerpo y asunto fijos, PDF sin enlaces activos, límite por persona y por IP, segundo factor obligatorio para Owner y Admin y rastro (`1410`/`1411` con persona y presupuesto) para **suspender la empresa**. Lo que lo cerraría del todo es **limitar el envío a la dirección guardada en la ficha del cliente** (B2) o un tope por empresa y día; es la primera cosa que endurecer si se ve abuso.
- **Reputación de envío compartida:** todas las empresas mandan con el remitente de OfiFlow; una que provoque quejas daña la entrega de las demás. Cuando haya volumen, remitente por empresa (con su dominio, SPF/DKIM) o un proveedor que gestione la reputación.
- **El correo no es una prueba de entrega:** `200` significa «el servidor de correo aceptó el mensaje», no «el cliente lo recibió ni lo leyó» (sin rebotes ni confirmaciones). Queda `emailedAt`, no un acuse.
- **Sin cifrado de extremo a extremo:** quien tenga acceso al buzón del cliente tiene el presupuesto. El tramo de OfiFlow al servidor de correo es siempre cifrado (ADR-020).
- **El PDF enviado no se guarda:** si la empresa cambia sus datos fiscales, la próxima descarga o reenvío saldrá con los nuevos y no queda copia de lo que recibió el cliente. Para facturas no valdrá; llega con la fase 4.
- **Dos envíos seguidos llegan dos veces:** no hay idempotencia; reenviar es legítimo y la protección es el botón desactivado y el límite.
- **No se reintenta anotar:** si dos cambios coinciden en los milisegundos entre enviar y anotar, el correo sale y la fecha no se guarda (el evento lo dice). La alternativa, un `409` con el correo ya entregado, llevaría a reenviar.
- **El envío es síncrono:** la petición espera al servidor de correo (hasta 10 s); si el proveedor va lento, la persona espera.
- **La dirección la elige quien manda** (B1); se compensa con los límites y la plantilla fija.
- **Pendientes que no son código** (de la persona dueña del proyecto): SMTP con **dominio propio** y SPF/DKIM/DMARC antes de mandar a clientes reales, el **contrato de encargado del tratamiento** con el proveedor y los **textos legales** que cubran este envío.

### Lo que cambió al implementar

1. **No hay reintento al anotar.** La spec decía «un conflicto se reintenta una vez»; la capa de datos que ve Application no permite releer una entidad que ya está seguida, y la ventana es de milisegundos. La spec se corrigió (R8 y D6).
2. **El orden de comprobaciones es otro:** el límite por persona va **justo después del permiso** (y solo gasta quien lo tiene) y la forma del cuerpo se valida antes de leer nada (R7 corregida).
3. **`MoneyFormat` pasó de Infrastructure a Application:** el cuerpo del correo también formatea el total y el formato del dinero no puede tener dos copias.
4. **`Email.IsValid` no basta para una cabecera:** se descubrió al escribir los casos hostiles (`<ana@ejemplo.es>` y `ana,otra@ejemplo.es` pasan su expresión). El validador es más estricto y el transporte lo repite.
5. **`EmailMessage.ToString()`:** el `record` imprimía destinatario y cuerpo; ahora no imprime nada, y lo mismo `EmailAttachment`, el comando, `QuotePdfDocument` y `QuotePdfSource`.
6. **`QuotePdfBuilder` en dos pasos** (cargar y generar) porque la comprobación de las líneas va entre el `404` y el perfil.
7. **Cinco tests de arquitectura tuvieron que adaptarse** («presupuestos nunca escriben en el registro» pasó a tener **una excepción acotada**: solo el handler del correo, solo dos eventos y solo Ids y categoría; la lectura del PDF se vigila ahora en el servicio compartido) y los que cuentan endpoints, manejadores y operaciones fallaron a propósito (9 → 10, 9 → 10 y 58 → 59).
8. **Anotar el envío cambia la versión de fila,** así que una edición abierta choca. No se evitó (no es un cambio del contenido, pero sí de la fila): el frontend vuelve a leer el presupuesto tras mandarlo y un test de persistencia lo fija.
9. **`Retry-After` se lee en la utilidad común de resultados del frontend** (validado: solo un entero positivo y razonable), no en un parche del componente.
10. **Una mutación equivalente documentada:** pedir `SevenBit` en vez de `Base64` para el adjunto no cambia el mensaje (MimeKit codifica en base64 toda parte que no es de texto al enviar); la línea explícita queda como declaración de intención.
11. **Las mutaciones dejaron dos supervivientes de verdad y las dos eran tests flojos, no código:** una mutación por cada carácter prohibido de la dirección (`;`, `<`, `>`, `(`, `)`, `[`, `]`) sobrevivía porque cada caso de prueba tenía **dos** razones para fallar; y la fecha de validez no se comprobaba en el cuerpo del correo. Se añadieron casos con **un solo** carácter cada vez y la aserción de la validez. Otra mutación (quitar la comprobación de espacios) era **equivalente** —`Email.IsValid` ya los rechaza— y se quitó el código redundante.
12. **Una frase del correo terminaba en el nombre de la empresa:** con «Fontanería Demo S.L.» salía «S.L..» con dos puntos. Lo destapó la comprobación en el navegador; las frases se reformularon para que ninguna termine en el nombre y un test fija que no hay «..» en el cuerpo, con y sin respuesta a la empresa.

### Hallazgos de la implementación (útiles para el tribunal)

- **El correo a un tercero es una superficie de abuso, no solo de privacidad:** el riesgo no es que se filtre un dato, sino que la plataforma sirva de **altavoz** (*spam*, *phishing*) con su dominio y su reputación. Por eso se limitó **quién** (permiso y segundo factor), **cuánto** (dos límites), **qué** (plantilla fija, adjunto controlado) y **desde dónde** (remitente fijo).
- **Mantener una regla cerrada y enmendar la otra, con motivo:** «sin adjuntos» se abrió en su forma mínima (uno, un tipo, un nombre, un tamaño); «nada que escriba la persona entra en el correo» se mantuvo entera. El texto libre del presupuesto va **dentro del PDF**, que es un documento, no en el cuerpo.
- **La dirección es un dato personal de un tercero que no ha firmado nada con OfiFlow:** no se guarda (solo cuándo y quién), no se registra, no vuelve en la respuesta y no sale en un error. Un test marca una dirección única y comprueba que no está en **ningún** registro, ni siquiera cuando todo falla.
- **Defensa en profundidad:** la misma lista de caracteres prohibidos se comprueba en el validador (400 limpio), en el frontend (sin ir y volver) y en el transporte (antes de la red, aunque llegue un valor fabricado saltándose el dominio).
- **El límite por persona se consulta después del permiso:** si no, un `Technician` agotaría un cupo que no va a usar y recibiría `429` en vez de `403`. Un test pide 25 veces como `Employee`.
- **El `ToString()` de un `record` imprime todas sus propiedades:** ya era una regla de la casa para los datos personales; el correo la extiende a la dirección, el cuerpo y el documento.
- **Un test de «solo estos parámetros» fija lo que NO entra:** la firma de la plantilla no admite título, notas ni cliente; si alguien añade uno, el test obliga a decidirlo.
- **Leer lo recibido, no lo enviado:** los tests del transporte leen el mensaje que **recibió** un servidor SMTP de verdad (Mailpit): partes, tipo, nombre, `Reply-To` y los bytes del adjunto, con los 256 valores de byte.
- **Las mutaciones destapan casos redundantes:** una mutación por cada carácter prohibido sobrevivió porque cada caso de prueba tenía **dos** razones para fallar (una coma «redundante» con un `@` de más); hace falta un caso **con un solo carácter** a la vez.
- **Un commit por orden no puede cruzarse con una serie de mutaciones:** las series se interrumpieron dos veces para commitear, y las dos veces se restauraron los ficheros y se pasó la suite completa antes. Las mutaciones se relanzaron después sobre el código ya commiteado.
- **Mis propias comprobaciones también se equivocan:** la comprobación en vivo falló cuatro veces antes de probar nada del código —un `sed` mío cambió los puertos internos de Mailpit (`1025` y `8025` pasaron a `1026` y `8026`), Git Bash convirtió en ruta de Windows un secreto en base64 que empezaba por `/`— y, ya con el sistema en marcha, **dos de sus expectativas eran erróneas** (la revisión de un borrador nace en 1, no en 0; y otra persona había reenviado el mismo presupuesto entre medias). Ninguna era un fallo del código, pero todas se averiguaron mirando el registro y el estado real en vez de dar el código por culpable.
- **En el navegador** (con un `Manager`, que no necesita segundo factor): sin datos fiscales el botón abre el diálogo con la dirección de la ficha y, al enviar, la API responde `400` y la pantalla se queda con el aviso «Pídele a un administrador…» sin error suelto; con el perfil guardado, una lista de direcciones se rechaza en el diálogo; con una dirección válida distinta de la de la ficha se cierra el diálogo, sale «Presupuesto enviado por correo P-0001» y el presupuesto muestra «Enviado por correo el…»; y el servidor de correo recibió el mensaje **en la dirección escrita y ninguno en la de la ficha**, con su adjunto de 38 KB.

### Cifras

- **Backend:** Domain 630, Application 1423, Infrastructure 1094 y Api 1161 = **4308 casos** (+216 respecto a la spec 025: +69 en el bloque 1, +136 en el 2 y +11 de los refuerzos tras las mutaciones), todo en verde. **125 mutaciones a mano sobre el código real** (60 del puerto y la plantilla, 65 de la operación y la API): **123 detectadas**, una **equivalente** documentada (pedir `SevenBit` en vez de `Base64` para el adjunto: MimeKit codifica en base64 toda parte que no es de texto) y una **redundante** cuyo código se quitó (la comprobación de espacios del validador).
- **Comprobado en vivo** contra la API publicada, SQL Server real y un servidor SMTP real (Mailpit), leyendo lo que el servidor **recibió**: **76/76**. Un envío con SMTP real tarda unos 170 ms; el `502` con el servidor caído, unos 2 s.
- **Frontend:** **936 specs** en verde (+40 sobre las 896 de la spec 025), **60 de 60 mutaciones** detectadas (más una redundante, cuyo código se quitó) y comprobado en el navegador integrado contra la API real.

### Recomendación de despliegue (no es código)

1. **Aplicar la migración `AddQuoteEmail`** (dos columnas nulables, sin tocar datos) antes de desplegar el backend.
2. **Configurar el servidor de correo** (`Email:Provider = Smtp`) con **un dominio propio** y publicar **SPF, DKIM y DMARC** de ese dominio antes de mandar a clientes reales: con el remitente de pruebas los correos acabarán en el spam o rechazados. Sin servidor, la operación responde `400 quote.email_disabled` (el aviso de arranque en `Production` con `Provider = None` ya existe).
3. **Revisar el contrato de encargado del tratamiento** con el proveedor de correo y los **textos legales**: OfiFlow entrega, a petición de la empresa, un correo con datos de un cliente de esta.
4. **El backend puede desplegarse solo:** añade una operación, una columna y un límite. El frontend va después (sin él el correo existe pero nadie lo pide).
5. **Detrás de un proxy inverso** el límite por IP necesita `UseForwardedHeaders` con proxies de confianza (decisión pendiente de la [ADR-009](ADR-009-seguridad-base.md)); el límite por persona no depende de la IP.
6. **Probar de punta a punta con una cuenta real** antes de abrir a clientes: mandar un borrador y uno enviado a una dirección propia, abrir el correo y el PDF en un cliente de correo real, comprobar el `Reply-To` y que un `Employee` recibe `403`.
7. **Vigilar los eventos `1411` con la categoría `Authentication` o `Unavailable`:** son la señal de que el proveedor está mal configurado o caído.

### Qué queda cerrado (no se debe reabrir sin una razón nueva y explícita)

- **Nada que escriba la persona entra en el asunto ni en el cuerpo del correo;** el texto libre va dentro del PDF.
- **El adjunto es uno, un PDF, con nombre y tamaño acotados** y se valida al construirse.
- **La dirección no se guarda, no se registra y no vuelve en ninguna respuesta.**
- **El remitente lo decide la configuración, nunca la empresa;** la empresa solo es la dirección de respuesta.
- **Se envía antes de anotar,** y anotar no puede deshacer un envío entregado.
- **El permiso es `Quotes.Issue`,** con la invariante de que todo rol que lo tiene lee presupuestos y datos de la empresa.
- **Dos límites** (por persona y por IP), y el de la persona solo lo gasta quien tiene el permiso.
- **Mandar por correo no cambia el estado del presupuesto.**

### Qué queda abierto para revisar más adelante

- **Endurecer el destinatario** (solo la dirección de la ficha) o un **tope por empresa y día**, si se ve abuso.
- **Remitente por empresa** (su dominio, SPF/DKIM) o un proveedor que gestione la reputación; **rebotes, quejas y confirmación de lectura**.
- **Un historial de envíos** (si «no me llegó» se vuelve frecuente), con su retención y su purga.
- **Un mensaje propio opcional,** `Cc`/`Bcc`, varios destinatarios, HTML con marca, plantillas y logotipo.
- **Una cola persistida con reintentos** (ADR-020 B2) y un enlace para que el cliente acepte el presupuesto desde el correo.
- **Mandar otros documentos** (la factura, fase 4) y **guardar una copia** del PDF enviado.
- **Un evento de descarga del PDF** (la 025 lo dejó abierto) ahora que el envío sí deja rastro.

---

## Relación con otros ADR

- Amplía: **[ADR-020](ADR-020-correo-saliente.md)** (el correo saliente: un adjunto controlado y una dirección de respuesta; la regla «nada que escriba la persona entra en el correo» se mantiene), **[ADR-028](ADR-028-pdf-presupuesto.md)** (el PDF ya se manda), **[ADR-026](ADR-026-presupuestos.md)** (los presupuestos), **[ADR-012](ADR-012-autorizacion-rbac.md)** (la invariante de `Quotes.Issue`) y **[ADR-011](ADR-011-errores-codigos-diccionario.md)** (tres códigos más: 70).
- Relacionada con: **[ADR-027](ADR-027-perfil-fiscal-empresa.md)** (los datos fiscales y el email de respuesta que salen de él), **[ADR-009](ADR-009-seguridad-base.md)** (datos personales: la dirección de un tercero; los límites de peticiones), **[ADR-024](ADR-024-segundo-factor-obligatorio.md)** (la puerta se aplica sola) y **[ADR-021](ADR-021-recuperacion-de-contrasena.md)** (la cola en memoria que aquí no se usa).
- Prepara: el correo de las **facturas** (fase 4), que exigirá conservar el documento tal como se emitió, y el **enlace de aceptación** del presupuesto para el cliente.
