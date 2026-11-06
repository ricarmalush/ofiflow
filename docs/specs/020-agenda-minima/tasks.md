# Tasks — 020-agenda-minima

**Spec relacionada:** [./spec.md](./spec.md)
**ADR relacionadas:** [ADR-002](../../adr/ADR-002-multi-tenancy.md), [ADR-006](../../adr/ADR-006-persistencia-domain-events.md) (se anota: las transiciones de `Job`), [ADR-012](../../adr/ADR-012-autorizacion-rbac.md) (se anota: el permiso nuevo) y la nueva **ADR-022** (agenda: fechas en UTC, huso del navegador y qué se difiere)
**Estado general:** En curso (spec aprobada el 2026-11-03). Bloques 1 (commiteado) y 2 hechos y verificados; falta el commit del 2.

---

## Convención

- Cada tarea completable y verificable en una sesión.
- Se marca `[x]` solo cuando cumple la Definición de «Done»: compila, tests en verde, mutaciones detectadas y manual al día.
- **Nivel de riesgo de las pruebas: medio.** No hay secretos ni criptografía, pero sí **permisos nuevos y aislamiento entre empresas**, que son lo que no se puede romper. La batería completa de mutaciones se aplica a las guardas de estado, a la tabla de permisos, al rango semiabierto y al tope de 42 días; el resto lleva tests normales.
- Las mutaciones se hacen a mano sobre el código real, con copia de seguridad, con aviso si la mutación no cambió el fichero y con **`touch` al restaurar**. Los guiones de mutación van en ficheros. Solo cuenta como «detectada» una mutación con tests en rojo (`[FAIL]`); un test colgado o un error de compilación no cuentan.
- Verificación en vivo con la receta de siempre (base desechable, API real, script fuera del repositorio) y, en el frontend, `ng serve` con el navegador integrado.
- Las suites se lanzan **por proyecto, una detrás de otra**.
- **Cada bloque se puede entregar solo** y termina en verde. El 1 es el único que toca el dominio y la migración; el 2 es de solo lectura; el 3 necesita ambos.
- **Cuidado con la ruta:** `GET /jobs/agenda` convive con `GET /jobs/{id:guid}`. La restricción `:guid` hace que `agenda` no case con la segunda; un test lo fija para que nadie la quite.

---

## Bloques

### Bloque 1 — Programar y desprogramar (Domain + Application + Infrastructure + Api)
- [x] **Domain.** `Job.Schedule(DateTime utc, int? minutes)` y `Job.Unschedule()`; `Start()` acepta `New` y `Scheduled` (R8). Guarda de estado (R2, R7) y cotas (R5, R6) como constantes de Domain (`ScheduledDateMin/Max`, `EstimatedDurationMinutesMin/Max`) que usa también el validador. Una fecha que no sea UTC o fuera de cotas lanza `ArgumentOutOfRangeException` (error de programación: Application ya lo validó). **Lección:** la regla de arquitectura de la spec 006 prohíbe el texto `new ArgumentException(` en Domain; la guarda usa el estilo `ThrowIf…` de las demás.
- [x] **Errores nuevos** en `JobErrors` y en el diccionario: `job.cannot_schedule` y `job.cannot_unschedule` (argumento: estado actual), `job.schedule_date_invalid` y `job.estimated_duration_invalid`. Además se corrigió el texto de `job.cannot_start` («nuevo o programado») y un test lo fija.
- [x] **Permiso `Jobs.Schedule`** (`Permission.JobsSchedule`) concedido a `Owner`, `Admin` y `Manager` (D4); `PermissionNamesTests` y la matriz de `RolePermissionsTests` actualizadas.
- [x] **Application.** `ScheduleJobCommand(Id, string ScheduledDate, int? EstimatedDurationMinutes)` y `UnscheduleJobCommand(Id)`, con `[RequiresPermission(Permission.JobsSchedule)]`. La fecha viaja como **texto** porque `DateTimeOffset` leería una hora sin desfase con el huso del servidor: `IsoInstant` (forma exacta con `[0-9]` y `\z`, longitud acotada, cultura invariante) exige el desfase y devuelve UTC. Validador con los códigos del diccionario.
- [x] **Infrastructure.** Índice `(TenantId, ScheduledDate)` y migración **`AddJobScheduledDateIndex`** (solo el índice). **Un bug encontrado por un test contra SQL Server real:** EF devolvía la fecha con `Kind = Unspecified`, así que el JSON salía sin la «Z» y el navegador la leería como hora local. La columna `ScheduledDate` se lee ahora marcada como UTC (conversión solo de esa columna; sin migración).
- [x] **Api.** `PUT /jobs/{id}/schedule` y `DELETE /jobs/{id}/schedule` (`204`); `ScheduleJobRequest`; ejemplos en `OfiFlow.Api.http`. Una fecha sin desfase da `400` con el código del campo.
- [x] **Tests (por proyecto): Domain 300 (+37), Application 828 (+85), Infrastructure 662 (+7), Api 665 (+37).** *Domain:* cada transición legal e ilegal, `Start` desde `Scheduled` conservando la fecha, desprogramar borra fecha y duración, cotas exactas y `Kind`. *Application:* `IsoInstantTests` (formas válidas, 20 rechazos, dígitos de otros alfabetos en cada posición, cuatro culturas), validador, handlers (lo guardado se lee con otro contexto), contador de `OperationPermissionsTests` 42 → 44. *Infrastructure (SQL Server real):* la fecha vuelve en UTC y exacta, también en el DTO, el índice existe con la empresa primero, aislamiento. *Api:* 26 casos nuevos de extremo a extremo con dos empresas (la «Z» del JSON, entradas inválidas, estados que no admiten programar, 404 entre empresas, 401) y dos filas nuevas en la matriz RBAC (15 operaciones × 5 roles) más «403 antes que 400».
- [x] **Mutaciones (50): 47 detectadas y 3 equivalentes.** A las guardas de estado, efectos, cotas (exactas y desplazadas), la tabla de permisos y la declaración de los comandos, el filtro de tenant (`IgnoreQueryFilters` en los dos handlers), `IsoInstant`, la conversión a UTC, el índice y los códigos de respuesta. Los tres supervivientes son **equivalentes** aunque se reforzaron los tests: quitar el tope de longitud (la regex ya acota a 33 caracteres), abrir la regex a `\d` solo en el año (`DateTimeOffset.TryParse` rechaza los dígitos no ASCII) y leer con la cultura del servidor (el formato ISO con desfase no depende de ella, ni con tailandés, persa, hebreo o árabe saudí). Son barreras redundantes a propósito.
- [x] **Comprobación en vivo** (API real, base desechable, SQL Server real): **29 comprobaciones correctas**. Programar con `+01:00` guarda 08:00 UTC en la base y devuelve `…08:00:00Z`; fecha sin desfase y duración 1441 dan `400` con su código y no cambian nada; reprogramar sin duración la borra; desprogramar vuelve a `New`; `start` desde `Scheduled` conserva la fecha; otra empresa recibe `404` en las dos operaciones y sin token `401`; el registro no lleva títulos, clientes ni emails.
- [ ] **[manual]** Se hace en el bloque 4 (capítulos de Jobs y de tests, ADR-022), para no escribirlo dos veces.
- [ ] [externo] Commit del bloque 1

### Bloque 2 — Leer la agenda (Application + Api)
- [x] **`GetAgendaQuery(string From, string To)`** con `[RequiresPermission(Permission.JobsRead)]` y su validador. Los extremos llegan como **texto con desfase explícito** (el mismo `IsoInstant` que al programar); el final debe ser posterior al principio y el rango no puede pasar de 42 días (`GetAgendaQuery.MaxRange`). Todo fallo del rango da el código nuevo **`job.agenda_range_invalid`**, en el campo `From` o `To`; la relación entre los dos solo se evalúa si cada uno es un instante válido.
- [x] **Handler:** trabajos con `ScheduledDate` en **[from, to)**, estado `Scheduled`/`InProgress`/`Completed`, ordenados por fecha y por `Id` (desempate estable), mismo `JobDto`. Un `New` nunca tiene fecha, así que el filtro de rango ya lo excluye.
- [x] **`GET /api/v1/jobs/agenda?from=&to=`** con su documentación y ejemplos en `OfiFlow.Api.http`. La ruta no choca con `/{id:guid}` (un test lo fija). **Un `+` sin codificar en la URL** llega como un espacio y se rechaza con `400`: el cliente debe escribir `%2B` (documentado en el `.http` y fijado por un test).
- [x] **Tests (por proyecto): Domain 300, Application 861 (+33), Infrastructure 668 (+6), Api 688 (+23).** *Application:* validador (21 casos: extremos, tope exacto de 42 días y un segundo más, final igual o anterior, desfases distintos), handler (borde de entrada y de salida, estados, orden, doce trabajos en desorden, desempate, DTO, desfases); contador de `OperationPermissionsTests` 44 → 45. *Infrastructure (SQL Server real):* aislamiento entre empresas con los mismos instantes, bordes en la base de datos, estados, fechas en UTC, y el **plan de la consulta real** (ejecutada como EF la ejecuta, con la caché de planes vacía): `Index Seek` sobre `IX_Jobs_TenantId_ScheduledDate`. *Api:* 18 casos nuevos de extremo a extremo con dos empresas (la «Z», el orden, los bordes, los estados, un día de Madrid con su desfase, los 400 de campo, el `+` sin codificar, la ruta, el 401) y una fila nueva en la matriz RBAC (16 operaciones × 5 roles: los cinco roles leen la agenda).
- [x] **Mutaciones (33): 32 detectadas y 1 equivalente.** Al rango (`>`/`>=`, `<`/`<=`, filtros quitados, extremos intercambiados), a los estados (cada uno quitado o añadido), al orden (por fecha, descendente, sin desempate), al filtro de tenant (`IgnoreQueryFilters`), al DTO, a la validación (cada extremo, final igual o anterior, sin tope, 41/43/exclusivo, relación no evaluada, código), a los permisos, al endpoint (parámetros ignorados, 204) y al índice. **El equivalente:** incluir `New` en los estados, porque un `New` nunca tiene fecha y el filtro de rango ya lo excluye. **Una lección:** «sin orden por fecha» sobrevivió la primera vez **por azar** (con tres trabajos, el orden por `Id` aleatorio coincide con el de fechas una de cada seis veces, en cada test de orden); se añadió un test con doce trabajos insertados en desorden (coincidir por azar: 1 entre 479 millones) y la mutación pasó a detectarse siempre.
- [x] **Comprobación en vivo** (API real, base desechable, SQL Server real): **25 comprobaciones correctas**. Una semana con trabajos programados, en curso, completado, cancelado, sin fecha y en los dos bordes; sale lo que toca y en orden, con la «Z»; el rango de Madrid (`%2B01:00`) incluye el trabajo de las 23:00Z del domingo (las 00:00 del lunes en Madrid); seis rangos inválidos dan `400` con `job.agenda_range_invalid`; 42 días justos dan `200`; la otra empresa ve su agenda vacía; sin token `401`; el trabajo por Id sigue funcionando; el registro no lleva títulos, clientes ni emails. *Mi guion se equivocó dos veces en sus expectativas (el borde de las 00:00 de Madrid); la API acertó.*
- [ ] **[manual]** Se hace en el bloque 4.
- [ ] [externo] Commit del bloque 2

### Bloque 3 — Frontend (`ofiflow-frontend`)
- [ ] **Modelo y servicio.** `Job` ya trae `scheduledDate` y `estimatedDurationMinutes`; `JobService.schedule(id, date, minutes?)`, `unschedule(id)` y `agenda(from, to)`; `canStart` incluye `Scheduled` (F6); permiso `Jobs.Schedule` en `permission.model.ts`.
- [ ] **Utilidad de semana** (`week-range.util.ts`): lunes 00:00 → lunes 00:00 siguiente **construidos con componentes de fecha locales**, nunca sumando 168 h (F3); agrupar por día local; «Vencido» (F4). Sin dependencias.
- [ ] **Pantalla `/agenda`** (carga perezosa, permiso `Jobs.Read`, entrada en el menú «Gestión»): rejilla de siete columnas (apiladas en móvil), «Anterior / Hoy / Siguiente», «Solo mis trabajos», estado y «Vencido»; nombres de cliente y asignado resueltos como en la lista; tarjeta enlazada solo con `Jobs.Write` (F8). Estado de carga, vacío y error con «Reintentar».
- [ ] **Formulario del trabajo:** sección «Programación» (F5) con selector de fecha y hora, duración, «Programar/Reprogramar» y «Quitar de la agenda»; desactivada y explicada en los estados que no la admiten; el mensaje de la API se enseña y lo escrito se conserva.
- [ ] **Lista de trabajos:** columna «Programado» ordenable y «Vencido»; «Iniciar» también en `Scheduled`.
- [ ] **Tests (specs):** utilidad de semana (incluida la **semana del cambio de hora**, construida con fechas locales para que no dependa del huso de la máquina), servicio, la pantalla (semana anterior/siguiente/hoy, agrupación por día, `Technician` sin acciones, «solo mis trabajos», errores), la sección del formulario (cada estado, el envío en ISO con `Z`, el rechazo de la API), la columna de la lista y `canStart`. Los specs de pantallas sustituyen el configurador de tema por un doble vacío.
- [ ] **Mutaciones:** sumar 7×24 h en vez de componentes locales, el rango fuera de [from, to), la fecha enviada sin convertir a UTC, «Vencido» en un trabajo completado, la entrada del menú sin permiso, `canStart` sin `Scheduled`.
- [ ] **Comprobación en vivo** (navegador integrado, API real): programar desde el formulario, verlo en la agenda, reprogramar, quitarlo, iniciar desde «Programado», entrar como `Technician` y no ver acciones; una pantalla estrecha.
- [ ] [externo] Commit del bloque 3 (en `ofiflow-frontend`)

### Bloque 4 — Documentación
- [ ] **ADR-022** (`docs/adr/ADR-022-agenda.md`): fechas como instante UTC con desfase obligatorio y por qué no la «hora de pared» (D2), qué día es según el navegador y su límite, el permiso nuevo (D4), fechas pasadas permitidas (D5), sin conflictos (D6), sin `RowVersion` (D8), sin librería de calendario (D9), lo que cambió al implementar, riesgos aceptados y lo que queda abierto.
- [ ] Anotaciones «Ampliada por ADR-022» en **ADR-006** (transiciones de `Job`) y **ADR-012** (`Jobs.Schedule`); `docs/README.md` y el roadmap del `README.md` (fase 2: agenda mínima hecha, conflictos pendientes).
- [ ] Manual: el capítulo de Jobs (programar, estados, agenda), el de tests (cifras reales, mutaciones, lecciones) y el glosario; el anexo con la tabla de permisos y los errores nuevos.
- [ ] `spec.md` pasa a «Implementada» y este `tasks.md` queda completo, con la Definición de Done al día.
- [ ] [externo] Commit del bloque 4 y de `tasks.md`
- [ ] [externo] Push de las dos ramas y PR (backend y frontend), con tu orden

---

## Definición de Done (por bloque)

- Compila sin avisos nuevos y los tests de **todos los proyectos afectados** pasan, lanzados por proyecto.
- Cada regla de la spec tiene al menos un test que falla si se rompe; las guardas, la tabla de permisos, el rango y el aislamiento, además, su mutación detectada.
- Comprobado en vivo contra la API real y, en el bloque 3, en el navegador.
- Ningún secreto, fecha personal ni dato de clientes en logs, ejemplos ni tests.
- Manual y ADR al día en el mismo bloque o en el 4, nunca desfasados al cerrar la spec.

## Notas / bloqueos

- **Nada empieza hasta que lo ordenes.** Esta spec y este `tasks.md` están sin commitear en la rama `spec/020-agenda-minima`.
- La migración `AddJobScheduledDateIndex` se suma a las pendientes de aplicar (specs 016 y 018) antes de un despliegue real.
- Orden de PR sugerido: backend (bloques 1, 2 y 4) y después frontend (bloque 3); el frontend no sirve sin el backend y el backend es compatible hacia atrás (ninguna operación existente cambia, salvo que `Start` acepta un estado más).
