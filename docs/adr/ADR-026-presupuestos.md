# ADR-026 — Presupuestos: el dinero en céntimos enteros, el IVA calculado una vez por tipo, un presupuesto enviado inmutable y la concurrencia que lo vigila

**Estado:** Aceptado (2026-11-09). Las decisiones D1–D14 de la [spec 023](../specs/023-presupuestos/spec.md) se aprobaron tal como estaban escritas (D1, D2 y D3 confirmadas expresamente: **crear y gestionar sin PDF ni correo**, **del cliente con trabajo opcional** y **céntimos enteros con un IVA por línea**); los cambios que se hicieron al implementar están en [«Lo que cambió al implementar»](#lo-que-cambió-al-implementar).
**Fecha:** 2026-11-09
**Proyecto:** OfiFlow

---

## Contexto

Hasta ahora OfiFlow era una lista de clientes y trabajos. El flujo de valor del documento maestro es **cliente → trabajo → presupuesto → cita → factura → cobro**, y el presupuesto es el primer documento **con dinero** del producto: el momento en que una empresa de oficios gana o pierde un cliente. Hoy se hace fuera (una hoja de cálculo, Word) y se pierde la trazabilidad: qué se ofreció, a quién, cuándo y qué respondió.

Esta spec es **pequeña a propósito**: no genera el PDF ni manda el correo (las specs 025 y 026: la 024 se intercaló para guardar los datos de la empresa, que el PDF necesita; ver la [ADR-027](ADR-027-perfil-fiscal-empresa.md)). Lo que cierra es **el modelo del dinero**, que es lo que no se puede arreglar después sin migrar datos de clientes reales, y la regla que da valor a un presupuesto: **un precio ofrecido a un cliente no se reescribe a posteriori**. Y es la base de la factura (fase 4): el modelo de líneas, el IVA por tipo y el redondeo se deciden aquí una sola vez.

## Problema

- ¿Cómo se **representa el dinero** sin errores de redondeo ni de coma flotante, y quién lo calcula: la petición o el servidor?
- ¿Cómo se calcula el **IVA** de un presupuesto con líneas de tipos distintos, de forma que **coincida al céntimo con la factura futura**?
- ¿Qué **estados** tiene, y cómo se garantiza que uno enviado **no cambia**?
- ¿Cómo se asigna un **número correlativo por empresa** sin SQL a mano y sin que dos creaciones simultáneas lo repitan?
- ¿Cómo se evita que **dos personas pisen** el mismo borrador, o que una edición se cuele **después** de un envío?
- ¿Quién puede **preparar** un presupuesto y quién **comprometerlo**? ¿Y quién **no debe ver los precios**?
- ¿Cómo se prueba una regla del dinero que **se repite en el frontend** para una vista previa?

## Opciones consideradas

### A. El dinero y quién lo calcula

**A1. Céntimos enteros (`long`), y el servidor calcula todo.** *(elegida; D3 y R5)* Sin decimales de coma flotante; la petición **nunca** lleva un total, un neto ni un IVA (un campo de más se ignora), y **una sola función** del dominio (`QuoteCalculator`) fija los importes. Se guardan neto, IVA y total en la fila para listar sin cargar las líneas, y se recalculan en cada cambio.
**A2. Decimales (`decimal`) en la base de datos.** Más cómodo de leer, pero cada operación vuelve a decidir el redondeo y la comparación con la factura se complica. Se descarta.
**A3. Que el cliente mande el total y el servidor lo compruebe.** Dos fuentes de verdad y un campo que alguien manipulará. Se descarta.

### B. El IVA

**B1. Redondeado una vez por tipo, sobre la suma de las bases.** *(elegida; D4)* Es como lo desglosa una factura (base e IVA por tipo), así que el presupuesto y la factura futura **coincidirán al céntimo**. Redondeo *away from zero*. Una lista **cerrada** de tipos (0, 4, 10, 21; D10) en un único sitio.
**B2. Redondeado línea a línea.** Más simple, pero se desvía de la factura (cinco líneas de 0,02 € al 21 % dan 0,00 € de IVA por línea y 0,02 € por tipo). Se descarta.
**B3. Un IVA único por presupuesto.** No sirve cuando se mezclan materiales y mano de obra. Se descarta.

### C. Estados e inmutabilidad

**C1. `Draft → Sent → Accepted | Rejected`, y todo lo enviado inmutable.** *(elegida; D7)* Solo un borrador se modifica o se elimina; uno resuelto no se reabre. Corregir uno enviado es hacer uno nuevo (NEXT: duplicar). «Enviar» **solo anota**: no manda nada (D1).
**C2. Poder reabrir o editar uno enviado.** Convierte un compromiso de precio en un borrador cualquiera. Se descarta (NEVER).

### D. El número

**D1. `MAX + 1` por empresa, índice único `(TenantId, Number)` y reintento acotado (tres).** *(elegida; D5)* Sin SQL a mano (regla del proyecto) y sin una tabla de contadores. Una carrera la resuelve el índice y un reintento.
**D2. Un contador atómico por SQL.** Sin huecos y sin reintentos, pero exige SQL a mano. Se descarta aquí: una numeración fiscal sin huecos es asunto de la factura, no del presupuesto.

### E. Concurrencia

**E1. Control optimista con la versión de fila (`RowVersion`) más un contador `Revision`.** *(elegida; D9)* El presupuesto lleva su `version` al leerlo y el `PUT` la devuelve: si cambió mientras tanto, `400 quote.changed_meanwhile` y no se pisa nada. Y `Revision` sube con **cada** cambio, para que la fila del presupuesto se actualice siempre y la versión la vigile.
**E2. Sin control (gana la última escritura),** como `Job` (spec 020 D8). Aquí hay dinero comprometido: una edición no puede dejar un precio cambiado **después** de ofrecerlo. Se descarta.

### F. Permisos

**F1. Tres: `Quotes.Read`, `Quotes.Write` y `Quotes.Issue`.** *(elegida; D11)* Separar **preparar** (`Write`) de **comprometer** (`Issue`: enviar, aceptar, rechazar) sigue el patrón de `Jobs.Schedule`. `Employee` prepara; `Technician` no ve presupuestos.
**F2. Dos permisos** (con las transiciones dentro de `Write`): más simple, más permisivo. Se descarta.

## Decisión

### 1. El dominio

`Quote` es la raíz de su agregado y `QuoteItem` sus líneas (tabla `QuoteItems`, con su propio `TenantId` para que el filtro global las cubra también). Las relaciones con el cliente y el trabajo van por Id (ADR-006). **El cliente de un presupuesto no cambia**; el trabajo, opcional, **debe ser del mismo cliente** (`400 quote.job_customer_mismatch`).

- **Datos:** `Title` (hasta 200), `Notes` (hasta 2000, las ve el cliente), `ValidUntil` (una **fecha de calendario sin hora**, `DateOnly`, años 2000–2099, informativa: [ADR-022](ADR-022-agenda.md) obligó un desfase a los *instantes*, y una validez no es un instante).
- **Líneas:** de 0 a 100 (un borrador puede estar vacío; **enviarlo** exige al menos una), con descripción (500), cantidad (> 0, hasta 3 decimales y 99 999,999), precio en céntimos (0 a 9 999 999 999) y tipo de IVA. Con estas cotas ninguna suma desborda un `long` (100 líneas del máximo = 1,2·10^17 frente a 9,2·10^18).
- **El cálculo:** `neto de la línea = cantidad × precio` al céntimo *away from zero*; `base por tipo = suma de los netos`; `IVA del tipo = base × tipo / 100`, **una vez por tipo**; `neto = Σ bases`, `IVA = Σ IVA`, `total = neto + IVA`. Los importes guardados solo los fija `Quote.Recalculate`.
- **Estados:** `Draft → Sent` (enviar, con `SentAt`), `Sent → Accepted | Rejected` (con `DecidedAt`). Cada transición desde un estado que no es el suyo lanza su código (`quote.cannot_send`, `quote.cannot_accept`, `quote.cannot_reject`); modificar o eliminar un no borrador, `quote.not_editable` y `quote.cannot_delete`. Aceptar es una **anotación interna**, no la firma del cliente.
- **Las líneas se exponen como una vista de solo lectura:** ni un `cast` a `List` permite cambiar las de un presupuesto enviado.

### 2. La persistencia, el número y la concurrencia

- **Número:** al crear se lee el máximo de **esa** empresa (el filtro global limita la consulta) y se intenta `máximo + 1`; el índice único `(TenantId, Number)` rechaza a la segunda de dos creaciones simultáneas y esta **reintenta** hasta tres veces, sacando el intento fallido del seguimiento. Si no lo consigue, `400 quote.number_unavailable`. Un borrador eliminado puede dejar un hueco o liberar el último número (no ha salido de la empresa).
- **Versión:** `RowVersion` en la fila y `Revision` en el dominio. Sin `Revision`, una edición que deja las mismas líneas y la misma cabecera no toca ninguna columna de negocio, no hay `UPDATE` sobre el presupuesto y el control de concurrencia no la vigila: **una edición y un envío simultáneos podrían dejar un presupuesto enviado con las líneas cambiadas**. Con él, uno gana y el otro recibe `quote.changed_meanwhile`; un test contra SQL Server lo demuestra en los dos órdenes.
- **El conflicto es un `400` de negocio,** no un `409` nuevo: no se añade una traducción HTTP por una sola operación.
- **Migración `AddQuotes`:** las dos tablas, la FK en cascada de las líneas y cuatro índices (el único por número, por cliente, y por estado con la fecha descendente). Ninguna otra tabla cambia.
- **Una regla sobre lo existente:** no se puede eliminar un cliente con presupuestos, en **ningún** estado (`400 customer.has_quotes`; la de trabajos activos salta antes).

### 3. Quién, cuánto y qué se registra

- **Permisos:** `Quotes.Read`, `Quotes.Write` y `Quotes.Issue`. `Owner`, `Admin` y `Manager`, los tres; `Employee`, `Read` y `Write`; **`Technician`, ninguno** (los precios son información comercial). Ya hay **18 permisos** y la matriz pasa a **90 celdas**; la tabla de operaciones, a **55**.
- **La puerta del segundo factor** ([ADR-024](ADR-024-segundo-factor-obligatorio.md)) se aplica sola: ninguna operación de presupuestos está en su lista cerrada.
- **Sin eventos nuevos** (siguen siendo **53**): no es una operación de seguridad; las denegaciones ya las registra la autorización. **Nada de un presupuesto llega al registro** (título, notas, descripciones, importes): ningún caso de uso inyecta un registrador, los comandos con texto libre no imprimen su contenido y un escaneo vigila hasta la consola.
- **Códigos de error:** 14 nuevos (los once de la spec, **tres de campo** que pidieron las reglas de forma —`quote.vat_invalid`, `quote.quantity_invalid`, `quote.valid_until_invalid`— y `customer.has_quotes`); ya hay **64**.

### 4. Los endpoints

Ocho bajo `/api/v1/quotes` (crear, listar, leer, modificar, eliminar, enviar, aceptar y rechazar), siempre con sesión. Las lecturas llevan `Cache-Control: no-store`. El listado va **paginado** (hasta 100 por página, del más reciente al más antiguo, filtrable por estado y por cliente y sin las líneas). La empresa sale del token: un presupuesto, un cliente o un trabajo de otra empresa da el mismo `404` que uno inexistente.

### 5. El frontend

Un listado y una pantalla de un presupuesto, sin librerías nuevas. **Mientras se edita, el total es una vista previa** con la misma regla que el servidor (aritmética entera con `BigInt`, la cantidad en milésimas, IVA por tipo, medios céntimos *away from zero*) y **los mismos 15 casos fijos** que el backend (`QuoteTotalsVectors.json`, copiados como datos); **una vez guardado, lo que se enseña es lo que devuelve la API**. Solo un borrador se edita; enviar, aceptar y rechazar piden confirmación y `Quotes.Issue`; «Enviar» solo se habilita sin cambios sin guardar y con líneas (lo que se envía es lo guardado). Con una versión vieja se ve el conflicto **sin perder lo escrito** y se elige entre cargar lo guardado o guardar lo propio con la versión nueva (no si entre tanto ya se envió).

## Consecuencias

### Positivas

- **El precio ofrecido no se reescribe:** un presupuesto enviado es inmutable y una edición simultánea no se cuela. Hay trazabilidad de qué se ofreció y cuándo (`SentAt`).
- **El servidor es la única fuente del dinero:** un cliente manipulado no puede «rebajar» un total, y el cálculo coincide con una factura al céntimo (IVA por tipo).
- **Un cálculo, probado contra una tabla fija compartida:** si el frontend y el backend discrepan, un test lo dice.
- **Numeración sin SQL a mano ni contadores**, con la carrera probada de verdad.
- **Aislamiento y permisos sin sorpresas:** dos empresas no se ven ni se numeran entre sí; la matriz de 8 operaciones × 5 roles se prueba con efecto en cada celda.
- **El backend puede desplegarse solo:** operaciones nuevas y una regla que solo **restringe** el borrado de un cliente con presupuestos.

### Negativas / riesgos aceptados

- **Cualquier miembro con `Quotes.Issue` puede aceptar un presupuesto «en nombre» del cliente:** aceptar es una anotación interna, no una firma. Sin la aceptación del cliente (LATER) no hay valor contractual en el sistema, solo un registro.
- **Un `Employee` ve y fija precios en un borrador** (para prepararlo) pero no puede comprometerlo.
- **Muchas creaciones simultáneas en una misma empresa pueden agotar los tres intentos.** Con **diez a la vez**, ocho se crearon y dos recibieron `quote.number_unavailable` (sin ningún `500` ni número repetido). Una persona real no crea diez presupuestos a la vez; si algún día pasara (una importación), la solución es un contador, no más reintentos.
- **Un conflicto de versión es un `400`:** el cliente decide por el código `quote.changed_meanwhile`, nunca por el texto.
- **El cálculo existe en dos sitios.** Los 15 casos fijos los protegen, pero **la copia del frontend se mantiene a mano** (no hay un test que compare los dos ficheros entre repositorios): si cambia el cálculo, cambia en los dos y se regeneran los datos.
- **Una cifra por encima de 9·10^15 céntimos no se representa exactamente en JavaScript** (90 000 millones de euros); el servidor admite hasta 1,2·10^17. Muy fuera de lo realista, pero es un límite del cliente.
- **El listado sin filtros** ordena por fecha tras buscar por empresa: se midió el plan **con filtro por estado y por cliente** (usan su índice), no el del listado sin filtros, que con muchos presupuestos de una empresa ordenaría en memoria. Si hiciera falta, un índice por `(TenantId, CreatedAt)`.
- **Solo euros** y solo el IVA español: otra moneda o otro país es una migración y una ampliación de la lista cerrada.
- **Sin caducidad automática:** `ValidUntil` es informativa; la pantalla marca «Caducado» pero nada bloquea aceptar uno caducado.

### Lo que cambió al implementar

1. **Un borrador puede estar vacío** (0 a 100 líneas): la spec decía «de 1 a 100» y a la vez que enviar un borrador sin líneas da `quote.empty`; son incompatibles y se resolvió a favor de lo segundo.
2. **Tres códigos de campo** que la spec no listaba (IVA, cantidad y validez), porque un `Must` sin código da un texto en inglés de FluentValidation.
3. **`Revision`**, un entero que sube con cada cambio (ver la sección 2): lo descubrió pensar en «¿qué pasa si la edición no cambia nada?».
4. **`ValueGeneratedNever` en la clave de las líneas.** EF Core trata como «modificada» una entidad nueva con su `Guid` ya puesto cuando la descubre por la colección de su raíz, y el `UPDATE` fallaba al sustituir las líneas con un `DbUpdateConcurrencyException`. Lo encontró un test de Application con dobles (no el de SQL Server, que ya usaba la configuración real).
5. **Las líneas se exponen con `AsReadOnly()`**: la primera versión devolvía la propia lista y un `cast` la habría hecho modificable.
6. **El cliente de un presupuesto no cambia** (la spec no lo decía): un presupuesto a un cliente equivocado se elimina y se crea otro.
7. **Los tres números de la tabla de operaciones, permisos y celdas fallaron a propósito** (47 → 55, 15 → 18 y 75 → 90): son los tres tests que fijan la matriz.
8. **La tabla de líneas se llamaba `QuoteItem`** (sin `DbSet` propio) y se fijó `QuoteItems`.
9. **Hay 64 códigos de error** (50 antes) y los mismos 53 eventos, recontados contra el código.

### Hallazgos de la implementación (útiles para el tribunal)

- **Seis mutaciones sobrevivieron, y cada una era un hueco real de un test:** el DTO que no ordena las líneas por posición (SQL Server ya las devolvía en orden por el índice que usa para leerlas, así que ningún test lo veía: ahora uno baraja la lista interna); el DTO que devuelve otra versión (con la base en memoria la versión es siempre vacía: lo detecta Infrastructure); el endpoint que registra el título con `Console.WriteLine` (el escaneo solo buscaba `ILogger`; ahora también la consola y la traza); y, en el frontend, que un `Employee` no ve «Aceptar/Rechazar» en un presupuesto **enviado**, que «una línea inválida no entra en la vista previa» (mi test usaba un precio vacío, que suma 0 y no demuestra nada) y que **ningún test hacía que el servidor dijera un total distinto del cálculo local** (para un presupuesto guardado coinciden, así que nada distinguía de dónde salía el número).
- **Una mutación que no compila no se ha detectado:** tres mutaciones con `if (false)` rompían el estrechamiento de tipos del frontend; se rehicieron como inversiones.
- **El redondeo bancario habría dado importes distintos a los de un papel:** los casos fijos incluyen 0,5 → 1 y 2,5 → 3, y un test de la calculadora comprueba que el redondeo es *away from zero* y no a par.
- **«El IVA se redondea una vez por tipo» solo se ve con casos construidos para ello** (cinco líneas de 0,02 € al 21 %): por línea saldría 0,00 € y por tipo, 0,02 €.
- **Un test con la carrera real vale más que uno con dobles:** que la confirmación falle porque otra edición ganó, o que dos creaciones no repitan número, solo se ve con contextos distintos contra SQL Server de verdad; y un test con dobles (en Application) fue el que cazó el fallo de `ValueGeneratedNever`.
- **El formato `es-ES` de euros no separa los miles de un número de cuatro cifras** («1234,56 €»); un test del frontend lo fijó tras fallar con una expectativa equivocada mía.
- **En el navegador** la vista previa (80,85 / 6,48 / 87,33 €) coincidió **al céntimo** con lo que devolvió el servidor al guardar.

### Cifras

- **Backend:** Domain 465, Application 1152, Infrastructure 865 y Api 965 = **3447 casos**, todo en verde. **112 mutaciones a mano sobre el código real, todas detectadas** (81 del bloque 1 y 31 de la API). Comprobado en vivo con la API real, SQL Server real, Mailpit y TOTP calculado: **54/54**, con la base de datos coherente (ningún total distinto de neto + IVA, ningún número repetido por empresa, ninguna línea huérfana).
- **Frontend:** **693 specs** en verde (107 nuevos), **72/72 mutaciones** detectadas y comprobado en el navegador integrado contra la API real.
- **Gitleaks** sobre cada commit: sin hallazgos.

### Recomendación de despliegue (no es código)

1. **Aplicar la migración `AddQuotes`** (dos tablas nuevas, nada más). Las migraciones pendientes de otras specs (016, 018, 020, 021) siguen su propio ritmo.
2. **El backend puede desplegarse solo:** añade operaciones nuevas y una regla que solo restringe el borrado de un cliente con presupuestos. El frontend va después (sin él los presupuestos existen pero nadie los usa).
3. **Los permisos se reparten solos** (la tabla es parte del código): `Employee` prepara, `Manager` y superiores comprometen, `Technician` no ve nada. Avisar a quien lo vaya a notar.
4. **Un `Owner` o `Admin` sin segundo factor** recibe `403 mfa.enrollment_required` también aquí (spec 022).
5. **Vigilar `quote.number_unavailable`:** solo debería aparecer con ráfagas de creaciones en una misma empresa.
6. **Probar de punta a punta con una cuenta real** antes de abrir a clientes: crear, editar con dos pestañas, enviar, aceptar, y comprobar el total contra una calculadora.

### Qué queda cerrado (no se debe reabrir sin una razón nueva y explícita)

- **El dinero en céntimos enteros y calculado solo por el servidor:** ninguna petición lleva un total.
- **El IVA, una vez por tipo y *away from zero*,** con una lista cerrada de tipos en un único sitio.
- **Un presupuesto enviado es inmutable:** se corrige haciendo otro.
- **Solo un borrador se modifica o se elimina;** un resuelto no se reabre.
- **Preparar y comprometer son permisos distintos,** y un `Technician` no ve los precios.
- **El control de concurrencia con versión de fila y `Revision`,** y el conflicto es un `400` de negocio.
- **Ni títulos, ni notas, ni descripciones, ni importes en los registros.**
- **El aislamiento por empresa también en el número y en el listado.**

### Qué queda abierto para revisar más adelante

- **El PDF** (spec 025; el nombre fiscal, el NIF y la dirección de la empresa ya existen desde la spec 024 y el logotipo queda abierto) y **el envío por correo** (spec 026).
- **Crear el trabajo o la factura** a partir de un presupuesto aceptado; **duplicar** uno (la forma de corregir uno enviado); descuentos; caducidad automática; plantillas o catálogo de servicios.
- **La aceptación del cliente final en línea** (una identidad nueva y no confiable) y la firma.
- **Otras monedas y otros países;** adjuntos; **un borrador generado por IA** (fase 6).
- **Un contador atómico de números** si hace falta numerar ráfagas; **un índice por `(TenantId, CreatedAt)`** para el listado sin filtros.
- **Un test que compare los casos fijos entre los dos repositorios.**

---

## Relación con otros ADR

- Amplía: **[ADR-012](ADR-012-autorizacion-rbac.md)** (tres permisos nuevos y la matriz de 90 celdas), **[ADR-006](ADR-006-persistencia-domain-events.md)** (una entidad con líneas, la clave que genera el dominio y el control de concurrencia optimista), **[ADR-022](ADR-022-agenda.md)** (una fecha de calendario sin hora), **[ADR-011](ADR-011-errores-codigos-diccionario.md)** (14 códigos nuevos; ya hay 64).
- Relacionada con: **[ADR-002](ADR-002-multi-tenancy.md)** (las líneas llevan su propio `TenantId`; el número y el listado respetan el filtro), **[ADR-024](ADR-024-segundo-factor-obligatorio.md)** (la puerta se aplica sola), **[ADR-009](ADR-009-seguridad-base.md)** (nada de un presupuesto llega al registro), **[ADR-008](ADR-008-testing-integracion.md)** (el SQL Server real que prueba las carreras y los planes).
- Prepara: el PDF y el correo del presupuesto (specs 025 y 026), y la factura (fase 4), que reutilizará el modelo de líneas, el IVA por tipo y el redondeo.
