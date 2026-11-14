# ADR-028 — PDF del presupuesto: generado al pedirlo, con PDFsharp, y un documento que sale solo de datos del servidor

**Estado:** Aceptado (2026-11-11). Las decisiones D1–D13 de la [spec 025](../specs/025-pdf-presupuesto/spec.md) se aprobaron tal como estaban escritas (D1, D2, D3 y D4 confirmadas expresamente con la persona dueña del proyecto: **solo el PDF descargable**, **PDFsharp + MigraDoc**, **todos los estados, con marca «BORRADOR» en el borrador** y **generado al pedirlo, sin guardarlo**); los cambios que se hicieron al implementar están en [«Lo que cambió al implementar»](#lo-que-cambió-al-implementar).
**Fecha:** 2026-11-11
**Proyecto:** OfiFlow

---

## Contexto

Un presupuesto (spec 023) vivía solo en la pantalla: para dárselo a un cliente había que copiar los importes a otro documento, y ahí se cuelan los errores de tecleo y se pierde la trazabilidad. La spec 024 guardó los **datos fiscales de la empresa** precisamente para esto: el PDF es su razón de ser. Esta spec lo cierra: **descargar un presupuesto como PDF, a nombre de la empresa y con sus datos fiscales**. No manda nada por correo (spec 026) ni guarda ningún fichero.

Es el primer fichero que **sale de la plataforma** con tres cosas delicadas juntas: los **datos fiscales** de la empresa (para un autónomo, su NIF y su dirección personales), el **nombre y la dirección de un cliente** y **precios**. Y se genera a partir de **texto libre** que escribió una persona. Eso decide casi todo lo que sigue.

## Problema

- ¿Con **qué librería** se genera, y qué licencia tiene? OfiFlow es un repositorio **público**, y una licencia que obligue a publicar el código de lo que se enlace con ella no es un detalle.
- ¿Qué presupuestos tienen PDF: **solo los enviados**, o también un borrador (para revisarlo antes de enviarlo)?
- ¿Se **guarda** el PDF o se genera **cada vez**? ¿Qué pasa si la empresa cambia después sus datos fiscales?
- ¿Cómo se garantiza que el PDF **no puede discrepar** de lo que ve la pantalla (el dinero se calculó una vez, en el servidor)?
- ¿Cómo se trata el **texto libre** (títulos, descripciones, notas, nombres) para que no rompa el documento, no reordene lo que lo rodea y no se salga de la página?
- ¿Quién puede descargarlo: un **permiso nuevo** o el que ya lee presupuestos? ¿Y qué ve en él?
- ¿Qué pasa si la **empresa no tiene sus datos fiscales**?
- ¿Cómo se evita que una sesión robada **agote el servidor** pidiendo PDF sin parar?
- ¿Cómo se **prueba** un fichero binario: su contenido, que sea inerte y que no se salga de la página?

## Opciones consideradas

### A. La librería

**A1. PDFsharp + MigraDoc 6.2.4 (licencia MIT), fijada.** *(elegida; D2)* Código abierto sin restricciones por facturación, pura gestión de documentos (sin navegador ni proceso externo) y con una versión sin dependencias de Windows (la que se usa). El diseño de una tabla es más manual que en otras. **Hallazgo (2026-11-10): las versiones 6.5.0 y 7.0.0-preview-2, publicadas el 14-10-2026, cambiaron la licencia a AGPL-3.0**, que obligaría a publicar el código de todo lo que se enlace con ellas. Por eso se **fija la 6.2.4**, se pide a Dependabot que **no proponga** las posteriores a la 6.2 y un test lee el `.nuspec` de lo restaurado.
**A2. QuestPDF.** API muy cómoda, pero su licencia gratuita lo es solo por debajo de cierta facturación anual y **no es de código abierto**: habría que documentar una condición que hoy no tenemos por qué asumir. Se descarta.
**A3. HTML a PDF con un navegador sin cabeza.** Diseño con HTML/CSS, pero arrastra **un navegador en el servidor**: una imagen enorme, más superficie de ataque (peticiones a recursos externos desde un documento con texto de usuario) y peor encaje con el pipeline de seguridad. Se descarta.

### B. Qué presupuestos tienen PDF

**B1. Todos; el borrador sale con la marca «BORRADOR» en cada página.** *(elegida; D3)* Quien prepara un presupuesto puede ver cómo quedará **antes** de enviarlo. Uno enviado, aceptado o rechazado sale sin marca: el PDF es la oferta, no su estado interno.
**B2. Solo los enviados, aceptados y rechazados.** Más simple, pero no se puede previsualizar. Se descarta.

### C. Guardar o generar

**C1. Se genera al pedirlo; no se guarda nada.** *(elegida; D4)* Sin almacenamiento de ficheros, sin migración y sin política de retención. Un presupuesto enviado no cambia (spec 023), así que el PDF sale igual cada vez **salvo por los datos fiscales de la empresa**, que son los de ahora (riesgo aceptado).
**C2. Se guarda una copia al enviar.** Congela el PDF con los datos de ese momento, pero exige almacenamiento de ficheros y decidir cuánto tiempo se conservan. Es lo que **sí** pedirá la factura (fase 4), que debe conservarse tal como se emitió. Se descarta por ahora.

### D. Dónde vive el código

**D1. Un puerto en Application (`IQuotePdfRenderer`) y la maquetación en Infrastructure.** *(elegida; D5)* El puerto recibe un modelo **ya calculado** y devuelve los bytes; no toca la base de datos ni recalcula nada. La librería no sale de Infrastructure (un escaneo lo vigila). Es el patrón de `IEmailSender` ([ADR-020](ADR-020-correo-saliente.md)).
**D2. Maquetar dentro del caso de uso.** Mezcla la regla (qué se imprime) con la librería. Se descarta.

### E. El texto libre

**E1. Imprimirlo tal cual como texto, saneado antes.** *(elegida)* Sin HTML, sin marcas ni enlaces que interpretar. Se quitan los caracteres de control y los que **invierten el sentido de lectura** (U+202E y compañía); solo se imprime lo que la fuente incluida cubre y lo demás sale como «?»; las palabras que no caben se **parten donde dejan de caber**.
**E2. Admitir marcas de formato (negritas, enlaces).** Una puerta de entrada nueva a cambio de comodidad. Se descarta.

### F. Permiso

**F1. Sin permiso nuevo: `Quotes.Read`.** *(elegida; D6)* Descargar el PDF no enseña nada que esos roles no vean ya en pantalla. Lo que sí se fija con un test es la **invariante «todo rol con `Quotes.Read` tiene `Company.Read`»**: el PDF enseña los datos de la empresa, y si un rol perdiera `Company.Read` y conservara `Quotes.Read`, el PDF se los enseñaría.
**F2. Un permiso aparte (`Quotes.Export`).** Duplicaría la matriz sin proteger nada más. Se descarta.

### G. Sin datos fiscales

**G1. `400 quote.company_profile_incomplete`.** *(elegida; D7)* Un error de **negocio**, no de campo (no hay ningún campo que corregir) y con el mismo estado que el resto de errores de dominio. **Después** del `404`: a quien pregunta por un presupuesto que no es suyo no se le dice nada de los datos de la empresa.
**G2. Generar un PDF «a medias» con el emisor en blanco.** No sirve para dárselo a un cliente. Se descarta.

## Decisión

### 1. El documento

- **Una operación:** `GET /api/v1/quotes/{id}/pdf` (`Quotes.Read`). La empresa sale **solo del token**; el presupuesto de otra empresa es el mismo `404` que uno inexistente.
- **El modelo** (`QuotePdfModel`: emisor, documento, cliente, líneas, desglose y totales) se construye con `QuotePdfModelFactory` **sin recalcular nada**: copia los importes del `QuoteDto` que ya calculó el servidor (spec 023), así que el PDF **no puede discrepar** de la pantalla. Un escaneo prohíbe a la fábrica y al generador sumar, multiplicar, redondear o usar `QuoteCalculator`. El cliente aporta **solo su nombre y su dirección**: nunca sus notas (internas), su email ni su teléfono (minimización; el tipo tiene exactamente dos propiedades).
- **El generador** (`PdfSharpQuotePdfRenderer`, MigraDoc para la maquetación y PDFsharp para la marca): cabecera con el emisor y los datos del documento, el cliente, el título, la tabla de líneas (**la cabecera se repite en cada página**), el desglose de IVA y los totales **en una tabla que no se parte** entre dos páginas, las notas del presupuesto, un pie «P-0012 · Página X de Y» y, en un borrador, la marca «BORRADOR» a 45°, bajo el contenido y con transparencia, **en cada página**.
- **Formato español fijo e independiente del servidor** (`MoneyFormat`, `SpanishFormat`): importes `1.234,56 €` **desde céntimos enteros y solo con aritmética entera** (ningún `double`, `float` ni `decimal` toca el dinero; en un contenedor sin datos de cultura, `es-ES` ni siquiera existe), cantidades con hasta tres decimales sin ceros sobrantes y fechas `dd/MM/yyyy`. El espacio antes del € y del `%` es **no separable**. La fecha del documento es la de **creación en hora de Madrid** (D12; en UTC, un presupuesto creado a las 00:30 saldría con el día anterior), con 16 casos fijos en los bordes de medianoche y los dos cambios de hora.
- **La fuente va dentro del ensamblado** (D8): Liberation Sans 2.1.5 (SIL OFL), regular y negrita, con su licencia, sus autores y sus huellas SHA-256 al lado. Un servidor Linux no trae fuentes del sistema que se puedan dar por supuestas, y depender de las de la máquina haría que el mismo presupuesto saliera distinto en cada servidor. Un test comprueba las huellas y que la fuente tiene un glifo para **cada** carácter que el saneado deja pasar.

### 2. El texto libre (`PdfText`)

- **Se imprime como texto, sin interpretar nada.** Los caracteres de control y de formato (incluidos los que invierten el sentido de lectura) se quitan; los caracteres que la fuente no tiene (un emoji, el árabe, el chino) salen como «?» y **nunca rompen la generación**; los saltos de línea se respetan donde tienen sentido (las notas, la dirección: dos seguidos como máximo) y son un espacio en el resto.
- **Las palabras largas se parten por ancho medido, no por número de letras.** `BreakLongWords` mide la anchura real de cada letra con la fuente y el cuerpo con que se va a dibujar, e inserta un espacio donde la palabra deja de caber (a la vista es un salto de línea). Una «W» ocupa el doble que una «i»: un tope de caracteres o dejaba pasar una hilera de «W» fuera de la página o partía palabras normales.

### 3. Quién, cuánto y qué se registra

- **Permiso `Quotes.Read`** comprobado en Application **antes** de leer nada: sin él, `403` sin mirar si el presupuesto existe ni si hay perfil. Orden de comprobaciones: sesión (`401`) → permiso (`403`) → presupuesto (`404`) → perfil (`400`) → generación. La matriz sigue en **20 permisos y 100 celdas**; la tabla de operaciones pasa a **58** y los endpoints de presupuestos, de 8 a **9** (los tests que fijan esas cifras fallaron a propósito).
- **La puerta del segundo factor** ([ADR-024](ADR-024-segundo-factor-obligatorio.md)) se aplica sola: la operación no está en la lista cerrada (sigue siendo **exactamente cuatro**), así que un `Owner` o `Admin` sin factor recibe `403 mfa.enrollment_required`.
- **Un límite de peticiones propio** (D9): política `quote-pdf`, **30 por minuto y por IP**, con su cubo (leer el propio presupuesto desde una IP agotada sigue funcionando). Generar un PDF cuesta CPU y memoria; el límite frena a una sesión robada usada para agotarlas.
- **Cabeceras:** `Content-Type: application/pdf`, `Cache-Control: no-store` **antes** de enviar la consulta (también los errores salen sin guardarse en cachés), `Content-Disposition: attachment` con el nombre **`presupuesto-P-0012.pdf`: solo el número**, nunca el título ni el cliente (D11: son texto libre y datos personales, y no entran en una cabecera), y `nosniff` (ya lo pone el middleware).
- **Ningún evento nuevo** (D10): una descarga es una lectura y `GetQuote` y `GetQuotes` tampoco registran. **Ninguna parte del documento llega a un registro:** ni el modelo, ni la consulta, ni el generador, ni su formato escriben en él (un escaneo lo vigila) y todos los tipos sobrescriben `ToString`. Sigue habiendo **54 eventos**.
- **Un código de error nuevo**, `quote.company_profile_incomplete` (ya hay **67**).
- **El PDF es inerte:** el generador no usa ninguna API que cree enlaces, acciones, adjuntos, formularios o JavaScript, no toca el sistema de ficheros ni la red, y un test recorre **todos los objetos** del documento buscando 19 claves peligrosas (con un control positivo: un PDF con un enlace sí las tiene). Los metadatos dicen solo «OfiFlow» como creador y el título; el productor es el de la librería.

### 4. La comprobación de arranque

`QuotePdfStartupCheck`: **el servidor no arranca** si le falta la zona horaria `Europe/Madrid` (en una imagen mínima de Linux hay que instalar `tzdata`), las fuentes o si el generador no puede producir un documento pequeño con datos de mentira. Es mejor que arrancar y responder `500` a cada descarga sin que nadie sepa por qué. Recibe el generador registrado en DI: prueba el de verdad, no una copia.

### 5. El frontend

Un botón **«Descargar PDF»** en el presupuesto guardado, para quien tiene `Quotes.Read`: se deshabilita con cambios sin guardar (el PDF es el guardado), mientras otra acción trabaja y mientras se genera. Un borrador avisa de que su PDF lleva la marca. Si faltan los datos fiscales se ve un **aviso** (con el enlace a «Datos de la empresa» para quien tiene `Company.Manage` y «pídele a un administrador» para el resto), tanto al abrir el presupuesto como si la API contesta `quote.company_profile_incomplete`. El fichero se entrega al navegador por un enlace temporal y **se libera su URL**; no se guarda en ningún almacenamiento. El nombre de la cabecera solo se usa si es **exactamente** `presupuesto-P-NNNN.pdf`; si no, se construye el mismo con el número.

## Consecuencias

### Positivas

- **El PDF no puede discrepar de la pantalla:** imprime lo que calculó el servidor, y los **15 casos fijos de totales** pasan por `Quote` → `QuoteDto` → modelo → PDF y por HTTP y se comparan, con un formato del importe escrito aparte en los tests.
- **Una licencia comprobada y vigilada,** en un repositorio público: un cambio a una licencia que obliga a publicar el código habría llegado a la rama principal en el siguiente PR semanal de Dependabot sin que nadie lo notara.
- **El texto de una persona no puede romper, reordenar ni desbordar el documento,** y **no se interpreta nunca**.
- **No hay almacenamiento que proteger:** ni ficheros, ni migración, ni retención.
- **El backend puede desplegarse solo:** una operación nueva, sin tocar nada existente.
- **Un servidor mal preparado se oye al arrancar,** no en cada descarga.

### Negativas / riesgos aceptados

- **Quien tenga `Quotes.Read` puede descargar todos los presupuestos de su empresa** (con precios y datos de clientes) **sin que quede traza**; lo frena solo el límite de 30 por minuto. Es el mismo acceso que ya tiene leyéndolos en pantalla; si importa, se añade un evento (D10) en una spec pequeña.
- **El PDF no queda guardado:** si la empresa cambia después su nombre fiscal o su NIF, el PDF de un presupuesto **ya enviado** saldrá con los datos nuevos (D4). Para presupuestos es tolerable; para facturas no, y por eso la copia congelada llega con la fase 4.
- **Un PDF no es un documento firmado:** cualquiera puede alterar el fichero una vez descargado.
- **El generador vive en el proceso de la API:** un fallo de la librería con una entrada rara es un `500` (con su código genérico, sin datos). Lo cubren las pruebas de texto hostil, no un aislamiento en otro proceso.
- **La pantalla de una descarga no se ve en el sistema de pruebas automáticas:** no hay rasterizador de PDF en el entorno, así que el **aspecto** de las páginas se revisó a ojo (cuatro documentos, en el navegador integrado); lo que se automatiza es el **texto** leído de vuelta, la estructura y las cajas de cada letra.
- **`dependabot.yml` ignora `PDFsharp` y `PDFsharp-MigraDoc` a partir de la 6.3 con la sintaxis `">= 6.3"` de su documentación, pero no se puede probar en local:** se verá en el primer PR semanal. Si no funcionara, el test de licencia seguiría fallando al actualizar.
- **Los caracteres que la fuente no tiene salen como «?»:** un cliente con el nombre en árabe o chino lo verá así. La plataforma es solo para España.
- **Sin logotipo,** sin plantillas, solo en español y con una sola fuente (queda para después).
- **La fecha del documento necesita la base de datos de zonas horarias** en el servidor; sin ella el arranque falla (a propósito).

### Lo que cambió al implementar

1. **El productor del PDF es la propia librería** («PDFsharp 6.2.4»): PDFsharp lo escribe siempre y, si se le da otro, lo deja como «(Original: …)». La spec decía «OfiFlow» como productor; ahora el creador es «OfiFlow» y el productor, el de la librería (no es un dato de la máquina).
2. **Las palabras largas se parten por ancho medido, no por número de letras** (ver arriba): el primer intento con un tope de caracteres dejó una hilera de «W» fuera de la página, y lo destapó un test que mide la caja de cada letra.
3. **Una comprobación de arranque que no estaba en el plan** (`QuotePdfStartupCheck`), por el riesgo de despliegue de la D12. Una mutación destapó que creaba su propio generador y que ningún test demostraba que el arranque genera un documento; ahora recibe el generador por inyección y tres tests lo prueban.
4. **El CORS no exponía `Content-Disposition`:** un frontend en otro origen no podía leer el nombre del fichero. Se expone junto con `Retry-After` y un test fija que son **exactamente esas dos** ([ADR-017](ADR-017-cors-para-el-frontend-web.md) anotada).
5. **El error de una petición que espera un fichero llega como un `Blob`** con el JSON dentro, así que el **código estable era invisible para el interceptor del frontend**: una descarga de una empresa suspendida no cerraba la sesión, un `403` de rol no volvía a pedir la cuenta y un `403 mfa.enrollment_required` no llevaba a la pantalla de seguridad. Ahora el interceptor lee el `Blob` y el error sigue su camino con el cuerpo ya como objeto.
6. **`CompanyProfileDto.From(profile)`** sustituyó al mapeo que estaba dentro de la consulta del perfil: lo usan la consulta del perfil y la del PDF.
7. **`QuotePdfFile`** (el resultado con el nombre y los bytes) vive en el espacio de nombres del documento, así que hereda el escaneo de `ToString`.
8. **Los tests que cuentan operaciones, endpoints y manejadores de presupuestos fallaron a propósito** (57 → 58, 8 → 9 y 8 → 9) y se actualizaron.

### Hallazgos de la implementación (útiles para el tribunal)

- **Mirar la licencia de lo que se actualiza:** una librería puede cambiar de MIT a AGPL entre dos versiones menores. Se comprobó en el `.nuspec` de cada versión, se fijó la última MIT y se vigila con un test **y** con Dependabot.
- **Un PDF se prueba leyéndolo de vuelta, nunca comparando bytes** (llevan identificadores y fechas): el texto de cada página con PdfPig, la estructura con el propio PDFsharp y la **caja de cada letra** para comprobar que nada se sale de los márgenes.
- **Un test «inerte» necesita su control positivo:** recorrer todos los objetos buscando claves peligrosas pasa siempre si el PDF no tiene ninguna; el control es un PDF con un enlace, que sí las tiene.
- **El formato del dinero se prueba contra otra implementación independiente** (en los tests de la API, escrita con aritmética distinta): dos copias que tienen que coincidir.
- **Las mutaciones destapan los tests flojos:** un DTO «incoherente» cuyo total era justo neto + IVA (111 + 222 = 333) dejó sobrevivir una mutación que recalculaba el total; el borde 0x80–0x9F del conjunto imprimible no tenía test; y la comprobación de arranque no probaba lo que decía probar.
- **Un identificador de una cabecera que viene de fuera no se usa tal cual:** el nombre del fichero del frontend solo vale si coincide con una expresión **cerrada** (16 nombres hostiles: rutas, extensiones, saltos de línea).
- **Mis propias comprobaciones también se equivocan:** la comprobación en el navegador falló la primera vez por un **error de mi guion de arranque** (Git Bash convierte en ruta de Windows un secreto base64 que empieza por «/»), no del código; la comprobación en vivo, por pasar un rol como texto cuando la API lo espera como número.
- **En el navegador:** un `Employee` ve el aviso «pídele a un administrador…» sin enlace; al pulsar el botón la API responde `400` y la pantalla se queda con el aviso, sin error suelto; con el perfil guardado, el botón dice «Generando…» y entrega un `application/pdf` con el nombre `presupuesto-P-0001.pdf`; un `Technician` llega a «Acceso denegado» sin botón.

### Cifras

- **Backend:** Domain 616, Application 1290, Infrastructure 1080 y Api 1106 = **4092 casos** (+340 respecto a la spec 024), todo en verde. **158 de 161 mutaciones detectadas** (118 de 121 del documento y 40 de 40 de la API; las tres que sobreviven son equivalentes: dar formato a un entero positivo no depende de la cultura —dos veces— y PDFsharp ignora el segundo registro de su resolvedor de fuentes). **Comprobado en vivo** con la API real como proceso, SQL Server de Docker, Mailpit y TOTP calculado: **42/42** (111 ms por un PDF de dos líneas, 172 ms por uno de cien).
- **Frontend:** **896 specs** en verde (+47), **54 de 54 mutaciones** detectadas y comprobado en el navegador integrado contra la API real.

### Recomendación de despliegue (no es código)

1. **Ninguna migración:** esta spec no toca la base de datos.
2. **Instalar los datos de zonas horarias** en la imagen del servidor (en Linux, el paquete `tzdata`): sin ellos, la API **no arranca** (a propósito).
3. **El backend puede desplegarse solo:** añade una operación y un límite. El frontend va después (sin él el PDF existe pero nadie lo pide).
4. **Los permisos no cambian:** quien ya lee presupuestos puede descargarlos.
5. **Detrás de un proxy inverso** el límite por IP necesita `UseForwardedHeaders` con proxies de confianza (decisión pendiente de la [ADR-009](ADR-009-seguridad-base.md)); si no, todos los clientes compartirían la IP del proxy y los 30 por minuto serían de **todos**.
6. **Probar de punta a punta con una cuenta real** antes de abrir a clientes: descargar un borrador y uno enviado, abrirlos en un lector de PDF, comprobar la marca y que un `Technician` recibe `403`.
7. **Revisar el primer PR semanal de Dependabot:** no debe proponer `PDFsharp` ni `PDFsharp-MigraDoc` 6.3 o posterior.

### Qué queda cerrado (no se debe reabrir sin una razón nueva y explícita)

- **El PDF sale de datos del servidor y los importes no se recalculan** para imprimirlos.
- **El texto libre se imprime como texto, nunca interpretado,** y el documento es inerte.
- **Se genera al pedirlo y no se guarda** (hasta que la factura exija conservarlo).
- **Sin permiso nuevo:** `Quotes.Read`, con la invariante de que todo rol que lo tiene ve los datos de la empresa.
- **El nombre del fichero sale solo del número.**
- **PDFsharp + MigraDoc en la última versión MIT** y la fuente dentro del ensamblado.
- **Sin datos fiscales, `400` y no un PDF a medias.**
- **La descarga tiene su límite de peticiones.**

### Qué queda abierto para revisar más adelante

- **El envío por correo con el PDF adjunto** (spec 026), que reutilizará el mismo generador.
- **El logotipo** (imágenes: almacenamiento, tamaño y seguridad del fichero), plantillas, otros idiomas, el PDF de la **factura** y su copia congelada (fase 4), firma digital y un QR de aceptación.
- **Un evento de descarga,** si un analista lo pide.
- **Una decisión sobre la licencia:** si algún día se quiere una versión posterior de PDFsharp (AGPL), habría que decidirlo en una ADR (publicar el código bajo AGPL o cambiar de librería).
- **Un aviso en el listado de presupuestos** cuando faltan los datos de la empresa (hoy solo en el detalle).
- **Un rasterizador de PDF en el pipeline** para automatizar la revisión del aspecto.

---

## Relación con otros ADR

- Amplía: **[ADR-026](ADR-026-presupuestos.md)** (el PDF del presupuesto, que se anunciaba), **[ADR-010](ADR-010-pipeline-devsecops.md)** (una regla nueva de dependencias: la **licencia** se comprueba en el `.nuspec` y una actualización que la cambie se bloquea), **[ADR-017](ADR-017-cors-para-el-frontend-web.md)** (se expone también `Content-Disposition`), **[ADR-011](ADR-011-errores-codigos-diccionario.md)** (un código nuevo; ya hay 67).
- Relacionada con: **[ADR-027](ADR-027-perfil-fiscal-empresa.md)** (los datos fiscales que imprime), **[ADR-012](ADR-012-autorizacion-rbac.md)** (sin permiso nuevo y la invariante entre `Quotes.Read` y `Company.Read`), **[ADR-009](ADR-009-seguridad-base.md)** (datos personales: nada del documento llega a un registro; y el límite de peticiones), **[ADR-024](ADR-024-segundo-factor-obligatorio.md)** (la puerta se aplica sola), **[ADR-020](ADR-020-correo-saliente.md)** (el patrón de puerto + implementación).
- Prepara: el correo del presupuesto con el PDF adjunto (spec 026) y la factura (fase 4), que reutilizará el generador y exigirá conservar el documento tal como se emitió.
