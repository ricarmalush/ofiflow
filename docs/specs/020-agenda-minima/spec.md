# Spec — 020-agenda-minima

**Estado:** Implementada (2026-11-06). Decisiones D1–D10 aprobadas el 2026-11-03 tal como estaban escritas
**Fecha:** 2026-11-03
**Bounded Context:** Jobs (el contexto `Scheduling` del documento maestro, §7, empieza aquí dentro de Jobs: no hay todavía reglas que justifiquen separarlo)
**Depende de:** spec 003 (el `Job` y su ciclo de vida), spec 008 y ADR-012 (permisos por rol), spec 012 (`GET /account/me`: el Id de mi pertenencia), ADR-006 (el estado como enum con métodos de transición), ADR-002 (aislamiento por empresa)

> **Numeración:** se llama 020 y no 019 porque la documentación ya reserva la 019 para «cambiar la contraseña estando dentro» (ADR-021, spec 018, manual). Si se prefiere otra, solo hay que renombrar la carpeta antes del primer commit.

---

## Objetivo

Que quien gestiona una empresa pueda **poner fecha y hora a un trabajo y ver qué toca hoy y esta semana**.

Hoy un trabajo se crea, se asigna y se ejecuta, pero **no se puede planificar**: `ScheduledDate` y `EstimatedDurationMinutes` existen en el modelo y en el DTO, y ninguna operación los escribe, así que los estados `Scheduled` y `Pending` son inalcanzables. Sin agenda, OfiFlow es una lista de tareas; con ella empieza a ser lo que promete el documento maestro: «cliente → trabajo → **cita** → presupuesto → factura → cobro». Es la fase 2 del roadmap y el «agenda básica» del CORE MVP (§66).

## Alcance — Qué SÍ incluye

Cuatro bloques:

1. **Programar:** `PUT /jobs/{id}/schedule` y `DELETE /jobs/{id}/schedule`, el permiso nuevo `Jobs.Schedule`, las transiciones del `Job` (`New → Scheduled`, reprogramar, desprogramar, `Scheduled → InProgress`) y el índice de la fecha.
2. **Leer la agenda:** `GET /jobs/agenda?from=&to=`, los trabajos con fecha dentro de un rango.
3. **Frontend:** la pantalla «Agenda» (vista de semana), la sección «Programación» en el formulario del trabajo, la columna de fecha en la lista y el «Iniciar» para un trabajo programado.
4. **Documentación:** ADR-022 (fechas en UTC, huso del navegador y qué se difiere), anotaciones en ADR-006 y ADR-012, el manual y el roadmap.

## Alcance — Qué NO incluye

- **NEXT:** **detección de conflictos de horario** (dos trabajos del mismo técnico a la vez) y vista de carga por técnico. Aquí se ven solapados en la semana, pero no se avisa ni se impide (D6).
- **NEXT:** **panel «Por programar»** en la agenda (los trabajos `New` sin fecha, para programarlos desde ahí). Hoy se programa desde el formulario del trabajo.
- **NEXT:** **arrastrar y soltar**, vista de mes y vista de día por horas.
- **NEXT:** el estado **`Pending`** («esperando algo»: material, aprobación del cliente). Sigue inalcanzable a propósito: sin una regla de negocio que lo use sería un estado muerto.
- **NEXT:** **huso horario por empresa** (D2). Aquí manda el del navegador.
- **NEXT:** tarjeta «Hoy» en el dashboard; recordatorios y avisos por correo al cliente o al técnico (cita → mensajería, fases 7 y 8).
- **NEXT:** trabajos de **varios días** o recurrentes; hoy un trabajo es un único intervalo de hasta 24 h.
- **LATER:** sincronizar con Google Calendar / Outlook (iCal); agenda del cliente (portal).
- **NEVER:** que el sistema **reasigne o reprograme solo** sin que nadie lo confirme (el documento maestro, §26, exige humano en el circuito para lo que comprometa a un cliente).

## Reglas de negocio / invariantes

**Programar y desprogramar**

- **R1.** `PUT /api/v1/jobs/{id}/schedule` con `{ scheduledDate, estimatedDurationMinutes? }` fija cuándo empieza el trabajo y cuánto se espera que dure. Exige el permiso `Jobs.Schedule` (R12). Responde `204`.
- **R2.** Solo se programa un trabajo en estado **`New`** o **`Scheduled`** (reprogramar). En cualquier otro estado responde `400 job.cannot_schedule`, con el estado actual como argumento, igual que `job.cannot_start`.
- **R3.** Tras programar, el estado es **`Scheduled`**. `PUT` **sustituye** la programación entera: si se omite la duración, queda sin duración (no se conserva la anterior).
- **R4.** `scheduledDate` es **un instante con su desfase explícito** (`2026-11-10T09:00:00+01:00` o `…Z`). Se guarda en **UTC**. Una fecha **sin desfase** se rechaza con `400` (no se adivina el huso del cliente: un error de una hora en una cita es peor que pedir el dato completo).
- **R5.** Cotas de cordura: `scheduledDate` entre `2000-01-01` y `2100-01-01` (UTC). Fuera de ellas, `400`. **No se rechaza una fecha pasada** (D5).
- **R6.** `estimatedDurationMinutes`, si se informa, es un entero de **1 a 1440** (un día). Fuera de eso, `400`.
- **R7.** `DELETE /api/v1/jobs/{id}/schedule` quita la programación: el trabajo vuelve a **`New`** y se borran **fecha y duración**. Solo desde `Scheduled`; en otro estado, `400 job.cannot_unschedule`. Exige `Jobs.Schedule`. Responde `204`.
- **R8.** **`Start()` se amplía:** se puede iniciar un trabajo **`New`** o **`Scheduled`** (hoy solo `New`). Un trabajo sin programar sigue pudiéndose iniciar directamente: muchos servicios no se planifican. Al iniciar, la **fecha se conserva** (es lo que se planeó; sirve de historial).
- **R9.** `Complete()` y `Cancel()` **no cambian**. Cancelar un trabajo programado conserva su fecha, pero deja de salir en la agenda (R11).
- **R10.** Programar **no exige** que el trabajo esté asignado, y asignar **no exige** que esté programado: son dos decisiones independientes (`AssignJob` no cambia). `UpdateJob` tampoco toca la programación.

**Leer la agenda**

- **R11.** `GET /api/v1/jobs/agenda?from=&to=` devuelve los trabajos de la empresa cuya `ScheduledDate` está en **[from, to)** (intervalo semiabierto: un trabajo a las 00:00 exactas del día siguiente es del día siguiente) y cuyo estado es **`Scheduled`, `InProgress` o `Completed`**, ordenados por fecha y, a igualdad, por `Id` (orden estable). Los `Cancelled` no salen. Mismo `JobDto` que el resto. Exige `Jobs.Read`.
  - `from` y `to` son **obligatorios** y llevan desfase explícito (R4).
  - `to` > `from`, y `to − from` **≤ 42 días** (seis semanas: una vista de mes). Si no, `400`. Acota lo que una sola petición puede devolver.
  - Sin paginación en esta spec: el intervalo acotado y la empresa única lo hacen pequeño. Se revisa con datos reales.

**Permisos y aislamiento**

- **R12.** Permiso nuevo **`Jobs.Schedule`**, concedido a **`Owner`, `Admin` y `Manager`** (los mismos que `Jobs.Assign`). `Employee` y `Technician` ven la agenda pero no la mueven (D4).
- **R13.** Todo es **de una empresa**: la consulta pasa por el filtro global de tenant. Un `Id` de trabajo de otra empresa da el mismo `404 job.not_found` que uno inexistente.
- **R14.** `Job` **no tiene control de concurrencia** (no hay `RowVersion`): si dos personas programan el mismo trabajo a la vez, **gana la última**. Es un límite conocido y aceptado aquí (D8).

**Frontend**

- **F1.** Entrada **«Agenda»** en el menú «Gestión», visible con `Jobs.Read`, ruta `/agenda`.
- **F2.** La agenda muestra **una semana (lunes a domingo)** en el huso del navegador, con «Anterior», «Hoy» y «Siguiente». Cada día es una columna (en móvil, apiladas) con sus trabajos por hora: hora, título, cliente, asignado («Tú», el nombre o «Sin asignar») y estado. Los de un día se ven aunque se solapen.
- **F3.** El intervalo que pide al servidor es **de medianoche local a medianoche local**, construido con componentes de fecha locales, **no sumando 7 × 24 h**: la semana del cambio de hora dura 167 o 169 horas.
- **F4.** «Solo mis trabajos» también en la agenda (mismo comportamiento que en la lista). Un trabajo **programado cuya fecha ya pasó y no está completado** se marca como **«Vencido»**.
- **F5.** El formulario del trabajo gana la sección **«Programación»** (al editar, con `Jobs.Schedule`): fecha y hora, duración en minutos, «Programar» / «Reprogramar» y «Quitar de la agenda». En un estado que no admite programar, la sección se ve **desactivada y dice por qué**.
- **F6.** La lista de trabajos gana la columna **«Programado»** (ordenable) y el botón **«Iniciar»** aparece también con el trabajo en `Programado`. `canStart` del modelo se ajusta a R8.
- **F7.** La fecha viaja siempre como **ISO-8601 con `Z`** (`Date.toISOString()`) y se muestra en local, en formato español de 24 horas.
- **F8.** Cada tarjeta de la agenda lleva al formulario del trabajo si la persona tiene `Jobs.Write` (ruta existente); si no, es texto (igual que la lista).

## Modelo conceptual

```
Job (sin cambios de columnas)
- ScheduledDate: DateTime?          UTC; null mientras sea New
- EstimatedDurationMinutes: int?    1..1440
- Status                            New → Scheduled → InProgress → Completed
                                    New ───────────→ InProgress            (R8)
                                    Scheduled → New  (desprogramar, R7)
                                    cualquiera menos Completed → Cancelled (sin cambios)
                                    Pending: sigue inalcanzable (NEXT)

Job.Schedule(DateTime utc, int? minutes)   guarda de estado R2 + cotas
Job.Unschedule()                           guarda de estado R7
Job.Start()                                acepta New | Scheduled (R8)

Índice nuevo:  (TenantId, ScheduledDate)   para el rango de R11
```

## Criterios de aceptación

**Programar**
- Dado un trabajo `New`, cuando se programa para el martes a las 09:00 con 90 minutos, entonces queda `Scheduled`, con esa fecha en UTC y esa duración, y `GET /jobs/{id}` lo muestra.
- Dado un trabajo `Scheduled`, cuando se reprograma sin duración, entonces la fecha cambia y la duración anterior **desaparece** (R3).
- Dado un trabajo `InProgress`, `Completed` o `Cancelled`, cuando se intenta programar, entonces `400 job.cannot_schedule`.
- Dada una fecha **sin desfase**, otra anterior a 2000 o posterior a 2100, o una duración de 0 o de 1441, entonces `400` y el trabajo no cambia.
- Dado un trabajo `Scheduled`, cuando se desprograma, entonces vuelve a `New` **sin fecha ni duración**; y desprogramar un `New` da `400 job.cannot_unschedule`.
- Dado un trabajo `Scheduled`, cuando se inicia, entonces pasa a `InProgress` y **conserva** su fecha; y un `New` sin programar se sigue pudiendo iniciar.
- Dados `Employee` y `Technician`, cuando intentan programar o desprogramar, entonces `403`; y con `Owner`, `Admin` y `Manager` funciona.
- Dado un `Id` de trabajo de **otra empresa**, cuando se programa o desprograma, entonces `404 job.not_found`.

**Leer la agenda**
- Dados tres trabajos programados (lunes 09:00, lunes 15:00, jueves 10:00) y uno `Cancelled`, cuando se pide la semana del lunes, entonces salen los tres, ordenados, y no el cancelado.
- Dado un trabajo a las 00:00 exactas del lunes siguiente, cuando se pide la semana, entonces **no sale** (semiabierto).
- Dados trabajos `New` sin fecha, entonces no salen.
- Dado `to ≤ from`, un rango de 43 días, o `from`/`to` ausentes o sin desfase, entonces `400`.
- Dada una agenda con trabajos de dos empresas, cuando una pide la suya, entonces **solo ve los suyos**.
- Dado cualquier rol con `Jobs.Read`, cuando pide la agenda, entonces la ve (el `Technician` ve la de toda la empresa: límite de ADR-012).

**Frontend**
- Dada la agenda, cuando se pulsa «Siguiente» y «Anterior», entonces vuelve a la misma semana; y «Hoy» lleva a la que contiene la fecha actual.
- Dada la semana en que cambia la hora, entonces los trabajos de domingo y lunes **aparecen en su día** y el rango pedido va de lunes 00:00 a lunes 00:00 locales.
- Dado un `Technician`, entonces la agenda no ofrece mover nada; y dado un `Manager`, el formulario ofrece «Programar».
- Dado un trabajo `Scheduled` con fecha pasada, entonces se marca «Vencido»; y dado `Completed`, no.
- Dada una programación rechazada por la API (p. ej. el estado cambió), entonces se enseña su mensaje y el formulario conserva lo que la persona escribió.

Caso multi-tenant obligatorio: un usuario de la Empresa A **nunca** ve en su agenda un trabajo de la B, ni puede programar ni desprogramar uno de la B.

## Seguridad (ADR-009)

- **Datos personales:** ninguno nuevo. La agenda muestra título, cliente y asignado, lo mismo que la lista de trabajos. Nada de eso sale en logs.
- **Autorización:** permiso nuevo `Jobs.Schedule` para `Owner`, `Admin` y `Manager` (R12); la lectura usa `Jobs.Read`. Las tres operaciones nuevas se añaden a la tabla de `OperationPermissionsTests` (el contador pasa de 42 a 45).
- **Entrada:** fechas con desfase explícito y cotas (R4, R5); duración 1–1440 (R6); rango de la agenda ≤ 42 días (R11). No hay campos de texto nuevos.
- **Aislamiento:** no hay entidad nueva; el filtro global cubre la consulta del rango y el índice lleva `TenantId` primero. Un test con dos empresas lo comprueba contra SQL Server real.
- **SQL crudo:** ninguno.
- **Abuso:** operaciones autenticadas y baratas; la agenda está acotada por rango (R11). No hace falta límite de peticiones propio.
- **Eventos de seguridad:** ninguno nuevo. Programar no es una operación de seguridad, y los accesos denegados (`403`) ya los registra el comportamiento de autorización.

## Decisiones técnicas relevantes

- **D1. Alcance: programar + ver la semana.** *Recomendado.* Lo mínimo que convierte la lista en agenda. Conflictos, panel «por programar», arrastrar y mes se difieren (NEXT) porque cada uno es una decisión de producto que conviene tomar viendo cómo usa la agenda una empresa real.
- **D2. Fechas: instante UTC con desfase obligatorio; el huso lo pone el navegador.** *Recomendado.* La API guarda y compara solo en UTC y se niega a interpretar una hora sin desfase. «Qué día es» lo decide el navegador (F3). Límite: una empresa con personas en husos distintos ve el mismo instante a horas distintas (correcto) pero puede repartirlo en días distintos. Un huso por empresa es NEXT (ADR-022). Alternativa descartada: guardar la «hora de pared» sin huso, que se rompe con el cambio de hora y con cualquier viaje.
- **D3. Un endpoint de agenda aparte (`/jobs/agenda`) y no un filtro en `GET /jobs`.** *Recomendado.* `GET /jobs` devuelve todo y no se toca: no cambia un contrato que el frontend usa. El rango acotado y obligatorio es una garantía que un filtro opcional no daría (alguien lo omitiría y pediría todo).
- **D4. Permiso nuevo `Jobs.Schedule` (Owner, Admin, Manager).** *Recomendado, y es la decisión que más conviene revisar.* Programar compromete el tiempo de una persona y la cita con un cliente: se parece a asignar, no a editar el título. Alternativas: reutilizar `Jobs.Assign` (más simple, pero acopla dos acciones que pueden separarse mañana) o dárselo también a `Employee` (el personal de oficina suele ser quien da las citas). Cambiarlo después es editar una tabla (ADR-012) y sus tests.
- **D5. No se rechaza una fecha pasada.** *Recomendado.* Se registran trabajos ya hechos y se corrige una fecha tarde. Lo que importa se ve en pantalla («Vencido», F4). Menos reglas, menos casos raros (p. ej. no poder corregir la duración de un trabajo de ayer).
- **D6. Sin detección de conflictos.** *Recomendado.* Es la regla más tentadora y la más ambigua (¿mismo técnico?, ¿sin asignar?, ¿solape parcial?, ¿bloquea o avisa?). Se pospone hasta ver el uso real; la vista de semana ya enseña los solapes.
- **D7. `Start()` acepta `Scheduled`; el estado `Pending` sigue sin usarse.** *Recomendado.* Es la única transición de ciclo de vida que cambia. Se anota en ADR-006.
- **D8. Sin control de concurrencia en `Job` (gana la última escritura).** *Recomendado para esta spec.* Añadir `RowVersion` toca todas las operaciones del trabajo y su frontend. Con equipos pequeños el riesgo es bajo; queda anotado como límite y sería una spec propia.
- **D9. Frontend sin librería de calendario.** *Recomendado.* La vista de semana es una rejilla de siete columnas con tarjetas; una librería (p. ej. FullCalendar) añade una dependencia grande y su seguridad que mantener, para una funcionalidad que no usaríamos entera. Se reconsidera con la vista de mes o el arrastrar y soltar.
- **D10. Orden de trabajo:** programar (**bloque 1**), leer la agenda (**bloque 2**), frontend (**bloque 3**) y documentación (**bloque 4**). *Recomendado.* El bloque 1 es lo único que toca el dominio y la migración; el 2 es de solo lectura; el 3 necesita ambos; cada uno termina en verde y se puede revisar solo.
- **¿Requiere ADR nueva? Sí: ADR-022**, «Agenda: fechas en UTC, huso del navegador y qué se difiere» (D2, D5, D6, D8), con anotaciones «Ampliada por» en ADR-006 (transiciones de `Job`) y ADR-012 (permiso nuevo).

## Fuera de alcance / Backlog relacionado

- Detección de conflictos y carga por técnico.
- Panel «Por programar», vista de mes, vista de día por horas, arrastrar y soltar.
- Huso horario por empresa.
- Estado `Pending` con su regla de negocio.
- `RowVersion` en `Job`.
- Tarjeta «Hoy» en el dashboard; recordatorios por correo o WhatsApp.
- Trabajos de varios días o recurrentes.

---

## Validación antes de aprobar

> ¿Esta funcionalidad ayuda a conseguir o mantener al primer cliente?

Sí, y es la que más acerca el MVP: el flujo de valor pasa de «cliente → trabajo» a «cliente → trabajo → **cita**», y es lo que un profesional abre cada mañana para saber qué hace hoy. Sin ella, la lista de trabajos no sustituye al papel o al calendario del móvil, y sin eso no hay hábito de uso ni piloto. Es además la base de lo que viene después: el presupuesto y la factura cuelgan de un trabajo que ya se ha planificado, y los recordatorios por mensajería (fases 7 y 8) necesitan una fecha de cita.
