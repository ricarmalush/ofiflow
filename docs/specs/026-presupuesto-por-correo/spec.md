# Spec — 026-presupuesto-por-correo

**Estado:** Implementada (2026-11-15). Aprobada tal como estaba escrita el 2026-11-14: cuatro decisiones de forma **confirmadas con la persona dueña del proyecto** (D1, D2, D3 y D4) y el resto (D5–D16) aprobadas con la spec. Lo que cambió al implementar, las cifras y los hallazgos están en la [ADR-029](../../adr/ADR-029-presupuesto-por-correo.md)
**Fecha:** 2026-11-14 (implementada el 2026-11-15)
**Bounded Context:** Quotes (con el puerto de correo de Application/Common y su transporte en Infrastructure/Mail)
**Depende de:** spec 025 y ADR-028 (el PDF, que esta spec adjunta), spec 023 y ADR-026 (presupuestos), spec 024 y ADR-027 (el perfil fiscal), spec 017 y ADR-020 (el correo saliente: **esta spec rompe dos de sus reglas cerradas, «sin adjuntos» y «nada de fuera entra en el correo», y las enmienda con motivo**), ADR-012 (permisos), ADR-009 (datos personales y registro), ADR-011 (códigos de error), ADR-024 (la puerta del segundo factor: se aplica sola)

> **Numeración:** 026 es la siguiente spec; su ADR será la **029** (la 028 es la del PDF).
> **Base:** la rama parte de `spec/025-quote-pdf` (el PR #70 del backend sigue abierto): el correo adjunta el PDF, que solo existe ahí.

---

## Objetivo

Que una empresa pueda **mandar un presupuesto por correo a su cliente, con el PDF adjunto**, sin salir de OfiFlow y sabiendo si el mensaje salió.

Tras la spec 025 el PDF se descarga y hay que adjuntarlo a mano en otro programa de correo: un paso más donde se manda el documento equivocado, a la dirección equivocada o, simplemente, no se manda. Con esta spec el envío sale **del mismo dato que ve el servidor** (el mismo PDF, sin recalcular nada) y queda anotado cuándo se mandó el último. Es el cierre de la fase 3 del flujo de valor (cliente → trabajo → **presupuesto** → cita → factura → cobro).

Es también el **primer correo que OfiFlow manda a un tercero que no es usuario suyo** (el cliente de la empresa) y el primero con **adjunto**: lo que más cuidado exige, y por eso la spec es larga en seguridad y corta en funciones.

## Alcance — Qué SÍ incluye

Cuatro bloques:

1. **El puerto de correo con adjunto (backend):** `EmailMessage` admite **un** adjunto controlado y una dirección de respuesta; `SmtpEmailSender` los construye; la plantilla fija del correo del presupuesto. Probado contra el servidor SMTP de pruebas leyendo el mensaje de vuelta.
2. **La operación y su API:** `POST /api/v1/quotes/{id}/email`, la anotación del último envío (una migración), el error de envío fallido, un límite por persona, dos eventos de seguridad y los tests de extremo a extremo (roles, aislamiento entre empresas, contenido, fallos del servidor de correo).
3. **Frontend:** un botón «Enviar por correo» con un diálogo (la dirección del cliente precargada y editable), los avisos de cada error y «Enviado por correo el…» en el presupuesto.
4. **Documentación:** ADR-029, el manual y la spec cerrada.

## Alcance — Qué NO incluye

- **LATER:** que quien envía escriba un **mensaje propio** en el cuerpo (D9: es la puerta al *phishing* desde el dominio de OfiFlow); copias (`Cc`) y copias ocultas; varios destinatarios; HTML con marca y logotipo; plantillas por empresa; **remitente por empresa** (hoy el remitente es el de la plataforma y la empresa va en la dirección de respuesta); idiomas distintos del español.
- **LATER:** un **historial** de envíos (una tabla con cada uno: fecha, usuario, resultado): D4 guarda solo el último, sin la dirección; si «no me llegó» se vuelve una queja frecuente, es la siguiente spec.
- **LATER:** **cola persistida con reintentos** (ADR-020 B2): el envío es interactivo y la persona ve el resultado (D3). **Rebotes y quejas** (hace falta un proveedor que los notifique), **confirmación de lectura** y **enlace de aceptación** para que el cliente acepte desde el correo (otra spec, con su propia superficie: un enlace público).
- **LATER:** mandar **otros documentos** (la factura; fase 4) y **guardar una copia** del PDF enviado (D4 de la spec 025: llega con las facturas).
- **NEVER:** que un correo salga con **importes o datos que no sean los del servidor**; que **otra empresa** pueda mandar o ver un presupuesto de la primera; que la dirección del destinatario, el PDF o el texto del servidor de correo lleguen a un registro, un evento o un error; que el envío pueda **inyectar cabeceras** o adjuntar algo que no sea el PDF del presupuesto.
- **No cambia:** el PDF (se reutiliza tal cual), el cálculo del presupuesto, sus estados y transiciones (enviar por correo **no** cambia el estado: D1), el perfil fiscal ni la matriz de permisos (D7: `Quotes.Issue` ya existe).

## Reglas de negocio / invariantes

**La operación**

- **R1. Una sola operación: mandar el presupuesto por correo.** `POST /api/v1/quotes/{id}/email` con un cuerpo `{ "recipient": "…" }` devuelve `200` con `{ "emailedAt": "…" }` cuando el servidor de correo **aceptó** el mensaje. La empresa sale **solo del token**; el presupuesto de otra empresa es el mismo `404` que uno inexistente (filtro global, R12 de la spec 023).
- **R2. Es independiente del estado del presupuesto** (D1). Se puede mandar un **borrador** (el PDF lleva la marca «BORRADOR», como en la spec 025) o uno ya enviado, aceptado o rechazado, y **se puede reenviar** las veces que haga falta. **No cambia el estado** del presupuesto: «Enviar» (Draft → Sent) y «enviar por correo» son dos acciones que la persona decide por separado.
- **R3. Hace falta al menos una línea.** Un presupuesto sin líneas no se manda: `400 quote.email_requires_items`. Un correo con un PDF de tabla vacía es un error de la persona, no algo que enseñar a un cliente.
- **R4. Sin datos fiscales no hay correo.** Como en la spec 025, `400 quote.company_profile_incomplete` (el mismo código: es la misma causa y el mismo aviso en la pantalla). No se manda un PDF «a medias» ni un correo sin saber de quién es.
- **R5. El destinatario es uno, y se valida.** `recipient` es obligatorio, un email válido (el mismo tipo `Email` del resto de la aplicación), de **un máximo de 254 caracteres**, **sin caracteres de control ni saltos de línea** (cabeceras: R12) y **una sola dirección** (nada de listas ni de «Nombre <a@b>»). Se **recortan los espacios de los extremos** antes de usarla (el resto se valida tal cual: lo que contenga un espacio, una coma, un ángulo o un carácter de control se rechaza). Un valor no válido es `400` con el error de campo de siempre, **antes** de leer nada de la base de datos.
- **R6. La dirección propuesta sale del cliente, la decide quien envía** (D2). La API **no** la rellena por su cuenta: el cuerpo manda. (El frontend precarga la de la ficha del cliente.) Así no hay un caso especial «cliente sin email» en el servidor, y la dirección usada para este envío **no se guarda en ningún sitio** (D4).
- **R7. Orden de comprobaciones:** sesión (`401`) → permiso (`403`) → **límite por persona** (`429`; solo gasta cupo quien tiene el permiso) → forma del cuerpo (`400`, **antes** de leer nada) → correo configurado (`400 quote.email_disabled`) → presupuesto (`404`) → líneas (`400`) → perfil (`400`) → generar el PDF → **enviar** → anotar. Nada de lo anterior toca el servidor de correo, y el cupo se gasta **después** del permiso (un `Employee` o un `Technician` recibe su `403` sin gastar uno).
- **R8. Se envía antes de anotar** (ADR-020 C1) y la anotación no puede estropear un envío ya hecho: si el servidor aceptó el mensaje, la respuesta es `200` **aunque guardar la fecha falle** (un conflicto de concurrencia, o un borrador eliminado entre medias). **No se reintenta** la anotación: el presupuesto se lee **después** de enviar y se guarda de inmediato, así que solo un cambio simultáneo en esos milisegundos la haría chocar, y el correo ya salió; el evento `1410` lleva un booleano que dice si se anotó. Lo contrario —un `409` tras un correo que ya salió— llevaría a reenviar el mismo presupuesto dos veces.
- **R9. Un envío fallido no anota nada.** Si el servidor de correo no acepta el mensaje, la respuesta es **`502 quote.email_not_sent`**, sin el motivo (la categoría va al evento de seguridad, no a la respuesta), y el presupuesto queda como estaba. 502 y no 503 por la misma razón que en las invitaciones (ADR-020): la API funciona, falla un servicio del que depende.
- **R10. Sin correo configurado no hay botón que valga.** Con `Email:Provider = None` la operación responde `400 quote.email_disabled` (D8: no hay alternativa útil, a diferencia de la invitación, que devolvía el token). El frontend lo traduce a un aviso, no a un error genérico.

**El correo**

- **R11. Plantilla fija, nada que escriba la persona** (D9). El **asunto** es «Presupuesto P-0012» (el mismo formato de la pantalla: solo el número). El **cuerpo** es texto plano con una frase fija, el nombre fiscal de la empresa (de la base de datos, **en una sola línea**: sin saltos de línea ni caracteres de control), el número, el total y la fecha de validez si la tiene, y la indicación de que el PDF va adjunto. **Ni el título, ni las notas, ni el nombre del cliente, ni nada de lo que teclea quien envía** entra en el asunto o el cuerpo. (El PDF sí lleva esos textos —es el documento—; el correo, no.)
- **R12. Cabeceras a salvo** (ADR-020 R7). El destinatario y el asunto con caracteres de control se rechazan antes de tocar la red. El nombre del remitente es el de la configuración; **el de la empresa no se usa como nombre del remitente** (D10: cualquier empresa podría llamarse «Banco X» y salir con la dirección de OfiFlow).
- **R13. Dirección de respuesta = el email del perfil fiscal de la empresa**, si lo tiene (D10). El cliente que responde escribe a la **empresa**, no a OfiFlow. Si el perfil no tiene email, el correo no lleva dirección de respuesta y el cuerpo lo dice («Para cualquier duda, contacta con {empresa}»; sin teléfono: el cuerpo no repite datos que ya están en el PDF).
- **R14. Un adjunto, controlado.** El mensaje admite como máximo **un** adjunto, con un tipo de una **lista cerrada** (`application/pdf`), un nombre que cumple `^[A-Za-z0-9._-]{1,100}$` (aquí, `presupuesto-P-0012.pdf`, el de la spec 025 D11) y un tamaño máximo (**5 MB**; un PDF de 100 líneas pesa del orden de 100 KB). El puerto **rechaza** lo que no cumpla antes de tocar la red, no confía en quien lo llama.
- **R15. El PDF es el mismo que se descarga.** Las dos operaciones llaman al **mismo** constructor del documento (un servicio de Application, no dos copias del código): lo que el cliente recibe es lo que la persona ve al descargar. Los importes no se recalculan (R5 de la spec 025).

**Quién puede y cuánto**

- **R16. Permiso: `Quotes.Issue`** (D7). Mandar un presupuesto a un cliente es **comprometer un precio ante un tercero**, que es exactamente lo que `Quotes.Issue` ya protege (enviar, aceptar y rechazar, spec 023 D11): `Owner`, `Admin` y `Manager` pueden; `Employee` (que prepara y descarga el PDF, pero no compromete) y `Technician`, no (`403`). No hay permiso nuevo: la matriz sigue en 20 permisos y 100 celdas. Una **invariante comprobada por un test**: todo rol con `Quotes.Issue` tiene también `Quotes.Read` y `Company.Read` (el PDF enseña los datos de la empresa).
- **R17. La puerta del segundo factor se aplica sola** (ADR-024): un `Owner` o `Admin` sin segundo factor recibe `403 mfa.enrollment_required`. Y un token de plataforma no sirve.
- **R18. Límite de envíos por persona: 20 por hora** (D11), como las invitaciones (ADR-020 F1): enviar correo a terceros es la vía clásica de abuso, y la clave es la persona, que solo se conoce una vez autenticada. Un `429` con `Retry-After` dice cuánto falta. Además, la política `quote-email` de **30 por minuto y por IP** frena un bucle de peticiones antes de autenticar (el cubo propio, como `quote-pdf`).

**Qué queda anotado**

- **R19. El último envío, en el presupuesto** (D4): `EmailedAt` (instante UTC) y `EmailedByUserId`. **No** se guarda la dirección, ni el correo, ni el PDF. Se rellena con una operación de Domain (`RecordEmailSent`) permitida en **cualquier estado** (no es un cambio del contenido: no sube `Revision`, no toca las líneas y un presupuesto enviado sigue siendo inmutable). El `QuoteDto` gana `emailedAt` (la persona lo ve en la pantalla); `EmailedByUserId` queda guardado para auditoría y **no** se expone.
- **R20. Eventos de seguridad** (ADR-009): `1410 QuoteEmailSent` y `1411 QuoteEmailFailed` (con la **categoría** del fallo), con **solo Ids** (empresa, persona, presupuesto). **Nunca** la dirección, el asunto, el cuerpo, el nombre del fichero, el texto del servidor ni el contenido del PDF. Es la traza que el PDF de la spec 025 no tenía (D10 de aquella spec).

## Modelo conceptual

```
Una columna nueva en Quote (una migración):
- EmailedAt         datetime2 null   instante UTC del último envío aceptado por el servidor de correo
- EmailedByUserId   uniqueidentifier null   quién lo mandó (auditoría; no sale por la API)

Application
- EmailMessage                        gana: EmailAttachment? Attachment y Email? ReplyTo
- EmailAttachment                     (FileName, ContentType, Content): valida su nombre, su tipo y su tamaño al construirse
- QuotePdfBuilder                     (servicio) lee presupuesto + cliente + perfil y devuelve el PDF; lo usan GetQuotePdf y SendQuoteEmail
- SendQuoteEmailCommand (Quotes.Issue) valida, construye, envía, anota; devuelve { emailedAt }
- QuoteEmail                          plantilla fija (función pura): asunto y cuerpo
- Errors: quote.email_disabled (400), quote.email_requires_items (400), quote.email_not_sent (502)

Infrastructure
- SmtpEmailSender                     construye multipart/mixed (texto + adjunto) y Reply-To

Api
- POST /api/v1/quotes/{id}/email      200 { emailedAt } (Quotes.Issue), política quote-email (IP) + QuoteEmailThrottle (persona)
```

## Criterios de aceptación

- Dado un presupuesto con líneas de una empresa con perfil fiscal y correo configurado, cuando alguien con `Quotes.Issue` lo manda a `cliente@ejemplo.es`, entonces `200` con `emailedAt`, y el servidor SMTP de pruebas recibe **un** mensaje: `To` = esa dirección, asunto «Presupuesto P-0012», cuerpo en texto plano con el nombre de la empresa, el número y el total, y **un adjunto** `presupuesto-P-0012.pdf` de tipo `application/pdf` que **empieza por `%PDF-`** y contiene el mismo texto que la descarga de la spec 025.
- Dado el mismo caso, entonces el presupuesto **no cambia de estado** y el siguiente `GET` trae `emailedAt` relleno; un segundo envío lo **actualiza**.
- Dado un presupuesto en **borrador**, entonces el correo sale y el PDF adjunto lleva «BORRADOR» en cada página.
- Dados los **15 casos fijos de totales** de la spec 023, entonces el total del cuerpo y los importes del PDF adjunto son **exactamente** los de la API.
- Dado un título, unas notas y un nombre de cliente con `\r\nBcc: x@y.z`, HTML, saltos de línea y caracteres de control, entonces **ninguno** aparece en el asunto ni en el cuerpo, el mensaje no tiene cabeceras extra y el correo sale.
- Dada una empresa cuyo **nombre fiscal** lleva saltos de línea o caracteres de control (no debería, pero la base de datos manda), entonces el cuerpo lo muestra **en una sola línea** y sin ellos, y el nombre **no** se usa como remitente.
- Dado un perfil **con** email, entonces el correo lleva `Reply-To` con esa dirección; **sin** email, no lleva `Reply-To` y el cuerpo remite a la empresa por su nombre.
- Dado un destinatario vacío, sin `@`, con dos direcciones, con espacios o saltos de línea, con «Nombre <a@b.c>», de más de 254 caracteres o con caracteres de control, entonces `400` de campo y **no** se toca el servidor de correo.
- Dado un presupuesto **sin líneas**, entonces `400 quote.email_requires_items`; dada una empresa **sin perfil fiscal**, `400 quote.company_profile_incomplete`; y tras guardar el perfil, el mismo presupuesto se manda.
- Dado `Email:Provider = None`, entonces `400 quote.email_disabled` y no se genera ningún PDF.
- Dado un servidor de correo que **no contesta**, **rechaza el mensaje**, **no acepta las credenciales** o **está caído**, entonces `502 quote.email_not_sent` (sin el motivo ni la dirección), **`emailedAt` no cambia**, y queda un evento `1411` con la categoría.
- Dado un envío **aceptado** pero cuya anotación falla por concurrencia, entonces `200` igualmente y, tras el reintento, `emailedAt` relleno.
- Dado un `Employee` o un `Technician`, entonces `403`; dado un `Manager`, `Admin` u `Owner`, `200`; dada una petición **sin sesión**, `401`; dado un `Owner` o `Admin` **sin** segundo factor, `403 mfa.enrollment_required`; dado un token de plataforma, `401` o `403` como en el resto de rutas.
- Dada **otra empresa**, entonces mandar un presupuesto ajeno da `404` (el mismo que uno inexistente), **no se envía nada** y el correo de cada empresa lleva **solo sus datos fiscales**.
- Dadas **21 peticiones en una hora de la misma persona**, entonces la 21 recibe `429` con `Retry-After` y no se envía; y **31 peticiones en un minuto desde una IP** también dan `429`.
- Dada **cualquier** ejecución, entonces **ni los logs ni los eventos ni las respuestas** contienen la dirección del destinatario, el asunto, el cuerpo, el nombre del fichero ni el PDF (un test lo comprueba con direcciones y textos «marcados»).
- Dado el **puerto de correo**: un adjunto con un tipo que no sea `application/pdf`, un nombre con `/`, `..`, comillas o saltos de línea, o de más de 5 MB se **rechaza** antes de tocar la red; un mensaje sin adjunto (invitaciones, recuperar contraseña) **se construye exactamente igual que hoy**.
- Frontend: el botón «Enviar por correo» aparece con `Quotes.Issue`; abre un diálogo con la dirección del cliente precargada (vacía si no la tiene) y editable; mientras envía el botón se desactiva (no hay doble clic); con éxito se ve «Enviado» y «Enviado por correo el…» en el presupuesto; cada error tiene su aviso: datos de la empresa incompletos (con el enlace a «Datos de la empresa»), sin líneas, correo no configurado, fallo del servidor de correo («inténtalo de nuevo; si sigue fallando, descarga el PDF y envíalo tú») y demasiados envíos (con el tiempo de espera).

Caso multi-tenant obligatorio: el presupuesto, el cliente y el perfil salen **solo de la empresa del token**; ninguna empresa puede mandar el presupuesto de otra ni recibir en su correo los datos fiscales de otra.

## Seguridad (ADR-009)

- **Datos personales:** **sí, y salen de la plataforma hacia un tercero.** El correo y el PDF juntan el NIF y la dirección de la empresa (para un autónomo, sus datos personales) con el nombre y la dirección del cliente y los precios, y la **dirección de correo del cliente** es un dato personal de un tercero que no ha firmado nada con OfiFlow. Por eso: solo `Quotes.Issue` lo manda; **la dirección no se guarda, no se registra y no sale en ninguna respuesta de error** (D4); el asunto y el cuerpo llevan **el mínimo** (R11); el cliente se minimiza en el PDF igual que en la spec 025 (nombre y dirección, nunca notas, email ni teléfono). **Base legal:** quien envía es la empresa (responsable); OfiFlow actúa como **encargado del tratamiento** al entregar el correo que la empresa decide mandar a su cliente, a petición de este y para dar un precio (relación precontractual, no comunicación comercial). Los textos legales y el contrato de encargado con el proveedor de correo siguen **pendientes de la persona dueña del proyecto** (abierto en la ADR-020 y en la ADR-028).
- **Autorización:** `Quotes.Issue` comprobado en Application antes de leer nada (R7, R16). Sin permiso nuevo (D7). Invariante comprobada por test.
- **Entrada:** **un campo de texto**, `recipient`: máximo 254 caracteres (constante de Domain, la misma de `Email`), sin caracteres de control, una sola dirección, validado en el Validator **antes** de tocar la base de datos o la red (R5). El `id` es un `Guid`. Todo el texto del correo y del PDF sale de la base de datos y se trata como no fiable (R11; el PDF, R7 de la spec 025).
- **Aislamiento:** una columna nueva en una entidad que ya es `ITenantOwned`; ninguna consulta nueva usa `IgnoreQueryFilters()` (la lista blanca del test de arquitectura **no** cambia).
- **SQL crudo:** ninguno.
- **Abuso:** **sí, es la operación más abusable hasta ahora**: manda un correo a una dirección que **elige quien envía** y con un adjunto cuyo texto (título, descripciones, notas) también lo elige quien envía, **desde el dominio y con la reputación de OfiFlow**. Límites: 20 por hora y por persona (R18), 30 por minuto y por IP (R18), correo solo a **una** dirección por envío, plantilla fija sin texto libre (R11), PDF **inerte** (sin enlaces ni acciones: R9 de la spec 025), nombre del remitente **no** controlable por la empresa (R12), segundo factor obligatorio para `Owner` y `Admin` (R17) y rol `Quotes.Issue` (no cualquiera). Ver el modelo de amenazas.
- **Eventos de seguridad:** `1410` y `1411` (R20), solo Ids y categoría.
- **Modelo de amenazas (lo importante):**
  - **Mejora:** el documento que recibe el cliente sale de los datos del servidor, no de lo que alguien adjunta a mano (R15), y el correo no puede inyectar cabeceras ni escribir texto libre a un tercero (R11, R12), que es lo que más se abusa del correo de una plataforma.
  - **Riesgo aceptado (el principal): *phishing* o *spam* desde el dominio de OfiFlow.** Una cuenta con `Quotes.Issue` (robada o maliciosa) puede mandar un correo con un PDF cuyo **texto es libre** (notas de hasta 2000 caracteres, descripciones) a **cualquier** dirección, hasta 20 por hora. Se mitiga, no se elimina: cuerpo y asunto fijos, PDF sin enlaces activos, límite por persona y por IP, segundo factor, y rastro (`1410`/`1411` con persona y presupuesto) que permite **suspender la empresa** (spec 011). Lo que lo cerraría del todo es **limitar el envío a la dirección guardada en la ficha del cliente** (la opción descartada en D2) o un límite por empresa y día; queda como la primera cosa que endurecer si se ve abuso. El **límite por empresa y día** se deja fuera por ahora (D11).
  - **Riesgo aceptado:** **reputación de envío compartida.** Todas las empresas mandan con el remitente de OfiFlow: una que provoque quejas daña la entrega de las demás. Cuando haya volumen, remitente por empresa (con su dominio, SPF/DKIM) o un proveedor que gestione la reputación.
  - **Riesgo aceptado:** **el correo no es una prueba de entrega.** `200` significa «el servidor de correo aceptó el mensaje», no «el cliente lo recibió ni lo leyó» (sin rebotes ni confirmaciones: LATER). Queda `emailedAt`, no un acuse.
  - **Riesgo aceptado:** **el correo viaja sin cifrado de extremo a extremo** y el PDF no va protegido: quien tenga acceso al buzón del cliente tiene el presupuesto. Es el mismo supuesto de cualquier correo comercial; el tramo de OfiFlow al servidor de correo es siempre cifrado (ADR-020).
  - **Riesgo aceptado:** **el PDF enviado no se guarda** (D4 de la spec 025): si la empresa cambia sus datos fiscales después, la próxima descarga o reenvío saldrá con los nuevos, y no queda copia de lo que recibió el cliente. Para facturas no valdrá; llega con la fase 4.
  - **Riesgo aceptado:** **dos envíos seguidos llegan dos veces.** No hay idempotencia: es una acción de persona, con el botón desactivado mientras envía y el límite por persona; reenviar es legítimo (R2).

## Decisiones técnicas relevantes

- **D1. Una acción aparte, «Enviar por correo», independiente del cambio de estado.** *Confirmada con la persona dueña del proyecto.* Frente a acoplarlo a «Enviar» (Draft → Sent): con una acción aparte se puede **reenviar**, mandar un borrador para que el cliente lo vea antes de comprometerlo y, sobre todo, un fallo del correo no deja el estado a medias (no hay estado que deshacer). El coste es un clic más si se quieren las dos cosas.
- **D2. El destinatario es editable, precargado con el del cliente.** *Confirmada.* Cubre al cliente sin email guardado y al contacto de administración distinto del habitual. **Coste asumido:** la dirección la elige quien envía, que es la puerta al abuso descrita en el modelo de amenazas; se compensa con los límites y la plantilla fija, y la alternativa (solo el email de la ficha) queda como endurecimiento.
- **D3. Envío síncrono: la petición espera al servidor de correo.** *Confirmada.* Frente a la cola en memoria de la spec 018 (sin persistencia ni reintentos, pensada para un token que no puede guardarse): aquí quien pulsa necesita saber si salió, y un adjunto de cientos de KB no debe vivir en una cola que se pierde al reiniciar. Se acota con el tiempo máximo ya existente (10 s por defecto). Es el patrón de la invitación (ADR-020 B1).
- **D4. Se anota solo el último envío (`EmailedAt` y `EmailedByUserId`), sin la dirección.** *Confirmada.* Frente a un historial: guardar el email de un tercero exigiría retención, purga y permisos de lectura. Queda la traza de los eventos `1410`/`1411` para auditoría. Una migración nueva (la 025 no tenía).
- **D5. Un servicio compartido construye el PDF (`QuotePdfBuilder`).** *Recomendado.* Hoy lo hace `GetQuotePdfQueryHandler`; el envío necesitaría repetir la lectura del presupuesto, el cliente y el perfil. Un servicio en Application que ambos llamen evita dos copias que se desincronizarían (R15) y mantiene el orden de comprobaciones y los errores en un solo sitio.
- **D6. Se envía antes de anotar, y la anotación no puede deshacer el envío.** *Recomendado.* ADR-020 C1: sin compensación que escribir. Un conflicto al anotar **no se reintenta** (la capa de datos que ve Application no permite releer una entidad ya seguida, y la ventana es de milisegundos); el correo ya salió y la respuesta es `200` (R8), con el evento `1410` diciendo que no se anotó.
- **D7. Permiso `Quotes.Issue`, sin permiso nuevo.** *Recomendado.* Es la acción que compromete un precio ante un tercero (spec 023 D11): `Owner`, `Admin` y `Manager`. `Employee` prepara y descarga, pero no manda, igual que no puede «Enviar». Un permiso aparte (`Quotes.Email`) duplicaría la matriz sin proteger nada más. Un test fija la invariante «`Quotes.Issue` ⇒ `Quotes.Read` y `Company.Read`».
- **D8. Sin correo configurado: `400 quote.email_disabled`.** *Recomendado.* La invitación podía degradarse devolviendo el token; aquí no hay equivalente útil. Es un error de **configuración del despliegue**, no de la persona: el mensaje lo dice, y el aviso de arranque de `Production` con `Provider = None` ya existe (ADR-020).
- **D9. Plantilla fija: ni mensaje propio ni texto de la persona en el correo.** *Recomendado.* La regla cerrada de ADR-020 («nada que escriba quien invita entra en el correo») se mantiene: un campo «mensaje» convertiría a OfiFlow en un enviador de correo libre desde su dominio. El texto libre del presupuesto va **dentro del PDF**, que es un documento, no en el cuerpo.
- **D10. Remitente fijo y `Reply-To` de la empresa.** *Recomendado.* La plataforma solo puede firmar (SPF/DKIM) con **su** dominio: un correo «de» la empresa desde OfiFlow se marcaría como suplantación. El nombre del remitente es el de la configuración (**no** el de la empresa: cualquiera se llamaría «Banco X»), y el cliente que responde escribe a la empresa gracias a `Reply-To`.
- **D11. Límites: 20 por hora y por persona + 30 por minuto y por IP; sin tope por empresa y día.** *Recomendado, valores iniciales.* El primero repite el de las invitaciones (ADR-020 F1) y vale para el abuso lento; el segundo, el patrón de `quote-pdf`, frena el bucle antes de autenticar. **Un tope por empresa y día** (en memoria, por instancia, como los demás) queda fuera hasta ver uso real. Se revisan con datos.
- **D12. El puerto admite un adjunto controlado, no adjuntos en general.** *Recomendado.* Se rompe la regla «sin adjuntos» de la ADR-020 **solo** en la forma mínima: **un** adjunto, tipo de una lista cerrada (`application/pdf`), nombre restringido y tamaño máximo, validados **en el puerto** y no por quien lo llama. Los usos existentes (invitación, recuperar contraseña) no cambian: sin adjunto, el mensaje sale **byte a byte igual** que hoy (un test lo fija).
- **D13. Mismo orden y mismos errores que el PDF.** *Recomendado.* `404` antes que `400`, y los códigos nuevos (`quote.email_disabled`, `quote.email_requires_items`, `quote.email_not_sent`) siguen la convención de ADR-011: los de negocio, `400`; el de un servicio externo, `502`. Pasan de 67 a **70 códigos**; los eventos, de 54 a **56**.
- **D14. La respuesta no repite la dirección.** *Recomendado.* `200 { emailedAt }` y nada más: ni el destinatario ni el nombre del fichero. Menos datos personales en una respuesta que puede acabar en una herramienta de depuración del navegador.
- **D15. Orden de trabajo:** el puerto con adjunto y el correo (**bloque 1**), la operación y la API (**2**), el frontend (**3**) y la documentación (**4**). *Recomendado.* El bloque 1 no cambia ningún uso existente y se prueba solo con el servidor SMTP de pruebas; los bloques 1 y 2 se entregan juntos y **el backend puede fusionarse solo**.
- **D16. Nivel de riesgo de las pruebas: alto.** *Recomendado.* Es el primer correo a un tercero, el primero con adjunto y el que más abusable es: pruebas de cada regla, del aislamiento, de las cabeceras con entrada hostil, de que ningún dato llegue a un registro, **mutaciones a mano** de cada bloque y una **comprobación en vivo** contra un servidor SMTP real (el de pruebas del proyecto) leyendo el mensaje recibido.
- **¿Requiere ADR nueva? Sí: ADR-029**, «Presupuesto por correo: el primer correo a un tercero y el primer adjunto», con las decisiones D1 a D4, D9, D10 y D12, el modelo de amenazas del abuso del dominio, y anotaciones «Ampliada por» en ADR-020 (el adjunto controlado y las dos reglas que se enmiendan), ADR-028 (el PDF ya se manda), ADR-026 y ADR-012 (la invariante de `Quotes.Issue`).

## Fuera de alcance / Backlog relacionado

- Mensaje propio opcional, `Cc`/`Bcc`, varios destinatarios, HTML con marca, logotipo, plantillas y remitente por empresa; el historial de envíos; la cola persistida con reintentos; rebotes, quejas y confirmación de lectura; un enlace para que el cliente acepte el presupuesto; un tope por empresa y día.
- **Pendientes de la persona dueña del proyecto** (no son código): SMTP con **dominio propio** y SPF/DKIM/DMARC antes de mandar a clientes reales, el contrato de encargado del tratamiento con el proveedor, y los textos legales (que cubran este envío).

---

## Validación antes de aprobar

> ¿Esta funcionalidad ayuda a conseguir o mantener al primer cliente?

Sí, directamente: **un presupuesto que hay que descargar, abrir otro programa y adjuntar a mano se manda tarde o no se manda**, y el primer autónomo que pruebe el producto lo pedirá en cuanto vea el PDF. Es también donde más riesgo concreto hay de todo lo hecho hasta ahora (un correo a un tercero desde el dominio de la plataforma, con un adjunto de texto libre): por eso se hace pequeña (una sola operación, una sola dirección, plantilla fija, sin historial ni mensaje propio) y con el nivel de pruebas **alto**.
