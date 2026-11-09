# Spec — 023-presupuestos

**Estado:** Aprobada (2026-11-09). Las tres decisiones de forma (alcance, relación con cliente y trabajo, y dinero e IVA) están **confirmadas con la persona dueña del proyecto** (D1, D2 y D3); el resto (D4–D14) son recomendaciones por revisar
**Fecha:** 2026-11-09
**Bounded Context:** Quotes (nuevo; **fase 3** del roadmap)
**Depende de:** spec 001 (clientes: a quién se presupuesta), specs 003 y 020 (trabajos: el enlace opcional), spec 008 y ADR-012 (permisos por rol), ADR-002 (aislamiento por empresa), ADR-006 (identificadores `Guid`, cadena de herencia de entidades y borrado), ADR-011 (códigos de error), ADR-024 (la puerta del segundo factor: se aplica sola), ADR-022 (fechas: esta spec decide qué hacer con una fecha **sin hora**)

> **Numeración:** 023 es la siguiente spec; su ADR será la **026** (la 025 ya existe).

---

## Objetivo

Que **una empresa pueda preparar, mandar a mano y dar por resuelto un presupuesto** dentro de OfiFlow: con sus líneas (descripción, cantidad, precio e IVA), su total calculado por el servidor y su estado (borrador → enviado → aceptado o rechazado).

Es el paso que sigue a «cliente → trabajo» en el flujo de valor del documento maestro (**cliente → trabajo → presupuesto → cita → factura → cobro**) y el primer documento **con dinero** del producto. Hoy, quien presupuesta lo hace fuera (una hoja de cálculo, Word) y pierde la trazabilidad: qué se ofreció, a quién, cuándo y qué respondió. Esta spec **no genera el PDF ni manda el correo**: crea el **modelo de datos correcto** (el difícil de cambiar después) y la pantalla para gestionarlo; el PDF y el envío son las specs siguientes.

## Alcance — Qué SÍ incluye

Cuatro bloques:

1. **El dominio y la persistencia (backend):** `Quote` con sus líneas (`QuoteItem`), el cálculo del total, los estados y sus transiciones, el número correlativo por empresa, la migración y el control de concurrencia.
2. **La API:** crear, ver, listar (paginado y filtrable), modificar un borrador, eliminar un borrador y las tres transiciones (enviar, aceptar, rechazar), con tres permisos nuevos, sus tests de extremo a extremo y las mutaciones de lo que decide.
3. **Frontend:** una entrada «Presupuestos» con el listado y la pantalla de un presupuesto (cabecera, editor de líneas con el total, y los botones de las transiciones), sin librerías nuevas.
4. **Documentación:** ADR-026, el manual y la spec cerrada.

## Alcance — Qué NO incluye

- **NEXT (spec 024):** el **PDF** del presupuesto. Exige antes los **datos de la empresa** en el documento (nombre fiscal, NIF, dirección, logotipo) que hoy no existen.
- **NEXT (spec 025):** **enviarlo por correo** al cliente (con el PDF adjunto). En esta spec «Enviar» **solo marca el presupuesto como enviado**: la persona lo ha mandado por el medio que sea.
- **NEXT:** crear el **trabajo** (o la factura) a partir de un presupuesto aceptado; **duplicar** un presupuesto (la forma de «corregir» uno ya enviado); descuentos por línea o globales; caducidad automática; plantillas o catálogo de servicios y materiales; notas internas distintas de las que ve el cliente.
- **LATER:** que **el cliente final lo acepte en línea** (hoy un `Customer` es un dato de la empresa, no un usuario: sería una identidad nueva, anónima y no confiable, y un riesgo propio); firma; otras monedas y otros países (otros tipos de IVA); adjuntos; **generar el borrador con IA** (fase 6).
- **NEVER:** modificar un presupuesto **ya enviado** (ni su precio ni sus líneas). Un precio ofrecido a un cliente es un compromiso: se corrige haciendo uno nuevo, no reescribiendo el anterior (R4).
- **No cambia:** los trabajos y los clientes, salvo **una** regla nueva: no se puede eliminar un cliente que tenga presupuestos (R13).

## Reglas de negocio / invariantes

**El presupuesto y sus líneas**

- **R1. Un presupuesto es de un cliente de la empresa y puede enlazarse a un trabajo** de ese mismo cliente (D2). Con un trabajo de **otro** cliente, `400 quote.job_customer_mismatch`. El **cliente de un presupuesto no cambia** (se corrige uno hecho a un cliente equivocado eliminándolo y creando otro). Un cliente o un trabajo que no existan **o sean de otra empresa** dan el mismo `404` de siempre (`customer.not_found`, `job.not_found`): no se informa de lo que existe en otra empresa.
- **R2. Datos de la cabecera:** `Title` (obligatorio, hasta 200), `Notes` (condiciones y observaciones **que ve el cliente**, hasta 2000), `ValidUntil` (fecha **sin hora**, opcional, entre los años 2000 y 2099; D8). Se guardan sin espacios sobrantes. `Number` lo asigna el servidor (R6), no la petición.
- **R3. Líneas.** De **0 a 100** por presupuesto (un borrador puede estar vacío; **enviarlo** exige al menos una, R7). Cada una: `Description` (obligatoria, hasta 500), `Quantity` (**mayor que 0**, hasta **3 decimales** y como máximo 99 999,999), `UnitPriceCents` (**céntimos enteros**, de 0 a 9 999 999 999; un precio 0 vale, es «incluido») y `VatPercent` (**solo 0, 4, 10 o 21**: lista cerrada en un único sitio; D3 y D10). Un tipo de IVA o una cantidad inválidos dan un error de **campo** con su propio código (`quote.vat_invalid`, `quote.quantity_invalid`), y una validez fuera de rango `quote.valid_until_invalid`. Se guardan en el orden en que llegan y ese orden se conserva.
- **R4. Solo un borrador se modifica.** Cambiar la cabecera, **sustituir las líneas** (D6) o eliminarlo solo se puede en `Draft`. Un presupuesto `Sent`, `Accepted` o `Rejected` es **inmutable** (NEVER). `400 quote.not_editable` / `quote.cannot_delete`.

**El dinero** (D3, D4)

- **R5. El servidor calcula todo; la petición nunca manda un total.** Con céntimos enteros y `decimal` solo dentro del cálculo:
  - `neto de la línea = redondeo(Quantity × UnitPriceCents)`, redondeado **al céntimo, alejándose de cero** (*away from zero*).
  - `base por tipo de IVA = suma de los netos de las líneas con ese tipo`; `IVA del tipo = redondeo(base × tipo / 100)` (**una vez por tipo**, no por línea: es cómo lo desglosa una factura).
  - `Neto = suma de las bases`, `IVA = suma de los IVA por tipo`, `Total = Neto + IVA`.
  - Una **sola** función del dominio lo calcula (`Quote.Recalculate`); se **guarda** en la fila (para listar sin cargar las líneas) y se recalcula en cada cambio. Los límites de R3 garantizan que ninguna suma desborda un `long`.
- **R6. Número correlativo por empresa.** `Number` es un entero que empieza en 1 **dentro de cada empresa**, asignado al crear (D5). Es **único por empresa** (índice único). Dos creaciones simultáneas **no repiten número**: la perdedora reintenta (hasta tres veces) y, si no lo consigue, `400 quote.number_unavailable`. Nunca se reasigna uno ya **enviado**; un borrador eliminado puede dejar un hueco o liberar el último número (los borradores no salen de la empresa).

**Estados** (D7)

```
Draft ──send──▶ Sent ──accept──▶ Accepted
  │               └─────reject──▶ Rejected
  └──delete (físico)
```

- **R7. `Draft → Sent` (enviar)**: exige **al menos una línea** (`400 quote.empty`) y deja `SentAt` (UTC, de `TimeProvider`). `400 quote.cannot_send` desde cualquier otro estado.
- **R8. `Sent → Accepted` o `Sent → Rejected`**, con `DecidedAt`. `400 quote.cannot_accept` / `quote.cannot_reject` desde cualquier otro estado (un borrador **no** se acepta, y un presupuesto resuelto **no** se reabre).
- **R9. La caducidad no es un estado.** `ValidUntil` pasado **no bloquea nada** (no se rechaza al crear ni al aceptar; D8): la pantalla lo marca como «caducado». Quien decide es la persona.

**Concurrencia** (D9)

- **R10. Control de concurrencia optimista** con una versión de fila. El presupuesto que se lee **lleva su `version`** y `PUT` la exige: si alguien lo modificó mientras tanto, `400 quote.changed_meanwhile` y **no se pisa nada**. Una transición y una edición simultáneas **nunca** dejan un presupuesto enviado con líneas cambiadas: gana una y la otra recibe el mismo error.

**Quién puede y cuánto**

- **R11. Tres permisos nuevos** (D11): `Quotes.Read` (ver y listar), `Quotes.Write` (crear, modificar y eliminar **borradores**) y `Quotes.Issue` (**enviar, aceptar y rechazar**: compromete un precio ante un cliente). `Owner`, `Admin` y `Manager` tienen los tres; `Employee`, `Read` y `Write`; `Technician`, **ninguno** (los precios son información comercial; D11). La matriz pasa de 15 a **18 permisos** y de 75 a **90 celdas**.
- **R12. Todo es de la empresa del token.** Nada de lo que llega en la petición decide de qué empresa es un presupuesto (`TenantId` sale de la sesión); las consultas pasan por el filtro global por empresa, y un presupuesto de otra empresa da `404 quote.not_found`, igual que uno inexistente.
- **R13. No se elimina un cliente con presupuestos** (`400 customer.has_quotes`): un presupuesto es un documento comercial y no se borra a espaldas de nadie. Es la única regla nueva sobre los clientes.
- **R14. La puerta del segundo factor se aplica sola** (ADR-024): ninguna operación de presupuestos está en la lista cerrada, así que un `Owner` o `Admin` sin factor recibe `403 mfa.enrollment_required`. Un escaneo existente ya fija que la lista sigue siendo exactamente cuatro.

**Lectura**

- **R15. El listado es paginado y filtrable** (D12): por estado y por cliente; el más reciente primero; **hasta 100 por página**; devuelve cabecera y totales (no las líneas). La lectura de uno devuelve las líneas, sus importes y el `version`.

## Modelo conceptual

```
Quote (raíz de agregado; ITenantOwned, IAuditable)
- Id, TenantId, CustomerId, JobId?            relaciones por Id, sin navegación (ADR-006)
- Number                                       int, único por (TenantId, Number)
- Title, Notes?, ValidUntil?                   (ValidUntil: DateOnly)
- Status                                       Draft | Sent | Accepted | Rejected
- NetCents, VatCents, TotalCents               long, calculados (R5)
- SentAt?, DecidedAt?                          instantes UTC
- Revision                                     int: sube con CADA cambio, para que la fila siempre se actualice y la versión la vigile (R10)
- CreatedAt, UpdatedAt                         (IAuditable)
- RowVersion                                   concurrencia (R10)
- Items: QuoteItem[]                           0..100, parte del agregado (tabla QuoteItems)

QuoteItem (entidad propia del agregado; no se consulta sin su Quote; lleva su propio TenantId para el filtro global)
- Id (la genera el dominio), TenantId, QuoteId, Position, Description
- Quantity (decimal(9,3)), UnitPriceCents (long), VatPercent (0|4|10|21)

Quote.Create / UpdateDetails / ReplaceItems / Send / Accept / Reject / Recalculate   (invariantes en el dominio)
VatRates.Allowed = {0, 4, 10, 21}                                                    (un solo sitio)
Permission + QuotesRead, QuotesWrite, QuotesIssue      (los roles: tabla de la R11)

Endpoints (todos bajo /api/v1, con sesión):
POST   /quotes                 crear (borrador)
GET    /quotes                 listar (?status=&customerId=&page=&pageSize=)
GET    /quotes/{id}            ver con sus líneas
PUT    /quotes/{id}            modificar el borrador (cabecera + líneas + version)
DELETE /quotes/{id}            eliminar un borrador
POST   /quotes/{id}/send       enviar        (Quotes.Issue)
POST   /quotes/{id}/accept     aceptar       (Quotes.Issue)
POST   /quotes/{id}/reject     rechazar      (Quotes.Issue)

Migración: tablas Quotes y QuoteItems; índices únicos (TenantId, Number); (TenantId, CustomerId);
           (TenantId, Status, CreatedAt DESC) para el listado.
```

## Criterios de aceptación

- Dado un cliente de mi empresa, cuando creo un presupuesto con dos líneas, entonces `201`, queda en `Draft`, con **el número siguiente de mi empresa** y los **totales que calcula el servidor** (aunque la petición mande unos «totales», se ignoran).
- Dado un cálculo con varios tipos de IVA y cantidades decimales, entonces el desglose coincide con una **tabla de casos fijos** (vectores): redondeo alejándose de cero, IVA **una vez por tipo**, suma exacta en céntimos.
- Dado un trabajo de **otro cliente**, entonces `400 quote.job_customer_mismatch`; dado un cliente o un trabajo **de otra empresa**, entonces `404`, indistinguible de uno inexistente.
- Dado un borrador, cuando lo modifico con la `version` que leí, entonces cambia y se recalculan los totales; con una `version` **vieja**, `400 quote.changed_meanwhile` y no cambia nada.
- Dados **dos modificaciones simultáneas** con la misma `version`, entonces **una** gana y la otra recibe `quote.changed_meanwhile`; dada una **edición y un envío simultáneos**, entonces nunca queda un presupuesto enviado con líneas cambiadas.
- Dado un borrador **sin líneas**, cuando lo envío, entonces `400 quote.empty`; con líneas, queda `Sent` con `SentAt`.
- Dado un presupuesto `Sent`, entonces **no se modifica ni se elimina** (`quote.not_editable`, `quote.cannot_delete`), y desde él se acepta o se rechaza una sola vez; un `Draft` no se acepta ni se rechaza, y uno `Accepted` o `Rejected` no se reabre.
- Dado un `ValidUntil` pasado, entonces nada se bloquea (y la API no lo convierte en un estado).
- Dados **dos creaciones simultáneas** en la misma empresa, entonces **dos números distintos**, sin `500`; y dos empresas pueden tener cada una su número 1.
- Dado un `Technician`, entonces `403` en todo; un `Employee` crea y modifica pero **no** envía, acepta ni rechaza (`403`); `Manager`, `Admin` y `Owner` pueden todo.
- Dado un `Owner` o `Admin` **sin** segundo factor, entonces `403 mfa.enrollment_required`.
- Dado un cliente **con presupuestos**, cuando lo elimino, entonces `400 customer.has_quotes`; sin ellos, como siempre.
- Dada una petición **sin sesión**, entonces `401`.
- Dado el listado, entonces va paginado (máximo 100), ordenado del más reciente al más antiguo, filtrable por estado y por cliente, sin las líneas.

Caso multi-tenant obligatorio: la empresa sale **solo del token**; un presupuesto, un cliente o un trabajo de otra empresa **no se ve, no se modifica, no se enlaza ni se cuenta** (tampoco al calcular el siguiente número ni al listar), con el mismo `404` que si no existiera.

## Seguridad (ADR-009)

- **Datos personales:** ninguno nuevo en sí; un presupuesto nombra a un cliente (que ya existe) y puede llevar texto libre (`Title`, `Notes`, `Description`) que **no se registra** en ningún log. Los **precios** son información comercial sensible dentro de la empresa: por eso el `Technician` no los ve (D11).
- **Autorización:** tres permisos (R11), comprobados en Application, **antes** de leer nada. Un presupuesto de otra empresa no se distingue de uno inexistente.
- **Entrada:** longitudes máximas en el `Validator` y en la base de datos desde una **fuente única** (constantes del dominio); número de líneas, cantidades y precios **acotados** (R3) para que ni una suma ni una multiplicación desborden; el IVA solo de una **lista cerrada**; las fechas, entre 2000 y 2099. **Ninguna cantidad, precio ni total llega de la petición como dato de cálculo** (R5): el servidor es la única fuente de verdad del dinero.
- **Aislamiento:** `ITenantOwned` + filtro global + el índice por `TenantId` primero; la lista blanca de entidades globales **no** cambia (un test de aislamiento nuevo, como los de clientes y trabajos). `TenantId` y `Number` nunca se leen de la petición.
- **SQL crudo:** ninguno (el número se asigna con índice único y reintento, no con SQL a mano; D5).
- **Abuso:** endpoints con sesión, **sin hash costoso**; el límite global de la API ya aplica. Una petición con 100 líneas es acotada; el listado, paginado. Sin límite propio (se reconsidera si aparece abuso).
- **Enumeración:** nada que enumerar fuera de la empresa; entre empresas, `404` uniforme.
- **Eventos de seguridad:** ninguno nuevo (como los trabajos: no es una operación de seguridad; las denegaciones ya las registra la autorización). **Ningún texto libre ni importe** en los registros.
- **Modelo de amenazas (lo importante):**
  - **Mejora:** el precio ofrecido a un cliente **no se puede reescribir a posteriori** (R4, R10): hay una trazabilidad de qué se ofreció y cuándo (`SentAt`).
  - **Mejora:** el servidor es la única fuente de los importes: un cliente manipulado no puede «rebajar» un total.
  - **Riesgo aceptado:** **cualquier miembro con `Quotes.Issue` puede aceptar un presupuesto «en nombre» del cliente**: aceptar es hoy una anotación interna, no una firma del cliente. Hasta que exista la aceptación del cliente (LATER) no hay valor contractual en el sistema, solo un registro.
  - **Riesgo aceptado:** un `Employee` puede ver y fijar precios en borradores (para preparar el presupuesto) pero **no comprometerlos** (enviar). Quien tenga `Write` y no `Issue` no puede dar un precio por ofrecido.

## Decisiones técnicas relevantes

- **D1. Alcance: crear y gestionar, sin PDF ni correo.** *Confirmada con la persona dueña del proyecto.* El PDF exige antes datos de la empresa que no existen, y el correo con adjunto depende del PDF. Mezclar los tres en una sola spec la haría enorme y poco verificable; el modelo de datos, que es lo difícil de cambiar, sí queda cerrado aquí.
- **D2. Un presupuesto cuelga del cliente, con trabajo opcional.** *Confirmada con la persona dueña del proyecto.* En un oficio se presupuesta **antes** de que exista el trabajo. La alternativa (siempre a un trabajo) sigue al pie de la letra el esquema del documento maestro, pero obliga a crear un trabajo antes de presupuestar. Con el enlace opcional se pueden asociar después sin rehacer el modelo.
- **D3. Dinero en céntimos enteros y un IVA por línea.** *Confirmada con la persona dueña del proyecto.* Sin decimales flotantes; cada línea lleva su tipo (se mezclan materiales y mano de obra con tipos distintos). Solo euros: no hay columna de moneda (añadirla es una migración pequeña el día que haga falta; guardarla ahora sería un dato muerto).
- **D4. El IVA se calcula una vez por tipo, sobre la suma de bases.** *Recomendado.* Es como lo desglosa una factura (base e IVA por tipo), así que el presupuesto y la factura futura **coincidirán al céntimo**. Redondear línea a línea es más simple pero se desvía de la factura. Redondeo *away from zero*; **una** función del dominio, probada con una tabla de casos fijos que **comparte** el frontend para su vista previa (los mismos vectores en los dos repositorios).
- **D5. Número por empresa con `MAX + 1`, índice único y reintento.** *Recomendado.* Sin SQL crudo (una regla del proyecto) y sin una tabla de contadores. Una carrera la resuelve el índice único y un reintento acotado; se prueba con una carrera real contra SQL Server. Consecuencia: un borrador eliminado puede dejar un hueco o liberar el último número; no importa, porque un borrador no ha salido de la empresa. **Una numeración fiscal sin huecos es asunto de la factura (fase 4), no del presupuesto.**
- **D6. `PUT` sustituye la cabecera y todas las líneas a la vez.** *Recomendado.* Es atómico, evita endpoints por línea y una clase de errores (líneas huérfanas, orden, totales a medias), y el editor de la pantalla ya trabaja con la lista entera. Coste: se envía todo en cada guardado (como mucho 100 líneas).
- **D7. Cuatro estados y un enviado inmutable.** *Recomendado.* `Draft`, `Sent`, `Accepted`, `Rejected`. «Enviar» solo anota (no manda nada: D1). **Un presupuesto enviado no se reabre ni se edita**: corregirlo es hacer uno nuevo (NEXT: «duplicar»). Un estado «caducado» automático o «anulado» se difiere: cada uno es una regla de producto que conviene decidir viendo el uso real.
- **D8. `ValidUntil` es una fecha sin hora (`DateOnly`) y no bloquea nada.** *Recomendado.* La ADR-022 obligó a un desfase en los **instantes**; una fecha de calendario («válido hasta el 30 de noviembre») no tiene huso ni hora que interpretar, así que no lleva desfase. Es informativa, como la fecha pasada de un trabajo (spec 020, D5): se marca en pantalla y quien decide es la persona.
- **D9. Control de concurrencia optimista (`RowVersion`) en `Quote`.** *Recomendado, a diferencia de `Job` (spec 020, D8).* Aquí hay dinero comprometido: dos personas editando el mismo borrador, o una editando mientras otra envía, **no** pueden dejar un precio cambiado tras ofrecerlo. El error es un `400` de negocio (`quote.changed_meanwhile`) y no un `409` nuevo, para no añadir una traducción de errores HTTP por una sola operación.
- **D10. El IVA, una lista cerrada (0, 4, 10, 21).** *Recomendado.* El caso español (general, reducido, superreducido y exento). Una lista cerrada impide un `13,37 %` de un cliente manipulado y se amplía en un único sitio cuando haya otro país; un porcentaje libre es un campo más que validar sin beneficio.
- **D11. Tres permisos: `Quotes.Read`, `Quotes.Write`, `Quotes.Issue`.** *Recomendado, y es la decisión que más conviene revisar.* Separar **preparar** (`Write`) de **comprometer** (`Issue`: enviar, aceptar, rechazar) sigue el patrón de `Jobs.Schedule` (spec 020): enviar compromete un precio ante un cliente. `Employee` (personal de oficina) prepara pero no compromete. `Technician` **no ve presupuestos**: los precios son comerciales y su trabajo es ejecutar. Alternativas: dos permisos (`Read` y `Write` con las transiciones dentro), más simple pero más permisivo; o dar `Read` también al técnico (útil para saber qué se prometió; reversible después).
- **D12. Listado paginado con filtros por estado y cliente.** *Recomendado.* Clientes y trabajos devuelven todo sin paginar, lo que es un límite conocido; los presupuestos se acumulan y llevan dinero, y la paginación (`PagedResult` ya existe) es más fácil de introducir ahora que de añadir con clientes en producción. Los índices se miden con el plan real, como en la agenda.
- **D13. Frontend sin librerías nuevas, con el cálculo del servidor como única verdad.** *Recomendado.* La pantalla muestra una **vista previa** del total mientras se edita, calculada con las mismas reglas y **los mismos vectores de prueba** que el servidor; al guardar, lo que manda es lo que devuelve la API. Dinero en pantalla con el formato de euros de `es-ES`.
- **D14. Orden de trabajo:** dominio, persistencia y cálculo (**bloque 1**), API y pruebas de extremo a extremo (**2**), frontend (**3**) y documentación (**4**). *Recomendado.* El bloque 1 es lo único que toca el modelo y la migración; los bloques 1 y 2 se entregan juntos y el backend **puede fusionarse solo** (añade operaciones nuevas; la única regla sobre lo existente es R13, que solo **restringe** el borrado de un cliente con presupuestos, y sin presupuestos nada cambia).
- **¿Requiere ADR nueva? Sí: ADR-026**, «Presupuestos: dinero en céntimos, IVA por tipo, estados e inmutabilidad», con las decisiones D3 a D11 y anotaciones «Ampliada por» en ADR-012 (los tres permisos y la matriz de 90 celdas), ADR-006 (una entidad con líneas y concurrencia optimista) y ADR-022 (una fecha sin hora).

## Fuera de alcance / Backlog relacionado

- El PDF y los datos de la empresa en el documento (spec 024); enviarlo por correo (spec 025).
- Crear el trabajo o la factura desde un presupuesto aceptado; duplicar; descuentos; caducidad automática; plantillas y catálogo.
- Aceptación en línea por el cliente final (una identidad nueva y no confiable); firma.
- Otras monedas y otros países; adjuntos; borrador generado por IA.
- Un límite propio de peticiones, si aparece abuso.

---

## Validación antes de aprobar

> ¿Esta funcionalidad ayuda a conseguir o mantener al primer cliente?

Sí, y es de las que más: **presupuestar es el momento en que un autónomo o una pequeña empresa de oficios gana o pierde un cliente**, y es el primer documento con dinero del producto. Sin presupuestos OfiFlow es una lista de clientes y trabajos; con ellos empieza a ser el flujo «cliente → trabajo → presupuesto» que el documento maestro vende. Además es **la base de la factura** (fase 4): el modelo de líneas, el IVA por tipo y el redondeo se deciden aquí una sola vez. Se mantiene **pequeña a propósito** (sin PDF ni correo) porque el riesgo no está en la pantalla, sino en el modelo del dinero y en su inmutabilidad, que son las dos cosas que no se pueden arreglar a posteriori sin migrar datos de clientes reales.
