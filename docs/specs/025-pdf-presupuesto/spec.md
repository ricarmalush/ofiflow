# Spec — 025-pdf-presupuesto

**Estado:** Implementada (2026-11-11). Aprobada tal como estaba escrita el 2026-11-10: las cuatro decisiones de forma (alcance, librería, estados y no guardarlo) estaban **confirmadas con la persona dueña del proyecto** (D1, D2, D3 y D4) y el resto (D5–D13) se aprobaron con la spec. Lo que cambió al implementar, las cifras y los hallazgos están en la [ADR-028](../../adr/ADR-028-pdf-presupuesto.md)
**Fecha:** 2026-11-10 (implementada el 2026-11-11)
**Bounded Context:** Quotes (con lectura de Tenancy para los datos fiscales de la empresa)
**Depende de:** spec 023 y ADR-026 (los presupuestos y su cálculo del dinero), spec 024 y ADR-027 (el perfil fiscal de la empresa: **esta spec es su razón de ser**), ADR-012 (permisos), ADR-009 (datos personales y registro), ADR-010 (dependencias y licencias), ADR-011 (códigos de error), ADR-024 (la puerta del segundo factor: se aplica sola)

> **Numeración:** 025 es la siguiente spec; su ADR será la **028** (la 027 es la del perfil fiscal). El correo con el PDF adjunto es la spec 026.
> **Base:** la rama parte de `spec/024-company-profile` (el PR #66 del backend sigue abierto): el PDF lee el `CompanyProfile`, que solo existe ahí.

---

## Objetivo

Que una empresa pueda **descargar un presupuesto como PDF**, a su nombre y con sus datos fiscales, listo para enseñárselo o mandárselo a un cliente por el medio que sea.

Hasta ahora un presupuesto vive solo en la pantalla (spec 023): para dárselo al cliente hay que copiar los importes a otro documento, y ahí es donde se cuelan los errores de tecleo y se pierde la trazabilidad. Con esta spec el documento sale **del mismo dato que ve el servidor** (los importes los calcula él, nunca se reescriben) y con los datos de quien lo emite (spec 024). Es el último paso antes de mandarlo por correo (spec 026). Esta spec **no manda nada ni guarda ningún fichero**: genera el PDF cuando se pide.

## Alcance — Qué SÍ incluye

Cuatro bloques:

1. **El documento (backend):** el modelo de lo que lleva el PDF, su maquetación con PDFsharp y MigraDoc, la fuente incluida en el repositorio, el formato español de importes y fechas, y la marca «BORRADOR». Probado leyendo el PDF de vuelta.
2. **La API:** `GET /api/v1/quotes/{id}/pdf`, el error de datos de la empresa incompletos, un límite de peticiones propio y los tests de extremo a extremo (roles, aislamiento entre empresas, contenido).
3. **Frontend:** un botón «Descargar PDF» en el presupuesto, el aviso cuando faltan los datos de la empresa (con el enlace a «Datos de la empresa») y la descarga con la sesión.
4. **Documentación:** ADR-028, el manual y la spec cerrada.

## Alcance — Qué NO incluye

- **NEXT (spec 026):** mandar el presupuesto por correo con el PDF adjunto. Reutilizará el mismo generador.
- **LATER:** el **logotipo** (exige guardar imágenes: otra decisión de almacenamiento y de seguridad del fichero); varios diseños o plantillas; idiomas distintos del español; el PDF de la factura (fase 4); firmar el PDF digitalmente; marca de agua personalizada; QR de aceptación.
- **LATER:** guardar una copia del PDF al enviar (D4). Se hará, si hace falta, cuando existan las facturas, que sí deben conservarse tal como se emitieron.
- **NEVER:** un PDF que **no salga de los datos del servidor** (que la petición traiga importes, textos o la empresa); que **otra empresa** pueda descargar el PDF de la primera; que el PDF ejecute nada al abrirse (scripts, acciones, enlaces automáticos).
- **No cambia:** el modelo del presupuesto ni su cálculo, los estados y sus transiciones, el perfil fiscal, la base de datos (no hay migración) ni los permisos (D6).

## Reglas de negocio / invariantes

**El documento**

- **R1. Una sola operación: descargar el PDF de un presupuesto.** `GET /api/v1/quotes/{id}/pdf` devuelve `200` con `application/pdf`. La empresa sale **solo del token**; el presupuesto de otra empresa es el mismo `404` que uno inexistente (filtro global, R12 de la spec 023).
- **R2. Todos los estados tienen PDF** (D3). Un **borrador** lleva la marca «BORRADOR» en diagonal en **cada página**; uno enviado, aceptado o rechazado, no lleva ninguna marca (el PDF es la oferta, no su estado interno).
- **R3. Sin datos fiscales no hay PDF.** Si la empresa no ha guardado su perfil fiscal (spec 024), la respuesta es `400 quote.company_profile_incomplete` (un error de **negocio**, no de campo; el mismo estado que `quote.cannot_send`). No se genera un PDF «a medias» con la empresa en blanco.
- **R4. Qué lleva.** Todo sale de datos guardados en el servidor:
  - **Quien emite:** nombre fiscal, NIF, dirección completa (calle, código postal, ciudad y provincia si hay), y teléfono y email si los hay.
  - **El documento:** «Presupuesto P-0012» (el mismo formato que la pantalla), título, **fecha** (D12) y «Válido hasta» si lo tiene.
  - **El cliente:** su **nombre** y su **dirección** si la tiene. **Nunca** sus notas (son internas), y por minimizar, tampoco su email ni su teléfono.
  - **Las líneas** en su orden: descripción, cantidad, precio unitario, IVA % e importe neto.
  - **Los totales:** el desglose de IVA por tipo (base e IVA de cada tipo), la base total, el IVA total y el **total**.
  - **Las notas del presupuesto** (condiciones y observaciones, que ve el cliente) si las tiene.
- **R5. Los importes no se recalculan.** El generador **imprime** lo que ya calculó el servidor (`QuoteDto`: neto por línea, desglose por tipo y totales); no suma, no multiplica ni redondea. Así el PDF **no puede discrepar** de la pantalla. Los importes se dan formato **desde céntimos enteros**, sin ningún tipo de coma flotante.
- **R6. Formato español fijo.** Importes `1.234,56 €`, cantidades con hasta tres decimales sin ceros sobrantes (`2,5`, `10`), fechas `dd/MM/yyyy`; **independiente de la cultura del servidor**.
- **R7. El texto libre es texto.** Títulos, descripciones, notas y nombres se imprimen **tal cual**, sin interpretar nada: no hay HTML, ni marcas de formato, ni enlaces. Los saltos de línea de las notas y de la dirección se respetan (dos seguidos como máximo) y en el resto de campos son un espacio; los caracteres de control y los que invierten el sentido de lectura (como U+202E) se quitan; solo se imprime lo que la fuente cubre (ASCII, Latin-1 y Latin extendido A, el euro y la puntuación tipográfica habitual) y un carácter que no tiene (un emoji, el árabe) **no rompe la generación**: sale «?». Una palabra larga sin espacios se parte con un espacio **donde deja de caber** (se mide el ancho real de cada letra con la fuente, no se cuentan caracteres: una «W» ocupa el doble que una «i»), así que nada se sale de la página.
- **R8. Varias páginas.** Con muchas líneas (hasta 100) o notas largas el documento pasa de página; la cabecera de la tabla de líneas se repite en cada una y los totales no se parten en dos páginas.
- **R9. Un PDF inerte.** El documento **no contiene** scripts, acciones al abrir, enlaces, adjuntos ni formularios. Sus metadatos dicen solo «OfiFlow» como creador y «Presupuesto P-0012» como título; el productor es el de la librería («PDFsharp 6.2.4», que la propia librería escribe siempre). Ni el nombre de usuario, ni la máquina, ni rutas.
- **R10. Mismo dato, mismo documento.** Dos peticiones seguidas del mismo presupuesto dan **el mismo contenido** (mismas páginas, mismo texto). Un presupuesto enviado no cambia (spec 023); lo único que puede cambiar el PDF es que la empresa edite después sus datos fiscales (D4).

**Quién puede y cuánto**

- **R11. Permiso: `Quotes.Read`** (D6). `Owner`, `Admin`, `Manager` y `Employee` pueden; `Technician`, no (`403`). No hay permiso nuevo: la matriz sigue en 20 permisos y 100 celdas. Una **invariante comprobada por un test**: todo rol con `Quotes.Read` tiene también `Company.Read` (el PDF enseña los datos de la empresa, que esos roles ya pueden ver).
- **R12. La autorización va antes que todo**: sin `Quotes.Read` se responde `403` sin mirar si el presupuesto existe ni si hay perfil. Orden de comprobaciones: sesión (`401`) → permiso (`403`) → presupuesto (`404`) → perfil (`400`) → generación.
- **R13. La puerta del segundo factor se aplica sola** (ADR-024): la operación no está en la lista cerrada, así que un `Owner` o `Admin` sin segundo factor recibe `403 mfa.enrollment_required`.
- **R14. Un límite de peticiones propio** (D9): 30 por minuto y por IP, con su cubo (`quote-pdf`) para no gastar el de otras rutas. Generar un PDF cuesta CPU y memoria; el límite frena a una sesión robada usada para agotarlas.

**La respuesta**

- **R15. Cabeceras.** `Content-Type: application/pdf`, `Cache-Control: no-store` (los precios no se guardan en cachés), `Content-Disposition: attachment; filename="presupuesto-P-0012.pdf"` (D11: solo el número, **nunca** texto del usuario) y `X-Content-Type-Options: nosniff` (ya la pone el middleware de seguridad).

## Modelo conceptual

```
Sin entidades nuevas ni migración.

Application
- IQuotePdfRenderer                  el puerto: byte[] Render(QuotePdfModel)
- QuotePdfModel                      lo que se imprime: emisor, documento, cliente, líneas, desglose y totales (todo ya calculado)
- GetQuotePdfQuery (Quotes.Read)     lee el presupuesto, el cliente y el perfil; construye el modelo; llama al puerto
- Error: quote.company_profile_incomplete

Infrastructure
- PdfSharpQuotePdfRenderer           PDFsharp 6.2.4 + MigraDoc (MIT); fuente OFL embebida; marca BORRADOR
- MoneyFormat / DateFormat           formato es-ES fijo desde céntimos

Api
- GET /api/v1/quotes/{id}/pdf        200 application/pdf (Quotes.Read), política de límite quote-pdf
```

## Criterios de aceptación

- Dado un presupuesto **enviado** de una empresa con perfil fiscal, cuando alguien con `Quotes.Read` pide su PDF, entonces `200`, `application/pdf`, empieza por `%PDF-` y **contiene**: el nombre fiscal y el NIF de la empresa, «P-0012», el nombre del cliente, cada descripción, el total y el desglose de IVA, **y ninguna marca «BORRADOR»**.
- Dado un presupuesto en **borrador**, entonces el PDF lleva «BORRADOR» en cada una de sus páginas.
- Dados los **15 casos fijos de totales** de la spec 023 (`QuoteTotalsVectors`), entonces los importes impresos son **exactamente** los de la API, en formato español.
- Dada una empresa **sin perfil fiscal**, entonces `400 quote.company_profile_incomplete` y no se genera nada; y tras guardar el perfil, el mismo presupuesto da `200`.
- Dado un presupuesto con **100 líneas**, entonces el PDF tiene varias páginas, las 100 descripciones aparecen en orden y los totales aparecen **una sola vez**.
- Dado texto libre con `<script>`, `&amp;`, `%`, `\`, saltos de línea, tabuladores, un emoji, escritura de derecha a izquierda y una palabra de 500 letras sin espacios, entonces el PDF **se genera**, el texto sale tal cual (sin interpretarse) y nada se sale de la página.
- Dado cualquier PDF generado, entonces **no contiene** `/JavaScript`, `/JS`, `/OpenAction`, `/AA`, `/Launch`, `/URI`, `/EmbeddedFile` ni `/AcroForm`, y sus metadatos son solo los de R9.
- Dado un cliente con **notas internas**, entonces esas notas **no aparecen** en el PDF; ni su email ni su teléfono.
- Dado un `Technician`, entonces `403`; dado un `Manager` o `Employee`, entonces `200`; dada una petición **sin sesión**, `401`; dado un `Owner` o `Admin` **sin** segundo factor, `403 mfa.enrollment_required`.
- Dada **otra empresa**, entonces el PDF de un presupuesto ajeno da `404` (el mismo que uno inexistente) y el PDF de cada empresa lleva **solo sus datos fiscales**.
- Dadas **31 peticiones en un minuto desde una IP**, entonces la 31 recibe `429` con `Retry-After`.
- Dadas dos peticiones seguidas del mismo presupuesto enviado, entonces el contenido es el mismo.
- Dada la respuesta, entonces lleva `Cache-Control: no-store` y un nombre de fichero con **solo** el número (aunque el título o el cliente tengan comillas, saltos de línea o `../`).
- Frontend: el botón «Descargar PDF» aparece con `Quotes.Read`; tras pulsarlo se descarga el fichero **con la sesión** (nunca con un token en la URL); si faltan los datos de la empresa se ve el aviso con el enlace a «Datos de la empresa» (o «pídele a un administrador que los complete» a quien no puede gestionarlos).

Caso multi-tenant obligatorio: el presupuesto, el cliente y el perfil salen **solo de la empresa del token**; ninguna empresa puede descargar el PDF de otra ni ver sus datos fiscales en el suyo.

## Seguridad (ADR-009)

- **Datos personales:** **sí, y se concentran en un fichero que sale de la plataforma.** El PDF junta el NIF y la dirección de la empresa (para un autónomo, sus datos personales) con el nombre y la dirección del cliente y los precios. Por eso: solo `Quotes.Read` lo descarga, **nada de su contenido llega a un registro** (ni el texto, ni los importes, ni los datos fiscales; el `ToString()` de la consulta y del modelo **no imprimen su contenido**), el cliente se minimiza (nombre y dirección, nunca notas, email ni teléfono) y el fichero se descarga con `no-store`.
- **Autorización:** `Quotes.Read` comprobado en Application antes de leer nada (R12). Sin permiso nuevo (D6).
- **Entrada:** la operación **no tiene cuerpo ni parámetros** salvo el `id` de la ruta (un `Guid`); no hay ninguna entrada de texto que validar. Todo el texto del documento viene de la base de datos y ya está acotado (spec 023 y 024); aun así se trata como no fiable (R7).
- **Aislamiento:** ninguna entidad nueva. Presupuesto, cliente y perfil se leen con el filtro global; ninguna consulta nueva usa `IgnoreQueryFilters()` (la lista blanca del test de arquitectura **no** cambia).
- **SQL crudo:** ninguno.
- **Abuso:** **sí, es un endpoint costoso** (CPU y memoria al maquetar hasta 100 líneas): límite propio de 30 por minuto y por IP (R14).
- **Superficie del PDF:** el documento se **genera**, no se recibe ni se parsea: no hay entrada de ficheros. El PDF es inerte (R9). La librería se queda en la versión que tiene licencia MIT (D2) y las actualizaciones que cambiarían la licencia se bloquean en Dependabot.
- **Eventos de seguridad:** ninguno nuevo (D10). Una descarga es una lectura y las demás lecturas de presupuestos tampoco se registran.
- **Modelo de amenazas (lo importante):**
  - **Mejora:** el documento sale de los datos del servidor (R5), no de lo que alguien reescribe a mano, y el texto libre no puede inyectar nada en él (R7, R9).
  - **Riesgo aceptado:** quien tenga `Quotes.Read` puede **descargar todos los presupuestos de su empresa** (con precios y datos de clientes) sin que quede traza; lo frena solo el límite de 30 por minuto. Es el mismo acceso que ya tiene leyéndolos en pantalla; si importa, se añade un evento (D10).
  - **Riesgo aceptado:** el PDF **no queda guardado**: si la empresa cambia después su nombre fiscal o su NIF, el PDF de un presupuesto ya enviado saldrá con los datos nuevos (D4). Para presupuestos es tolerable; para facturas no, y por eso la copia congelada llega con la fase 4.
  - **Riesgo aceptado:** un PDF no es un documento firmado: cualquiera puede alterar el fichero una vez descargado.
  - **Riesgo aceptado:** el generador se carga en el proceso de la API; un fallo de la librería con una entrada rara es un `500` (con su código genérico, sin datos). Lo cubren las pruebas de texto hostil (R7), no un aislamiento en otro proceso.

## Decisiones técnicas relevantes

- **D1. Alcance: solo el PDF descargable, sin correo ni copia guardada.** *Confirmada con la persona dueña del proyecto* (por la spec 024). El correo con adjunto es la spec 026.
- **D2. Librería: PDFsharp + MigraDoc 6.2.4 (licencia MIT).** *Confirmada con la persona dueña del proyecto.* Frente a QuestPDF (licencia no abierta, gratis solo por debajo de cierta facturación) y a un navegador sin cabeza (una imagen enorme y una superficie de ataque nueva). **Hallazgo (2026-11-10): las versiones 6.5.0 y 7.0.0-preview-2, publicadas el 14-10-2026, cambiaron la licencia a AGPL-3.0**, que obligaría a publicar el código de todo lo que se enlace con ellas. Se **fija 6.2.4** (MIT, de enero de 2026) y se pide a Dependabot que **no proponga** actualizaciones a partir de la 6.3 (`ignore` en `dependabot.yml`). Un test fija que las versiones usadas tienen licencia MIT.
- **D3. Todos los estados tienen PDF; el borrador, con marca «BORRADOR».** *Confirmada con la persona dueña del proyecto.* Quien prepara un presupuesto puede ver cómo quedará antes de enviarlo.
- **D4. El PDF se genera al pedirlo; no se guarda.** *Confirmada con la persona dueña del proyecto.* No hay almacenamiento de ficheros, ni migración, ni política de retención. Un presupuesto enviado no cambia, así que el PDF sale igual cada vez salvo por los datos fiscales de la empresa (riesgo aceptado).
- **D5. Un puerto en Application y la maquetación en Infrastructure.** *Recomendado.* `IQuotePdfRenderer` recibe un `QuotePdfModel` ya calculado y devuelve los bytes; no toca la base de datos ni recalcula nada, y se sustituye por un doble en los tests de Application (el patrón de `IEmailSender`, ADR-020). La librería no sale de Infrastructure y Application no la conoce.
- **D6. Sin permiso nuevo: `Quotes.Read`.** *Recomendado.* Descargar el PDF no enseña nada que esos roles no vean ya en pantalla; un permiso aparte duplicaría la matriz sin proteger nada más. Lo que sí se fija con un test es la invariante «quien tiene `Quotes.Read` tiene `Company.Read`» (R11).
- **D7. Perfil incompleto: `400 quote.company_profile_incomplete`.** *Recomendado.* Es un error de negocio con el mismo estado que `quote.cannot_send` (convención del proyecto: los errores de dominio son `400` con su código); la pantalla lo traduce a un aviso con el enlace a los datos de la empresa.
- **D8. Una fuente con licencia libre incluida en el repositorio.** *Recomendado.* En un servidor Linux no hay fuentes del sistema y PDFsharp no trae una útil: se incluye una fuente con licencia OFL (**Liberation Sans 2.1.5**, regular y negrita; con el euro, las tildes y la eñe) como recurso embebido y se registra con un `IFontResolver`. Su licencia, sus autores y las huellas SHA-256 de los dos ficheros se guardan junto a ella, y un test comprueba que son exactamente los descargados y que cubren **cada** carácter que el saneado deja pasar. El PDF incrusta solo los caracteres que usa.
- **D9. Límite de 30 peticiones por minuto y por IP.** *Recomendado, valor inicial.* Las políticas del proyecto son por IP (la de la plataforma, también autenticada, es de 30 por minuto). Se revisa con datos reales.
- **D10. Sin evento de seguridad.** *Recomendado.* Una descarga es una lectura; `GetQuote` y `GetQuotes` tampoco registran. Queda anotado como riesgo aceptado, y es una spec pequeña si un analista lo pide.
- **D11. El nombre del fichero lleva solo el número** (`presupuesto-P-0012.pdf`). *Recomendado.* El título y el cliente son texto libre (comillas, saltos de línea, `../`) y datos personales: ninguno entra en una cabecera.
- **D12. La fecha del documento es la de creación del presupuesto, en hora de Madrid.** *Recomendado.* La plataforma es solo para España (D6 de la spec 024); en UTC un presupuesto creado a las 00:30 saldría con el día anterior. Se calcula con la zona `Europe/Madrid` y un test fija los días de cambio de hora. *Riesgo de despliegue:* el servidor necesita la base de datos de zonas horarias (en una imagen mínima de Linux hay que instalarla); si no está, la generación falla, y por eso lo cubre un test de arranque.
- **D13. Orden de trabajo:** el documento y su dependencia (**bloque 1**), la API (**2**), el frontend (**3**) y la documentación (**4**). *Recomendado.* Los bloques 1 y 2 se entregan juntos y **el backend puede fusionarse solo** (una operación nueva, sin tocar nada existente).
- **¿Requiere ADR nueva? Sí: ADR-028**, «PDF del presupuesto: generado al pedirlo, con PDFsharp, y un documento que sale solo de datos del servidor», con las decisiones D2 a D5 y D8, el hallazgo de la licencia y anotaciones «Ampliada por» en ADR-026 (el PDF) y ADR-010 (la regla de licencias de las dependencias).

## Fuera de alcance / Backlog relacionado

- El correo con el PDF (spec 026); el logotipo; plantillas; la copia congelada del PDF al enviar (fase 4); un evento de descarga; firma digital.
- Un aviso en el listado de presupuestos (hoy solo en el detalle) cuando faltan los datos de la empresa.

---

## Validación antes de aprobar

> ¿Esta funcionalidad ayuda a conseguir o mantener al primer cliente?

Sí, directamente: **un presupuesto que no se puede dar en un papel con el nombre y el NIF de quien lo emite no sirve para presupuestar**, y el primer autónomo que pruebe el producto lo pedirá el segundo día. Es también donde más riesgo concreto hay si se hace a la ligera (importes que discrepen de la pantalla, texto libre en un documento que sale de la plataforma, una licencia que cambia a mitad de camino): por eso se hace pequeña (sin correo, sin ficheros guardados, sin logotipo) y con el nivel de pruebas **alto**.
