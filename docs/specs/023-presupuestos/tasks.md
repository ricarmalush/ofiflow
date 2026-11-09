# Tasks — 023-presupuestos

**Spec relacionada:** [./spec.md](./spec.md)
**ADR relacionadas:** [ADR-012](../../adr/ADR-012-autorizacion-rbac.md) (los tres permisos), [ADR-002](../../adr/ADR-002-multi-tenancy.md) (aislamiento por empresa), [ADR-006](../../adr/ADR-006-persistencia-domain-events.md) (entidades, `Guid`, borrado), [ADR-011](../../adr/ADR-011-errores-codigos-diccionario.md) (códigos de error), [ADR-022](../../adr/ADR-022-agenda.md) (fechas: aquí, una fecha sin hora), [ADR-024](../../adr/ADR-024-segundo-factor-obligatorio.md) (la puerta se aplica sola) y la nueva **ADR-026** («Presupuestos»)
**Estado general:** En curso (spec aprobada el 2026-11-09; ningún bloque empezado)

---

## Convención

- Cada tarea completable y verificable en una sesión.
- Se marca `[x]` solo cuando cumple la Definición de «Done»: compila, tests en verde, mutaciones detectadas, comprobado en vivo y manual al día.
- **Nivel de riesgo de las pruebas: alto.** Es el primer documento **con dinero**: un cálculo mal redondeado, un presupuesto enviado que se puede reescribir o dos empresas que se ven los precios son errores caros. Se aplica la batería completa: **una tabla de casos fijos del cálculo** (compartida con el frontend), la máquina de estados entera (cada transición permitida y **cada una prohibida**), la inmutabilidad, la **concurrencia real** (dos ediciones, edición contra envío, dos números a la vez, contra SQL Server), el aislamiento entre empresas, los tres permisos en los cinco roles, escaneos de arquitectura y mutaciones sobre todo lo que decide.
- Las mutaciones se hacen a mano sobre el código real, con copia de seguridad y **`touch` al restaurar**; solo cuenta como «detectada» una con tests en rojo (`[FAIL]`); un test colgado o un error de compilación no cuentan. **Una mutación debe quitar de verdad lo que dice quitar** (lección de las specs 021 y 022). Los guiones van **en ficheros** (nunca en `node -e` con comillas invertidas dentro).
- Las suites se lanzan **por proyecto, una detrás de otra**. Si `dotnet build` choca con una DLL bloqueada (IIS Express), se compila a una carpeta aparte con `-p:OutDir` y no se toca ese proceso.
- Verificación en vivo con la receta de siempre (base desechable, API real, y en el bloque 3 `ng serve` y el navegador integrado). Los valores de prueba (importes, textos) se generan en ejecución.
- **Orden de entrega:** el backend **puede fusionarse solo** (operaciones nuevas; la única regla sobre lo existente, R13, solo restringe el borrado de un cliente con presupuestos). El frontend va en el mismo par de PR por limpieza.

---

## Bloques

### Bloque 1 — El dominio, el cálculo y la persistencia (Domain + Application + Infrastructure)
- [ ] **Dominio del dinero:** `VatRates.Allowed` (0, 4, 10, 21) en un único sitio y las **constantes de cotas** (líneas máx. 100, descripción 500, título 200, notas 2000, cantidad máx. 99 999,999 con 3 decimales, precio máx. 9 999 999 999 céntimos, fechas 2000–2099), fuente única del dominio, el validador y EF.
- [ ] **`Quote` + `QuoteItem`** (`AggregateRoot`, `ITenantOwned`, `IAuditable`): `Create`, `UpdateDetails`, `ReplaceItems`, `Send`, `Accept`, `Reject` y `Recalculate` (**una sola función del cálculo**, R5: neto por línea con redondeo *away from zero*, base por tipo, IVA **una vez por tipo**, suma). Las invariantes viven en el dominio: solo `Draft` cambia (R4), `Send` exige líneas (R7), las transiciones desde un estado inválido lanzan su error (R7, R8). `SentAt` y `DecidedAt` salen de `TimeProvider`.
- [ ] **Errores y diccionario:** `quote.not_found` (`404`), `quote.not_editable`, `quote.cannot_delete`, `quote.cannot_send`, `quote.cannot_accept`, `quote.cannot_reject`, `quote.empty`, `quote.job_customer_mismatch`, `quote.number_unavailable`, `quote.changed_meanwhile` y `customer.has_quotes`, con su texto en `ErrorMessages.resx` (el test del diccionario completo lo exige) y su caso en `ExceptionMappingContractTests`.
- [ ] **Permisos (Domain):** `QuotesRead`, `QuotesWrite` y `QuotesIssue` con la tabla de la R11 (`Technician`: ninguno). Los tests que cuentan permisos y celdas (`RolePermissionsTests`, `PermissionNamesTests`) y la tabla de operaciones se actualizan **a propósito**: 15 → 18 permisos y 75 → 90 celdas.
- [ ] **Persistencia:** `QuoteConfiguration` y `QuoteItemConfiguration` (longitudes desde las constantes, `Quantity decimal(9,3)`, `ValidUntil` como `date`, `RowVersion`, el filtro global por empresa, los índices de la spec: único `(TenantId, Number)`, `(TenantId, CustomerId)` y `(TenantId, Status, CreatedAt)`), `IApplicationDbContext.Quotes` y la **migración** (revisada a mano: tablas, índices y nada más).
- [ ] **Casos de uso (Application):** `CreateQuote` (con el **número siguiente** y hasta tres reintentos ante la violación del índice único), `UpdateQuote` (con `version`), `DeleteQuote`, `SendQuote`, `AcceptQuote`, `RejectQuote`, `GetQuote`, `GetQuotes` (paginado, filtros por estado y cliente) y sus `Validator`s (longitudes **antes** de tocar la base). Cada comando declara su permiso. `DeleteCustomer` gana la regla **R13** (`customer.has_quotes`).
- [ ] **Tests de Domain:** el cálculo con una **tabla de casos fijos** (`QuoteTotalsVectors`: un caso por regla de redondeo, mezcla de tipos, cantidades decimales, precio 0, la línea que sube justo medio céntimo, el máximo permitido sin desbordar), la máquina de estados **entera** (matriz estado × operación: cada permitida y cada prohibida), las cotas, el IVA fuera de la lista y que `Recalculate` es la **única** vía a los totales.
- [ ] **Tests de Application (dobles):** cada rama de cada caso de uso (cliente o trabajo de otra empresa → `404`; trabajo de otro cliente; `Send` sin líneas; editar o eliminar un enviado; versión vieja), que la petición **nunca** fija un total, el reintento del número y su límite, `DeleteCustomer` con y sin presupuestos y los validadores (cada cota en sus dos lados).
- [ ] **Tests de Infrastructure (SQL Server real):** el filtro por empresa (la de otra no se ve, **tampoco al calcular el número ni al listar**), el índice único del número, **dos creaciones simultáneas** (números distintos), **dos modificaciones simultáneas** con la misma versión (gana una), **edición contra envío** (nunca un enviado con líneas cambiadas), la ida y vuelta de decimales y fechas (`decimal(9,3)`, `DateOnly`) y el **plan real** del listado (usa el índice).
- [ ] **Mutaciones:** el redondeo hacia otro lado o por línea en vez de por tipo; un tipo de IVA aceptado de más; los totales que no se recalculan al sustituir líneas; editar un enviado; enviar sin líneas; aceptar un borrador o reabrir uno resuelto; la versión que no se comprueba; el número sin reintento o con reintento infinito; el filtro por empresa quitado (al listar y al numerar); un trabajo de otro cliente admitido; `has_quotes` ausente; el permiso que falta a un rol o sobra a `Technician`.
- [ ] **[manual]** Diferido al **bloque 4**.
- [ ] [externo] Commit del bloque 1

### Bloque 2 — La API y sus pruebas de extremo a extremo (Api)
- [ ] **Los ocho endpoints** de la spec (`POST`, `GET` lista, `GET` uno, `PUT`, `DELETE` y `POST send/accept/reject`) con `201`/`200`/`204` según corresponda, `Cache-Control: no-store` en las lecturas, el cuerpo **sin** `tenantId` ni totales de entrada, documentados en OpenAPI y en el `.http`.
- [ ] **Escaneos de arquitectura** (al estilo de las specs 018, 021, 022 y 019): ningún DTO de entrada tiene campos de importe total ni `TenantId`, ninguna consulta de presupuestos se salta el filtro por empresa, ninguna operación de presupuestos está en la lista cerrada del segundo factor (la lista sigue siendo **exactamente cuatro**), todo comando declara su permiso (la tabla de operaciones se actualiza **a propósito**) y los registros no reciben títulos, notas, descripciones ni importes.
- [ ] **Tests de extremo a extremo** (API entera, SQL Server real): el flujo completo (crear → editar → enviar → aceptar), cada error en su código y su `HTTP`, la **inmutabilidad** (un enviado no se edita ni se borra), la concurrencia por HTTP (dos `PUT` con la misma versión; `PUT` contra `send`), la **matriz de roles** (los tres permisos en los cinco roles, con **control positivo** para cada permitido), un `Owner` y un `Admin` sin factor (`403 mfa.enrollment_required`), **aislamiento entre empresas** (leer, modificar, enviar, enlazar cliente o trabajo ajeno, listar y numerar), paginación y filtros, un total que la petición intenta fijar (se ignora) y que ningún importe ni texto libre llega al registro.
- [ ] **Mutaciones** de la capa API: el endpoint sin permiso o con otro, el verbo o el código de estado cambiado, sin `no-store`, un `Issue` que se concede a `Employee`, el filtro por empresa quitado en el endpoint, la operación añadida a la lista del factor.
- [ ] **Comprobación en vivo** (API real, base desechable, dos empresas): el flujo completo con importes reales (comparados con los vectores), la versión vieja, el envío tras una edición, el aislamiento entre las dos empresas y el listado con muchos presupuestos.
- [ ] **[manual]** Diferido al **bloque 4**.
- [ ] [externo] Commit del bloque 2

### Bloque 3 — Frontend (`ofiflow-frontend`)
- [ ] **Modelo y servicio:** `Quote`, `QuoteItem`, `QuoteStatus`, el servicio con sus ocho llamadas y el modelo de permisos con los tres nuevos (y `fake-account`).
- [ ] **Cálculo compartido:** una función pura `computeQuoteTotals` con **los mismos vectores** que el backend (copiados como datos de prueba) para la **vista previa**; lo que manda siempre es lo que devuelve la API.
- [ ] **Listado «Presupuestos»** (entrada del menú solo con `Quotes.Read`): tabla paginada con número, cliente, título, estado, total y validez (con «caducado» calculado en pantalla), filtros por estado y cliente.
- [ ] **Pantalla de un presupuesto:** cabecera (cliente, trabajo opcional del mismo cliente, título, validez, notas), **editor de líneas** (añadir, quitar, orden, importe de la línea y desglose por tipo de IVA en vivo), y los botones **Guardar**, **Enviar**, **Aceptar**, **Rechazar** y **Eliminar** según el estado y los permisos (solo un borrador se edita; el resto, solo lectura). Errores de la API en su campo o arriba; `quote.changed_meanwhile` ofrece recargar sin perder lo escrito.
- [ ] **Formato** de euros de `es-ES`; entradas con tope; ninguna cifra se calcula a mano en la plantilla.
- [ ] **Tests (specs):** servicio, cálculo (los vectores), listado (filtros, paginación, «caducado»), editor (añadir y quitar, orden, total en vivo, límites de 100 líneas, el botón bloqueado mientras guarda), botones por estado y por permiso, y que un `Technician` no ve la entrada del menú.
- [ ] **Mutaciones** del frontend: el vector que no coincide, los botones que se ofrecen en un estado o rol equivocado, el guardado que no manda la versión, el doble envío, el IVA que se acepta fuera de la lista.
- [ ] **Comprobación en vivo** (navegador integrado, API real): crear con varias líneas y tipos de IVA, ver el total en vivo coincidir con el de la API, enviar, aceptar, el modo solo lectura, la versión vieja desde dos pestañas, un `Technician` sin la entrada, y el ancho de móvil.
- [ ] [externo] Commit del frontend (en `ofiflow-frontend`)

### Bloque 4 — Documentación
- [ ] **ADR-026** (`docs/adr/ADR-026-presupuestos.md`): el modelo del dinero (céntimos, IVA por tipo, redondeo), los estados y la inmutabilidad, la numeración, la concurrencia, los permisos, lo que cambió al implementar, la recomendación de despliegue y lo que queda abierto.
- [ ] Anotaciones «Ampliada por ADR-026» en **ADR-012** (los tres permisos y las 90 celdas), **ADR-006** (una entidad con líneas y concurrencia optimista) y **ADR-022** (una fecha sin hora); `docs/README.md` y el roadmap del `README.md` (la fase 3 empieza).
- [ ] Manual: capítulo 0 (qué se puede hacer hoy), 3 y 5 (la nueva carpeta y el aislamiento), 7 (los códigos nuevos y el recuento real), 8 (el mapa de amenazas, los permisos), 9 (cifras, la sección de la spec 023, mutaciones y lecciones), 11 (si el ejemplo de «añadir una funcionalidad» lo pide), 12 (glosario e índices) y el anexo del tribunal.
- [ ] `spec.md` pasa a «Implementada» y este `tasks.md` queda completo, con la Definición de Done al día.
- [ ] [externo] Commit del bloque 4 y de `tasks.md`
- [ ] [externo] Push de las dos ramas y PR (backend y frontend), con tu orden

---

## Definición de Done (por bloque)

- Compila sin avisos nuevos y los tests de **todos los proyectos afectados** pasan, lanzados por proyecto.
- Cada regla de la spec tiene al menos un test que falla si se rompe; lo que decide (el cálculo y su redondeo, el IVA por tipo, la inmutabilidad, la máquina de estados, la versión, el número por empresa, el aislamiento, los permisos) tiene además su mutación detectada.
- Comprobado en vivo contra la API real y, en el bloque 3, en el navegador.
- **Ningún texto libre ni importe** en logs, eventos, errores ni ejemplos (los valores de prueba se generan en ejecución).
- Gitleaks en local con la misma imagen y orden que la CI (`detect --log-opts="--all"`, sobre **todo el historial**): sin hallazgos nuevos.
- Manual y ADR al día en el mismo bloque o en el 4, nunca desfasados al cerrar la spec.
