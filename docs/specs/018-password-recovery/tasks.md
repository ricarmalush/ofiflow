# Tasks — 018-password-recovery

**Spec relacionada:** [./spec.md](./spec.md)
**ADR relacionadas:** [ADR-007](../../adr/ADR-007-identity-refresh-token.md), [ADR-009](../../adr/ADR-009-seguridad-base.md), [ADR-014](../../adr/ADR-014-mfa-y-bloqueo-de-cuenta.md) y [ADR-020](../../adr/ADR-020-correo-saliente.md) (se anotan) y la **ADR-021** nueva (D14)
**Estado general:** Aprobada (decisiones D1–D15, 2026-11-02). Bloque 1 hecho y commiteado; bloque 2 hecho (pendiente de commit); bloques 3 a 5 sin empezar.

---

## Convención

- Cada tarea completable y verificable en una sesión.
- Se marca `[x]` solo cuando cumple la Definición de «Done»: compila, tests en verde, mutaciones detectadas y manual al día.
- Las mutaciones se hacen a mano sobre el código real, con copia de seguridad, con aviso si la mutación no cambió el fichero y con **`touch` al restaurar**. Los guiones de mutación van en ficheros (no en `node -e` con comillas dentro de bash: se rompen los escapes).
- Verificación en vivo con la receta de siempre (base desechable, API real, script fuera del repositorio) y **Mailpit** para leer los correos. Esta spec necesita además **medir el tiempo de respuesta** de `forgot-password` con y sin cuenta (D2), con muchas muestras.
- Las suites se lanzan **por proyecto, una detrás de otra** (la solución entera da fallos falsos por contención de contenedores).
- **El bloque 1 se puede entregar solo**: no cambia ningún comportamiento visible.

---

## Bloques

### Bloque 1 — La base: la tabla y la cola de correo (Domain + Application + Infrastructure)
- [x] `PasswordResetToken` en Domain.Identity (cuenta global): `Id`, `UserId`, `TokenHash`, `CreatedAt`, `ExpiresAt`, `UsedAt?`; `Create`, `IsUsableAt`, `Consume` (un solo error `password_reset.invalid` para usado, caducado o anulado) e `Invalidate` (no falla si ya estaba usado y conserva la fecha del primer uso). Caduca a la hora, **en el instante exacto**; el límite de 3 por cuenta y hora es una constante de Domain
- [x] Configuración de EF y migración **`AddPasswordResetTokens`**: índice único por `TokenHash`, índices por `(UserId, CreatedAt)` y por `ExpiresAt`, columna de versión de fila, **sin clave foránea** y sin ninguna columna que pueda llevar un email. La entidad está en la lista de entidades globales del test del modelo y su `DbSet` es interno: **la lee un solo fichero, `PasswordResetService.cs`** (lista blanca de lecturas, con su motivo)
- [x] `IEmailQueue` (Application) y `EmailQueue` (Infrastructure): un canal **acotado** de 100 con un solo lector; con la cola llena `TryEnqueue` devuelve `false` **al instante** (no espera un hueco), descarta y registra sin la dirección ni el contenido; apagada, rechaza todo. Sin persistencia
- [x] `EmailDispatchService` (segundo servicio en segundo plano) con `EmailDispatchOptions` (`Enabled`, `QueueCapacity` entre 1 y 10000, validadas al arrancar): un solo consumidor, de uno en uno; un fallo se registra con el evento nuevo **`1013`** y la **categoría** (nunca la dirección, el texto del servidor ni el contenido) y sigue con el siguiente; un error inesperado, solo por su tipo; con el correo apagado descarta en silencio; parada limpia; solo en la API (la consola de administración no lo recibe, con test). **Cambio respecto a la spec:** no lleva retraso inicial como la purga, porque sin mensajes esperar en la cola no compite con el arranque ni con las migraciones
- [x] `IEmailLinks.ResetPassword(token)`: la misma regla que `AcceptInvitation` (solo la configuración, token en el fragmento y codificado). El test de arquitectura ahora vigila el fragmento `#token=` (lo que tiene en común todo enlace con un secreto) y las dos rutas
- [x] La purga existente (`RefreshTokenPurgeService`) borra también los tokens de restablecimiento caducados hace más de 7 días, por lotes, con su evento **`1012`** (solo si borró algo, sin hashes ni Ids de cuenta) y devuelve el total de las dos tablas. **Los números de los eventos de la spec (los de pedir y restablecer) se asignan en los bloques 2 y 3**
- [x] Dar de baja a una cuenta borra sus tokens (como los del segundo factor: la tabla no tiene clave foránea), sin confirmar por sí mismo
- [x] Error nuevo `password_reset.invalid` (400) en el diccionario (el test de diccionario completo lo exige)
- [x] Tests (+19 de Domain, +82 de Infrastructure, +3 de Api): la entidad (vigencia, corte exacto, consumo, anulación, validaciones de `Create`); **SQL Server real** (esquema, sin columna de email, hash único, índices, sin clave foránea, **dos consumos a la vez: gana uno**, purga en lote con el corte exacto y el orden, el ciclo completo de las dos tablas con sus dos eventos); la cola (orden, límite, **descarta sin bloquear**, 200 escritores a la vez aceptan exactamente la capacidad, apagada, opciones); el servicio (entrega, fallos de cada categoría sin datos, error inesperado, parada limpia, apagado); el borrado de la baja; los enlaces; y el cableado (el host de la API los registra, la consola no)
- [x] Mutaciones (32, todas detectadas): el token de 2 horas, caducar en el instante exacto, un usado que sigue valiendo, consumir sin comprobar ni marcar, anular que pisa la fecha, el límite de 5, la caducidad igual a la creación; la purga con `<=`, sin corte, en orden inverso; la baja que se lleva los tokens de todas las cuentas o ninguno; el hash sin índice único, sin versión de fila o con la columna demasiado corta; el ciclo sin la segunda purga, que devuelve solo una tabla o que deja el evento sin borrar nada; la cola que dice «aceptado» estando llena, que descarta sin registrar, que ignora que está apagada o que tira el más antiguo; el servicio que registra el texto de la excepción, que envía con el correo apagado, que muere con un fallo o que no para limpio; la capacidad cero o por defecto distinta; el enlace de restablecer que apunta a la invitación; y los dos registros que podrían quitarse sin que falle un test del servicio
- [x] **Una lección del método:** la mutación de la cola que «acepta sin escribir» **colgó** el test host (un test esperaba un mensaje que nunca llegaba) y mi ejecutor, que contaba como detectada cualquier ejecución fallida, dio por buenas 12 mutaciones que ni compilaron (los procesos huérfanos bloqueaban las DLL: `MSB3027`). Se corrigió: ahora una mutación solo cuenta si hay tests en rojo, un test colgado se mata a los 90 s (`--blame-hang`) y un error de MSBuild es un error del ejecutor, no una detección
- [x] [externo] Commit del bloque 1 (`5a6f94e`)

### Bloque 2 — Pedir el restablecimiento (Application + Api)
- [x] `ForgotPasswordCommand` (anónimo) y su validador (`NotEmpty` + `ValidEmail`); `Handler`: con el correo apagado no hace nada más que avisar; si no, pide el token a `IPasswordResetService.IssueAsync` (puerto de Application, implementado en `PasswordResetService.cs`: busca la cuenta por email normalizado, la trata como inexistente si es de un operador de la plataforma, aplica el límite de 3 por cuenta y hora —cuenta los creados, usados o no—, anula los anteriores y crea el nuevo, todo con un solo `SaveChanges`); si lo emite, **encola** el correo (`TryEnqueue`, resultado ignorado). **La misma respuesta en todos los casos** (R1). Una carrera al guardar (`DbUpdateException`) se trata como «límite», no como 500
- [x] `PasswordResetEmail` (función pura, asunto fijo, 11 líneas con CRLF, solo cambia el enlace; ni nombre ni empresa)
- [x] `POST /api/v1/auth/forgot-password` (anónimo) con su política de límite por IP (5 por hora, `429` al sexto); `202` sin cuerpo y `Cache-Control: no-store`; documentación en `OfiFlow.Api.http`
- [x] Sin correo configurado: `202`, ninguna fila, `Warning` (R6); un operador de la plataforma o un email desconocido: igual que «sin cuenta»
- [x] Eventos de seguridad: **`1014`** `PasswordResetRequested` (Información; solo si la cuenta existía y el `UserId`, nunca email, token ni enlace) y **`1015`** `PasswordResetThrottled` (Warning; el pedido que el límite por cuenta frena en silencio). Los de restablecer (1016/1017) se asignan en el bloque 3
- [x] **Medición del tiempo** (en proceso, API real + SQL Server real, series alternadas): **sin suelo**, la respuesta de una cuenta que existe tardaba de mediana ~17,3 ms y la de una que no, ~4,8 ms (Δ ≈ 12,5 ms; t de Welch ≈ 54, Mann-Whitney z ≈ 24): **se distinguían sin dudar**. **Con un suelo de 250 ms** dos tandas dieron t = 1,47 / z = 1,67 y t = 0,96 / z = 1,12: indistinguibles. El suelo se aplica en el endpoint **después** del trabajo (`ResponseTimeFloor.HoldAsync`, `ForgotPassword:ResponseFloor`, 250 ms por defecto, entre 0 y 5 s, validado al arrancar). Límite honesto: si el trabajo de una cuenta que existe superase el suelo (base de datos muy lenta), esa cola volvería a delatarla; el p99 medido era ≈ 34 ms
- [x] Tests: **Application +34** (el handler con dobles: cada resultado, nada de `SendAsync`, ningún dato personal en los registros, correo apagado; la plantilla), **Infrastructure +16** contra SQL Server real (crear, solo el hash, mayúsculas, un solo vigente, el límite con el corte exacto de la hora, los usados cuentan, otras cuentas no, operadores, carrera → límite), **Api +27**: respuestas idénticas byte a byte (estado, cuerpo y cabeceras) con/sin cuenta, operador y mal formado (400), enlace y hash coinciden, `Host`/`Origin` falsos no cambian el enlace, el cuarto pedido de la hora no crea ni envía (y deja el evento 1015), `429` al sexto, sin PII en los registros, correo apagado, el suelo (con y sin él), y configuración inválida que impide arrancar; más 2 de arquitectura (el caso de uso nunca llama a `SendAsync`; el suelo va después del trabajo) y la tabla de permisos (41 operaciones)
- [x] **Comprobación en vivo** (API real, base desechable, Mailpit v1.33.0): `202` en 257 ms con cuenta y 274 ms sin ella, cabeceras idénticas, cuerpo vacío; el correo llega solo a la cuenta que existe, con el texto fijo; en la tabla hay el hash SHA-256 (base64) del token del enlace y **no** el token; caduca a los 60 minutos; pedir otra vez deja 2 filas y 1 vigente; email mal formado → `400 validation.failed`; sexto pedido → `429 rate_limit.exceeded` con `Retry-After: 3600`; el registro solo lleva el evento `1014` (existe sí/no y `UserId`), ni emails, ni token, ni enlace
- [x] Mutaciones (37, todas detectadas salvo una equivalente): el caso de uso que pide token con el correo apagado, que no encola, que encola con una cuenta inexistente, que envía a otra dirección o con otro token, que registra el email o el enlace, que trata el límite como emitido, sin validar el email o sin ser anónimo; el correo con LF, con «dos horas» o con otro asunto; el servicio con un operador que recibe token, el límite de uno de más, la ventana con el instante exacto o de dos horas, que cuenta solo los sin usar o los de todas las cuentas, que no anula el anterior o anula los de todas, la carrera que da 500, el token guardado en claro o el hash devuelto en su lugar, sin registrar; el endpoint sin suelo, con un suelo que no espera, que vale cero, que admite negativos o sin tope, sin validar al arrancar, sin límite, con 6 por hora o con ventana de un minuto, que responde 200 o que se puede guardar en caché. **Sobrevivieron 5 en la primera tanda y se reforzó el test de cada una** (el asunto literal, el registro del puerto en Infrastructure, el `Retry-After` de una hora; la mutación de la caché se aplicó mal por tener el texto dos veces). **Una es equivalente y se documenta:** quitar la normalización a mayúsculas del email en la búsqueda no cambia nada porque SQL Server compara sin distinguir mayúsculas (la misma situación que el inicio de sesión); la regla queda protegida por el test de «mayúsculas y minúsculas» solo mientras la columna no cambie de ordenación
- [ ] [externo] Commit del bloque 2 (con orden explícita)

### Bloque 3 — Restablecer (Application + Infrastructure + Api)
- [ ] `IdentityService.SetPasswordAsync`: hash con el mismo PBKDF2 y contador/bloqueo a cero, **sin confirmar** (lo confirma el Handler)
- [ ] Una variante de «revocar todos los refresh tokens» que **no confirme por sí misma**, para que cambiar la contraseña y cerrar las sesiones sean todo o nada (D6)
- [ ] `ResetPasswordCommand`, su validador (forma del token y contraseña de 8 a 128) y su `Handler`: busca por el hash, **un solo error** para los cuatro fallos (`password_reset.invalid`, R8), consume el token con control de concurrencia (R10), anula los demás, cambia la contraseña, cierra todas las sesiones, reinicia el bloqueo, encola el aviso (R12) — en una transacción
- [ ] `PasswordChangedEmail` (plantilla fija) y error nuevo `password_reset.invalid` (400) en el diccionario (ADR-011)
- [ ] `POST /api/v1/auth/reset-password` (anónimo) con su política de límite por IP (10 por hora); `204`
- [ ] Eventos de seguridad (correcto, con las sesiones cerradas; rechazado, con el motivo real) sin token ni contraseña
- [ ] Tests: cada fallo del token da el mismo error y el motivo real queda en el registro; **dos restablecimientos a la vez con el mismo token: gana uno** (SQL Server real); entra con la nueva y no con la vieja; las sesiones y sus access tokens mueren; una cuenta bloqueada entra; **una cuenta con segundo factor sigue pidiendo el código**; una empresa suspendida sigue sin entrar; todo o nada si falla a mitad; ni contraseña ni token en ningún log
- [ ] Mutaciones: no consumir el token; no cerrar sesiones; no reiniciar el bloqueo; saltarse el segundo factor; distinguir los cuatro fallos; confirmar por separado; la contraseña en claro en el evento; etc.
- [ ] Verificación contra la API publicada de verdad con Mailpit: pedir, leer el correo, restablecer, entrar; la sesión vieja da `401`; el token usado da `400`; un pedido sin cuenta da la misma respuesta; el aviso de cambio llega; los logs sin datos personales

### Bloque 4 — Frontend (`ofiflow-frontend`)
- [ ] Enlace «¿Has olvidado tu contraseña?» en el login; pantalla de pedir (siempre el mismo mensaje, sea cual sea la respuesta) y pantalla de restablecer (el token se lee del fragmento y se borra de la barra de direcciones; dos campos; errores de validación)
- [ ] Modelo, servicio y rutas; tras el éxito, al login con un aviso; un `400 password_reset.invalid` con el mensaje de la API y un enlace para pedir otro
- [ ] Tests y mutaciones; comprobación en vivo con la API real y Mailpit
- [ ] [externo] Commit del frontend (con orden explícita)

### Bloque 5 — Documentación
- [ ] ADR-021: el modelo de amenazas de la recuperación, la cola en memoria (y por qué no la persistida), el tiempo medido, las decisiones y lo abierto
- [ ] ADR-007, ADR-009, ADR-014 y ADR-020: «Ampliada por ADR-021»; los NEXT de «recuperar contraseña» tachados
- [ ] Manual: capítulos 1 (probar la recuperación con Mailpit), 6 (el flujo y sus límites) y 8 (límites, eventos y mapa de amenazas), 9 (tests, con cifras reales), glosario y preguntas del tribunal; `docs/README.md` con la fila de la spec y la ADR
- [ ] Retocar el `tasks.md` de la spec 017: el hallazgo de `Email.Create` ya está corregido (PR #57)

---

## Definición de «Done»

- Compila sin avisos nuevos y todos los tests en verde (las cuatro suites, por proyecto).
- Las mutaciones de cada bloque, detectadas.
- Comprobado en vivo contra la API real, una base desechable y Mailpit, **incluida la medición del tiempo**.
- Manual, ADR y spec al día, con las cifras reales (no estimadas).
- Ningún email, token, enlace ni contraseña en logs, eventos, errores ni respuestas.
