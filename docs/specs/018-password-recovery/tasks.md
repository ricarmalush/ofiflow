# Tasks — 018-password-recovery

**Spec relacionada:** [./spec.md](./spec.md)
**ADR relacionadas:** [ADR-007](../../adr/ADR-007-identity-refresh-token.md), [ADR-009](../../adr/ADR-009-seguridad-base.md), [ADR-014](../../adr/ADR-014-mfa-y-bloqueo-de-cuenta.md) y [ADR-020](../../adr/ADR-020-correo-saliente.md) (se anotan) y la **ADR-021** nueva (D14)
**Estado general:** Aprobada (decisiones D1–D15, 2026-11-02). Sin empezar.

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
- [ ] `PasswordResetToken` en Domain.Identity (cuenta global): `Id`, `UserId`, `TokenHash`, `CreatedAt`, `ExpiresAt`, `UsedAt?`, `RowVersion`; creación y consumo en métodos de la entidad, con las reglas de vigencia
- [ ] Configuración de EF y migración `AddPasswordResetTokens`: índice **único** por `TokenHash` e índice por `(UserId, CreatedAt)`; sin email en ninguna columna. La entidad entra en la lista de **entidades globales** del test de arquitectura (spec 007), con su motivo y la lectura limitada a un fichero
- [ ] `IEmailQueue` (Application) y su implementación en memoria, **acotada** (100) y con descarte registrado si se llena; **sin esperar al envío**
- [ ] `EmailDispatchService` (Infrastructure): único consumidor; no arranca justo al iniciar; un fallo de envío se registra con la **categoría** y no mata el servicio; se detiene limpio; opciones validadas al arrancar; solo en la API (la consola de administración no lo recibe, con test). Evento de fallo con la categoría y sin la dirección
- [ ] `IEmailLinks.ResetPassword(token)` con la misma regla que `AcceptInvitation` (solo la configuración, fragmento, token codificado) y el test de arquitectura que fija que el enlace se escribe en un solo sitio
- [ ] La purga existente (`RefreshTokenPurgeService`) borra también los tokens de restablecimiento caducados hace más de 7 días, por lotes, con su índice (R18)
- [ ] Tests: la entidad (vigencia, consumo, anulación); la persistencia contra SQL Server real (esquema, índice único, versión de fila); la cola (orden, límite, descarte, un solo consumidor); el servicio (envía, un fallo no lo mata y el siguiente se envía, retraso inicial, parada limpia); la purga (caducados fuera, vigentes y recientes dentro); el registro solo en la API
- [ ] Mutaciones: cola sin límite; descarte sin registro; el servicio que muere con la excepción; ignorar `Enabled`; la purga que borra lo vigente; el enlace desde otro sitio; etc. (se cierra la lista al implementar)

### Bloque 2 — Pedir el restablecimiento (Application + Api)
- [ ] `ForgotPasswordCommand` y su validador (solo formato y longitud del email); `Handler`: busca la cuenta **sin revelar nada**, aplica el límite por cuenta (R4), anula los tokens anteriores, crea el nuevo, encola el correo; **la misma respuesta en todos los casos** (R1)
- [ ] `PasswordResetEmail` (función pura, plantilla fija sin datos de la persona)
- [ ] `POST /api/v1/auth/forgot-password` (anónimo) con su política de límite por IP (5 por hora); `202` sin cuerpo; documentación en `OfiFlow.Api.http`
- [ ] Sin correo configurado: `202`, ninguna fila, `Warning` (R6); un operador de la plataforma o un email desconocido: igual que «sin cuenta»
- [ ] Eventos de seguridad (pedido, pedido limitado), sin email ni token ni enlace
- [ ] **Medición del tiempo** con y sin cuenta (muchas muestras, alternadas); si se distingue, suelo de tiempo (D2); la cifra medida, en la spec y en el manual
- [ ] Tests (con dobles): misma respuesta con/sin cuenta, bloqueada, suspendida, operador y correo apagado; un solo token vigente; el cuarto pedido de la hora no crea ni encola; el hash guardado es el del token del enlace; ningún dato personal en los eventos. Con SQL Server real y la API: el cuerpo, el estado y las cabeceras son **idénticos** byte a byte; el `429` al sexto; el correo llega a la cola y se envía
- [ ] Mutaciones: responder distinto si no existe; sin límite por cuenta; el límite que avisa; no anular el token anterior; el token en claro guardado; el email en el evento; etc.

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
