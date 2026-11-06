# ADR-022 — La agenda: fechas como instante UTC con desfase obligatorio, el día lo decide el navegador, y qué se difiere

**Estado:** Aceptado (2026-11-06). Las decisiones D1–D10 de la [spec 020](../specs/020-agenda-minima/spec.md) se aprobaron tal como estaban escritas; los cambios que se hicieron al implementar están en [«Lo que cambió al implementar»](#lo-que-cambió-al-implementar).
**Fecha:** 2026-11-06
**Proyecto:** OfiFlow

---

## Contexto

Hasta ahora un trabajo se creaba, se asignaba y se ejecutaba, pero **no se podía planificar**: `ScheduledDate` y `EstimatedDurationMinutes` existían en el modelo y en el DTO y ninguna operación los escribía, de modo que los estados `Scheduled` y `Pending` eran inalcanzables. Sin agenda, OfiFlow es una lista de tareas; con ella empieza a ser lo que promete el documento maestro: «cliente → trabajo → **cita** → presupuesto → factura → cobro». Es la fase 2 del roadmap.

Una agenda parece un formulario con una fecha, pero **una fecha es el dato más fácil de equivocar sin darse cuenta**: una cita a las 09:00 que se guarda a las 08:00 o a las 10:00 no falla ningún test que no la mire, y el cliente aparece en la puerta a la hora equivocada. Este ADR recoge cómo se evita, y qué se deja fuera a propósito.

## Problema

- ¿Qué es la fecha de un trabajo: una «hora de pared» (09:00) o un **instante**? ¿Y cómo viaja entre el navegador, la API y la base de datos sin que nadie la reinterprete?
- ¿Qué pasa con una fecha que llega **sin** indicar su huso horario?
- ¿Quién decide qué es «hoy» y «esta semana»: el servidor o el navegador? ¿Qué pasa la semana en que cambia la hora?
- ¿Qué permiso hace falta para programar? ¿Es lo mismo que asignar?
- ¿Se rechazan las fechas pasadas? ¿Se detectan conflictos de horario?
- ¿Cómo se lee una agenda sin que una sola petición pueda pedir toda la historia de una empresa?

## Opciones consideradas

### A. Qué es la fecha

**A1. Un instante en UTC, con su desfase explícito al enviarlo.** *(elegida)* `2026-11-10T09:00:00+01:00` (o terminado en `Z`) es un momento exacto del tiempo. La API lo convierte a UTC, lo guarda como `datetime2` y lo devuelve **siempre en UTC con la `Z`**. Comparar, ordenar y pedir rangos son operaciones sobre instantes y no dependen de ningún huso.

**A2. La «hora de pared» sin huso (`2026-11-10T09:00`).** Es lo que la persona escribe y lo que quiere ver, pero **no es un momento**: el mismo texto significa instantes distintos en Madrid y en Bogotá, y no existe (o existe dos veces) la noche en que cambia la hora. Se descarta: guardar horas de pared es la receta de los errores de una hora que solo aparecen dos veces al año.

### B. Una fecha sin desfase

**B1. Se rechaza con un `400` de campo.** *(elegida)* La API no sabe en qué huso la escribió quien la mandó, y adivinarlo es peor que pedir el dato completo.

**B2. Se interpreta con el huso del servidor.** Es lo que hace `DateTimeOffset.TryParse` por defecto con un texto sin desfase, y **es un error silencioso**: la cita queda a la hora del servidor, no a la de quien la escribió, y nadie lo nota hasta que falla. Se descarta, y por eso la fecha viaja como **texto** que `IsoInstant` valida antes de leer (una forma exacta, con `Z` o `±hh:mm`).

### C. Quién decide «hoy» y «esta semana»

**C1. El navegador, con su huso.** *(elegida)* La agenda pide a la API el intervalo `[from, to)` de la semana como instantes UTC, calculados a partir de la **medianoche local** del lunes y la del lunes siguiente. «Qué día es» es una cuestión de quien mira, no del servidor.

**C2. El servidor, con un huso por empresa.** Es lo correcto a largo plazo (una empresa con personas en husos distintos vería la misma agenda), pero exige guardar un huso por empresa, una pantalla para elegirlo y decidir qué hace una persona que viaja. Se difiere (NEXT).

### D. Los extremos de una semana

**D1. Componentes de fecha locales** (`new Date(año, mes, día + n)`). *(elegida)* Cada día es medianoche local, tenga las horas que tenga.

**D2. Sumar 24 h por día.** Es la tentación obvia y es **incorrecta**: la semana en que cambia la hora dura 167 o 169 horas, y sumar 168 deja el final de semana a las 23:00 o a las 01:00 en vez de a medianoche, con un día entero descuadrado.

### E. El permiso

**E1. Un permiso nuevo, `Jobs.Schedule`, para `Owner`, `Admin` y `Manager`.** *(elegida)* Programar compromete el tiempo de una persona y la cita con un cliente: se parece a asignar, no a editar el título. Hoy lo tienen los mismos roles que `Jobs.Assign`, pero son dos acciones que pueden separarse mañana. Leer la agenda es `Jobs.Read`, que tienen los cinco roles.

**E2. Reutilizar `Jobs.Assign`.** Más simple, pero acopla dos decisiones. **E3. Dárselo también a `Employee`** (el personal de oficina suele dar las citas): se descarta por ahora y cambiarlo después es editar una tabla (ADR-012).

### F. Lo que se difiere

- **Fechas pasadas: se admiten.** Se registran trabajos ya hechos y se corrige una fecha tarde. Lo que importa se ve en pantalla («Vencido»: un trabajo que sigue programado cuando su hora pasó). Menos reglas, menos casos raros (p. ej. no poder corregir la duración de un trabajo de ayer).
- **Conflictos de horario: no se detectan.** Es la regla más tentadora y la más ambigua (¿mismo técnico?, ¿sin asignar?, ¿solape parcial?, ¿bloquea o avisa?). La vista de semana ya enseña los solapes.
- **Concurrencia: gana la última escritura.** `Job` no tiene `RowVersion`; añadirlo toca todas las operaciones del trabajo y su frontend. Con equipos pequeños el riesgo es bajo.
- **Calendario: sin librería.** Una rejilla de columnas con tarjetas; una librería (p. ej. FullCalendar) añade una dependencia grande y su seguridad que mantener para una función que no se usaría entera.

## Decisión

### 1. La fecha

- **Programar:** `PUT /api/v1/jobs/{id}/schedule` con `{ scheduledDate, estimatedDurationMinutes? }`. Solo desde `New` o `Scheduled` (reprogramar); pasa a **`Scheduled`**. **Sustituye la programación entera**: una duración omitida borra la anterior. Responde `204`.
- **Quitar de la agenda:** `DELETE /api/v1/jobs/{id}/schedule`. Solo desde `Scheduled`; vuelve a `New` y se borran fecha y duración.
- **Iniciar:** `Start()` acepta `New` **o** `Scheduled` y conserva la fecha (es lo que se planeó; sirve de historial). Un trabajo sin programar sigue pudiéndose iniciar: muchos servicios no se planifican. `Complete()` y `Cancel()` no cambian; cancelar conserva la fecha, pero el trabajo deja de salir en la agenda.
- **La fecha es texto con desfase explícito** y la lee [`IsoInstant`](../../src/OfiFlow.Application/Common/Validation/IsoInstant.cs): forma exacta (`[0-9]`, no `\d`, que en .NET acepta dígitos de otros alfabetos; `\z`, no `$`, que acepta un salto de línea final), longitud acotada (40) y cultura invariante. Cotas de cordura: del año 2000 al 2099 en UTC. Duración: entero de 1 a 1440 minutos. Las cotas viven en Domain y el validador usa las mismas constantes.
- **SQL Server guarda un `datetime2` sin zona y EF lo devuelve como `Kind = Unspecified`**: sin más, el JSON saldría sin la `Z` y el navegador lo leería como hora local. La columna `ScheduledDate` se lee marcada como UTC (conversión solo de esa columna; ninguna migración). Lo encontró un test contra SQL Server real, no el razonamiento.

### 2. La agenda

- `GET /api/v1/jobs/agenda?from=&to=` devuelve los trabajos con fecha en **[from, to)** y estado `Scheduled`, `InProgress` o `Completed`, ordenados por fecha y, a igualdad, por `Id`. El final no entra (un trabajo a las 00:00 exactas del lunes siguiente es de la semana siguiente). Los cancelados y los nuevos (sin fecha) no salen.
- Los dos extremos son obligatorios, con desfase explícito; el final es posterior al principio y el rango **no pasa de 42 días** (seis semanas, una vista de mes): acota lo que una sola petición puede devolver. Cualquier fallo es un `400` con `job.agenda_range_invalid`.
- **Ruta:** `agenda` no choca con `/{id:guid}` porque la restricción `:guid` solo casa con un Guid; un test lo fija.
- **Un `+` sin codificar en la URL** llega al servidor como un espacio y se rechaza; el cliente debe enviar `%2B`. El frontend no lo sufre porque envía siempre la `Z` (`toISOString()`).
- Índice `(TenantId, ScheduledDate)`: la empresa primero, que es por lo que filtra siempre el filtro global, y la fecha después, para el rango.

### 3. El frontend

- **Pantalla `/agenda`:** una semana de lunes a domingo en la hora del navegador; cada trabajo en su **día local**; «Anterior / Hoy / Siguiente»; «Solo mis trabajos»; «Vencido».
- **Formulario del trabajo:** sección «Programación» con `<input type="datetime-local">` nativo (la hora de pared del navegador, que se convierte a UTC con la `Z` al enviar), solo con `Jobs.Schedule`.
- El día de una fecha se calcula con los **componentes locales**, nunca sumando horas (opción D1).

### 4. Qué no hace falta registrar

No hay eventos de seguridad nuevos: programar no es una operación de seguridad, y los accesos denegados (`403`) ya los registra el comportamiento de autorización. Ni el título, ni el cliente, ni las fechas aparecen en el registro (comprobado en vivo).

## Consecuencias

### Positivas

- **La fecha no se reinterpreta nunca:** entra con su desfase, se guarda en UTC y sale en UTC con la `Z`. Una hora sin desfase no se acepta.
- **Una semana con cambio de hora sigue siendo una semana:** los extremos son medianoche local, no 168 horas.
- **La agenda es barata y está acotada:** un rango obligatorio, de hasta 42 días, sobre un índice que empieza por la empresa. Comprobado: la consulta real hace *Index Seek* sobre el índice con una tabla grande.
- **Programar es una acción con su propio permiso**, separable de asignar.
- **Backend compatible hacia atrás:** ninguna operación existente cambia, salvo que `Start` acepta un estado más.

### Negativas / riesgos aceptados

- **El «día» lo decide el navegador.** Una empresa con personas en husos distintos ve el mismo instante a horas distintas (correcto) pero puede repartirlo en días distintos. Un huso por empresa es NEXT.
- **Sin detección de conflictos:** dos trabajos del mismo técnico a la vez se aceptan. La vista de semana los deja ver, pero nada avisa.
- **Gana la última escritura:** si dos personas programan el mismo trabajo a la vez, vale la última. Sin `RowVersion` en `Job`.
- **Una fecha pasada se acepta:** un error de año (2025 en vez de 2026) no lo para la API; la pantalla lo marcará como vencido.
- **Un trabajo de varios días o recurrente no se puede expresar:** un trabajo es un único intervalo de hasta 24 h.
- **Un técnico ve la agenda de toda la empresa**, no solo la suya (el mismo límite de ADR-012: `Jobs.Read` es de la empresa). «Solo mis trabajos» es un filtro de la pantalla, no una restricción.
- **`Pending` sigue inalcanzable:** un estado sin regla de negocio que lo use sería un estado muerto.
- **La fecha viaja como texto** y por eso el comando lleva un `string`; el validador y el handler la leen con el mismo `IsoInstant`.

### Lo que cambió al implementar

1. **La conversión a UTC al leer.** La spec no lo preveía: el primer test contra SQL Server real mostró que EF devolvía `Kind = Unspecified` y el JSON saldría sin la `Z`. Se añadió la conversión de esa columna.
2. **El plan de ejecución se mide como se ejecuta de verdad.** Mi primer test del plan usaba `ToQueryString()`, que genera variables locales (`DECLARE`) que SQL Server **no inspecciona**, sobre una tabla pequeña; salía un recorrido de tabla. Se rehízo ejecutando la consulta real de EF con la caché de planes vacía y leyendo el plan en caché. Con pocas filas SQL Server hace bien en recorrer la tabla (el índice no es de cobertura), así que el test usa una tabla grande y un rango de un día.
3. **`IsoInstant` es una clase propia** y no un `DateTimeOffset` en el comando: `System.Text.Json` lee un `DateTimeOffset` sin desfase con el huso local del servidor, justo lo que se quería evitar.
4. **La regla de arquitectura de la spec 006 frenó una línea:** prohíbe el texto `new ArgumentException(` en Domain; la guarda de «fecha no UTC» usa el estilo `ThrowIf…` de las demás.
5. **Un `+` sin codificar** se documentó y se fijó con un test en vez de aceptarlo «arreglándolo».
6. **La rejilla del frontend:** siete columnas eran ilegibles a 1400 px con la barra lateral (se vio al comprobarlo en el navegador); ahora son 1, 2 o 4 columnas y 7 solo en pantallas muy anchas.

### Hallazgos de la implementación (útiles para el tribunal)

- **Una mutación sobrevivió por azar.** «Ordenar por `Id` en vez de por fecha» pasó los tests la primera vez porque los `Id` son aleatorios y, con tres trabajos, el orden por `Id` coincide con el de fechas una vez de cada seis, en cada test de orden. Un test con doce trabajos insertados en desorden (coincidir por azar: 1 entre 479 millones) la detecta siempre. **Un test cuya detección depende de la suerte no demuestra nada.**
- **El compilador también «detecta»... pero no cuenta.** En TypeScript 5.5, mutar `canStart` lo convertía en un *type guard* inferido y el spec dejaba de compilar en vez de fallar. Un test que no compila no está en rojo: los specs reciben la regla como `(s) => boolean`.
- **Un día UTC no se distingue del local con un trabajo a las 23:30 en una zona UTC+1**; hace falta uno a las 00:30. Y el test de la semana del cambio de hora solo se ejecuta en una zona con horario de verano (queda `pending` en otra).
- **Mis expectativas también se equivocan.** Dos veces el guion de comprobación dio por mal un resultado correcto: las 23:00Z del domingo **son** las 00:00 del lunes en Madrid. La API acertó.
- **Un defecto de diseño que ningún test ve:** las siete columnas. Solo se vio mirando la pantalla.

### Recomendación de despliegue (no es código)

1. **Aplicar la migración `AddJobScheduledDateIndex`** (solo crea un índice) junto con las pendientes de las specs 016 y 018.
2. **`AUTO_UPDATE_STATISTICS_ASYNC ON`** en la base de datos (ya recomendado en la spec 016): sin estadísticas al día el plan de la agenda puede elegir mal.
3. **Una agenda real con datos reales** antes de darla por buena: dos personas, dos husos si se puede, y la semana en que cambia la hora.

### Qué queda cerrado (no se debe reabrir sin una razón nueva y explícita)

- **La fecha entra con su desfase, se guarda en UTC y sale en UTC con la `Z`.** Una fecha sin desfase se rechaza.
- **Los extremos de una semana son medianoche local, nunca 168 horas.**
- **Programar tiene su propio permiso** (`Jobs.Schedule`).
- **La agenda es un rango obligatorio y acotado.**
- **Ni títulos, ni clientes, ni fechas en los registros.**

### Qué queda abierto para revisar más adelante

- **Detección de conflictos de horario** y carga por técnico.
- **Huso horario por empresa.**
- **Panel «Por programar»**, vista de mes, vista de día por horas, arrastrar y soltar.
- **El estado `Pending`** con su regla de negocio.
- **`RowVersion` en `Job`** (concurrencia).
- **Tarjeta «Hoy» en el dashboard**, recordatorios por correo o WhatsApp (mensajería: fases 7 y 8).
- **Trabajos de varios días o recurrentes.**
- **Que un técnico solo vea su agenda.**
- **Sincronizar con Google Calendar / Outlook** (iCal) y agenda del cliente.

---

## Relación con otros ADR

- Amplía: **ADR-006** (el estado de `Job` como enum con métodos de transición: ahora `Schedule`, `Unschedule` y un `Start` más amplio; `Pending` sigue sin transición) y **ADR-012** (un permiso más, `Jobs.Schedule`).
- Relacionada con: ADR-002 (el filtro de empresa cubre la consulta de la agenda y el índice lleva `TenantId` primero), ADR-009 (entradas acotadas: fechas, duración y rango), ADR-011 (cinco códigos nuevos en el diccionario: `job.cannot_schedule`, `job.cannot_unschedule`, `job.schedule_date_invalid`, `job.estimated_duration_invalid` y `job.agenda_range_invalid`) y ADR-008 (el plan de ejecución y la fecha en UTC se comprueban contra SQL Server real).
- Prepara: los presupuestos y las facturas (fases 3 y 4), que cuelgan de un trabajo ya planificado, y los recordatorios por mensajería, que necesitan una fecha de cita.
