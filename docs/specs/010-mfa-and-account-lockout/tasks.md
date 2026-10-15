# Tasks — 010-mfa-and-account-lockout

**Spec relacionada:** [./spec.md](./spec.md)
**ADR relacionada:** [../../adr/ADR-014-mfa-y-bloqueo-de-cuenta.md](../../adr/ADR-014-mfa-y-bloqueo-de-cuenta.md)
**Estado general:** Aprobada (2026-10-15); en curso (bloque 1)

---

## Convención

- Cada tarea completable y verificable en una sesión (1-4h).
- Se marca `[x]` solo cuando cumple la Definición de "Done" (sección 86).
- Orden por defecto: Domain → Application → Infrastructure → API → Tests.
- Cada guardarraíl se verifica **rompiendo a propósito el código real** y viendo el test en rojo antes de restaurarlo. Restaurar con **copias de seguridad o tras commitear**, nunca con `git checkout` sobre cambios sin commitear (lección de la spec 008). Cada mutación se ejecuta contra **todos los grupos de tests que la vigilan** (lección del bloque 4 de la spec 009).
- ⚠️ Las tareas **[externo]** (push, PR, merge) requieren confirmación explícita en el momento de hacerlas.
- **Nivel de riesgo de las pruebas: alto** (autenticación, secretos y carreras). Batería completa, pruebas contra SQL Server real para todo lo que sea concurrencia, y mutaciones sobre el código real en cada bloque.

## Regla de trabajo: manual y código en paralelo

Cada bloque termina con su tarea **[manual]**: los capítulos que describen lo que ese bloque cambió se actualizan **en el mismo bloque**, verificando cada nombre, ruta y cifra contra el repositorio. La spec no se cierra ni se pide el merge con el manual por detrás.

## Checklist

### Bloque 0 — Aprobación
- [x] Decisiones D1-D9 de la spec confirmadas (2026-10-15, tal como se recomendaron)
- [x] Spec, tareas y ADR-014 aprobadas (2026-10-15); se trabaja en la rama `spec/010-mfa-and-lockout`, actualizada con `master`

### Bloque 1 — Piezas puras: TOTP, Base32, códigos y permiso (≈ 4 h)
- [x] `Base32` (RFC 4648, sin relleno) con sus vectores de prueba oficiales
- [x] `Totp` (RFC 4226/6238, HMAC-SHA1, 6 dígitos, 30 s) como función pura que recibe el reloj; **suprimir `CA5350` en un solo sitio con la justificación escrita**; ventana ±1; devuelve **el paso** aceptado (lo necesita R4)
- [x] Tests con los **vectores oficiales**: RFC 4226 Apéndice D (10 valores HOTP) y RFC 6238 Apéndice B (SHA-1); límites del paso (29 s / 30 s); código de longitud incorrecta o no numérico; comparación en tiempo constante (que el código use `CryptographicOperations.FixedTimeEquals`: mutación)
- [x] Generación del secreto (20 bytes del CSPRNG) y de la URI `otpauth://` (con el emisor y la cuenta codificados para URL); test de formato
- [x] Generación de los 10 códigos de recuperación (16 caracteres, alfabeto sin ambiguos, 80 bits), normalización (mayúsculas, guiones) y hash SHA-256; test de longitud, no repetición y hash de referencia calculado con OpenSSL. La generalización del generador de tokens opacos de las invitaciones se mueve al bloque 5, donde llega su segundo consumidor (el desafío)
- [x] `Permission.MfaManage` y `RolePermissions` (los cinco roles); test de matriz con las 5 celdas copiadas literal de la spec
- [x] Errores con código en `IdentityErrors` y sus mensajes en `ErrorMessages.resx` (el test del diccionario los exige): `mfa.already_enabled`, `mfa.not_enabled`, `mfa.setup_required`, `mfa.invalid_code`, `mfa.invalid_password` (el reinicio por un gestor reutiliza los `tenant_user.*` de la spec 009)
- [x] **Constantes de la política** (umbral 5, bloqueo 15 min, desafío 5 min / 5 intentos, ventana ±1, 10 códigos) en un solo sitio, con test que las fija
- [x] [manual] Capítulo 8 (permiso `Mfa.Manage` y su fila en la matriz) y capítulo 3 (dónde viven `Totp`, `Base32` y los generadores); capítulo 7 (nuevos códigos y número de entradas del diccionario)

### Bloque 2 — Infrastructure: persistencia y cifrado (≈ 5 h)
- [ ] `ISecretProtector` y su implementación **AES-256-GCM**: nonce aleatorio de 12 bytes distinto en cada cifrado, byte de versión de la clave, etiqueta de autenticación; `Mfa:EncryptionKey` (32 bytes en Base64) **sin valor por defecto: la API no arranca si falta o no mide 32 bytes**, con mensaje claro como el de `Jwt:Secret`
- [ ] Tests: ida y vuelta; dos cifrados del mismo texto son distintos; texto manipulado o clave distinta → falla (no devuelve basura); clave ausente o de longitud incorrecta → no arranca
- [ ] Entidades `MfaCredential`, `RecoveryCode` y `MfaChallenge` con sus configuraciones (longitudes desde constantes, índice **único** por `CodeHash` y por `TokenHash`, único por `UserId` en la credencial, `RowVersion`), **sin** `ITenantOwned`
- [ ] Migración `AddMfa` revisada a mano (el SQL generado, no solo el nombre); comprobar que **no toca** `AccessFailedCount`/`LockoutEnd` (ya existen) y no añade columnas a `AspNetUsers`
- [ ] Clasificación del modelo de la spec 007: las tres entidades como "de la cuenta, no de la empresa" (el test centinela lo exige a propósito)
- [ ] `IIdentityService.DeleteUserAsync` borra también credencial, códigos y desafíos (R20); `ITokenService`/servicio de MFA con las operaciones de persistencia necesarias
- [ ] Tests de integración contra SQL Server real: `TokenHash`/`CodeHash` duplicado imposible; borrar una cuenta no deja huérfanos de ninguna de las tres tablas; el secreto guardado no contiene el texto original
- [ ] Mutaciones: quitar los índices únicos (también en la propia migración, como en la spec 009); `DeleteUserAsync` sin borrar cada una de las tres tablas; cifrar con nonce fijo; guardar el secreto en claro
- [ ] `Mfa:EncryptionKey` de **pruebas** en los fixtures de integración y de la API (valor fijo solo de tests) y en la guía de arranque
- [ ] [manual] Capítulo 6 (identidad: las tres entidades, el cifrado y por qué el secreto no se puede hashear), capítulo 5 (clasificación: entidades de la cuenta), capítulo 1 (nuevo secreto de configuración al poner en marcha) y capítulo 9 (nuevos tests con SQL real)

### Bloque 3 — Bloqueo de cuenta e igualar el tiempo (≈ 6 h) — el bloque más delicado
- [ ] Decidir y documentar **cómo se reserva el intento de forma atómica** (una sentencia `ExecuteUpdate`/SQL parametrizada que comprueba "no bloqueada" y suma a la vez); si es SQL, parametrizado y con el test de SQL real
- [ ] `IIdentityService`: reservar intento, devolver la reserva, poner a cero, aplicar el bloqueo al llegar al umbral; el reloj entra como `TimeProvider`
- [ ] `LoginCommandHandler` / validación de credenciales: mismo resultado y **misma verificación de contraseña** para email inexistente, cuenta bloqueada y contraseña errónea (hash ficticio fijo para los dos primeros); evento `1005` al bloquear y `1006` al intentar bloqueada; **ningún email en los logs**
- [ ] Tests con dobles: cada rama (R15-R19): el desconocido no cuenta, el quinto fallo bloquea, la correcta durante el bloqueo falla, tras 15 minutos entra, el login completo pone a cero, la contraseña correcta con MFA no reinicia (R17), **conteo de verificaciones** idéntico en los tres casos
- [ ] **Tests de carrera contra SQL Server real**: 50 peticiones erróneas simultáneas → como mucho 5 comprobaciones de contraseña (contadas con un decorador del verificador) y cuenta bloqueada; mezcla de correctas y erróneas en paralelo no esquiva el bloqueo
- [ ] Mutaciones: sumar **después** de comprobar (el esquema ingenuo) → el test de 50 peticiones se pone en rojo; la correcta entra bloqueada; el desconocido bloquea algo; no devolver la reserva; reiniciar con la contraseña sola (R17); verificar solo cuando existe la cuenta (rompe la igualdad de tiempo); bloquear 14 o 16 minutos; umbral 4 o 6
- [ ] [manual] Capítulo 8 (nueva subsección "Bloqueo de cuenta": la política, el porqué de reservar antes de comprobar, el límite 1 "no hay bloqueo de cuenta" cerrado y el riesgo aceptado del bloqueo malicioso, eventos `1005`/`1006`) y capítulo 6 (login: lo que cambia); cerrar en el capítulo 8 el NEXT de igualar el tiempo

### Bloque 4 — Application: gestionar el MFA (≈ 6 h)
- [ ] `GetMfaStatusQuery`, `StartMfaSetupCommand`, `EnableMfaCommand`, `DisableMfaCommand`, `RegenerateRecoveryCodesCommand` [`Mfa.Manage`], con sus validadores; **el Id de la cuenta sale del token** (`ICurrentUser`), nunca del cuerpo
- [ ] `ResetUserMfaCommand` [`Users.Manage`] con `ManageableRoles`: 404 si el miembro no existe en la empresa (indistinguible del de otra empresa), autogestión rechazada, `Owner` intocable; borra credencial y códigos y cierra las sesiones; evento `1507`
- [ ] R4 completa: verificación con ventana ±1, tiempo constante y **último paso aceptado actualizado de forma atómica** (control de concurrencia): dos peticiones con el mismo código, una pasa
- [ ] Contraseña y código de **cada** operación sensible cuentan para el bloqueo si fallan (R5-R7); los códigos de recuperación se consumen de forma atómica
- [ ] Activar, desactivar y reiniciar **borran los refresh tokens** (R14)
- [ ] Eventos `1501`-`1507` en `SecurityEventIds` (sin códigos, secretos ni datos personales)
- [ ] Declarar permiso o marca anónima en las operaciones nuevas; el test de arquitectura de la spec 008 sigue en verde (30 operaciones)
- [ ] Tests con dobles: cada regla R5-R9, cada celda de la jerarquía del reinicio, `Id` de otra empresa ≡ inexistente; **carreras con SQL real**: mismo código dos veces a la vez, mismo código de recuperación dos veces a la vez
- [ ] Mutaciones: no exigir contraseña; no exigir código al desactivar; aceptar el mismo paso dos veces; aceptar dos pasos atrás; no consumir el código de recuperación; no cerrar sesiones; el Admin reinicia al Owner; no actualizar el último paso de forma atómica
- [ ] [manual] Capítulo 6 (nueva parte: MFA, códigos de recuperación, sesiones cerradas), capítulo 8 (jerarquía del reinicio, nuevas filas del mapa de amenazas: contraseña robada, secreto robado de la base de datos, código reutilizado, móvil perdido) y capítulo 7 (códigos)

### Bloque 5 — Login en dos pasos (≈ 5 h)
- [ ] `MfaChallenge`: creación (borra los caducados de la cuenta), caducidad, un solo uso, tope de 5 intentos erróneos, hash del valor opaco
- [ ] `LoginCommand`: devuelve sesión **o** desafío; el resultado nuevo tiene un tipo propio (no un `null` ambiguo); misma elección de empresa de hoy
- [ ] `VerifyMfaChallengeCommand` anónima: busca por hash con `IgnoreQueryFilters()` (lista blanca con motivo), **emite sesión solo para la empresa de la pertenencia**, canje atómico, misma respuesta en todos los fallos (R13), cuenta bloqueada tratada como fallo idéntico
- [ ] **Guardarraíl R12**: test de arquitectura que lista los llamantes de `IssueTokensAsync` (solo el login sin MFA y el canje); `RefreshToken`, `Register` y `AcceptInvitation` no emiten sesión nueva
- [ ] Tests con dobles: cada rama de R10, R11 y R13; el desafío no sirve como token de acceso (no es un JWT: un test lo presenta al middleware JWT y falla)
- [ ] Mutaciones: emitir tokens tras la contraseña con MFA activo; no consumir el desafío; sin tope de intentos; aceptar el desafío caducado; respuesta distinta por motivo; sesión de una empresa que manda el cliente; un segundo llamante nuevo de `IssueTokensAsync` (el test de arquitectura lo caza)
- [ ] [manual] Capítulo 6 (el login en dos pasos de principio a fin, con un diagrama de secuencia en texto), capítulo 4 (la petición: el login ya no es un solo paso), capítulo 8 (lista blanca de `IgnoreQueryFilters()`: cuarto uso; riesgos aceptados)

### Bloque 6 — API, rate limiting y errores (≈ 4 h)
- [ ] Endpoints: `POST /api/v1/auth/mfa/verify` (anónimo), grupo `/api/v1/account/mfa` (`GET`, `setup`, `enable`, `disable`, `recovery-codes`) y `DELETE /api/v1/users/{id}/mfa`; respuestas con secreto o códigos en claro con `Cache-Control: no-store`
- [ ] Políticas de rate limiting (canje 10/min por IP; gestión 10/h por IP) y sus pruebas (la 11.ª petición → 429)
- [ ] Mapeo en `GlobalExceptionHandler` de los códigos nuevos; filas del contrato de errores; el login con desafío devuelve su forma documentada en OpenAPI
- [ ] [manual] Capítulo 8 (tabla de rutas, de límites y de eventos con los valores nuevos), capítulo 7 (tabla de errores) y capítulo 1 (política de arranque)

### Bloque 7 — Extremo a extremo por HTTP (≈ 6 h)
- [ ] Ciclo completo con un **generador de TOTP en el propio test** (que usa `Totp` ya validado con las RFC): registrar → pedir secreto → confirmar → cerrar sesión → login en dos pasos → usar un código de recuperación → desactivar
- [ ] Un test por cada criterio de aceptación (activar, login con MFA, desactivar/regenerar/reiniciar, bloqueo, aislamiento, guardarraíles), cada denegación con su **control positivo**
- [ ] **Ataques**: fuerza bruta del código (límite de intentos del desafío y bloqueo de cuenta, medido en peticiones reales), código repetido, desafío reutilizado, desafío de otra cuenta con el código de esta, sesión sin pasar el segundo factor por cada ruta, cuenta bloqueada con la contraseña correcta
- [ ] El secreto TOTP, los códigos y los desafíos **no aparecen** en la base de datos en claro ni en ningún log; ningún dato personal en los eventos nuevos
- [ ] Concurrencia por HTTP: dos canjes del mismo desafío, una sesión; 50 logins erróneos a la vez, cuenta bloqueada
- [ ] Mutaciones sobre el código real, una por regla clave (R4, R11, R12, R16, R17, R18, R19), cada una contra los tres grupos que la vigilan
- [ ] Actualizar los fixtures de las specs 008/009 si el nuevo límite del login (cuenta bloqueada, desafío) los afecta, anotando por qué
- [ ] [manual] Capítulo 9 (nueva familia de tests, cifras, el decorador contador de verificaciones, los vectores de las RFC como pruebas de oro) y capítulo 8 (mapa de amenazas completo, controles sin test propio si queda alguno)

### Documentación y cierre (≈ 3 h)
- [ ] ADR-014: de "Propuesto" a "Aceptado" con nota de estado de lo que aclaró la implementación
- [ ] ADR-009: marcar cerrados los NEXT de lockout por cuenta y de igualar el tiempo del login; catálogo de eventos y secretos actualizados
- [ ] `docs/README.md`: filas de la ADR-014 y de la spec 010
- [ ] `OfiFlow.Api.http`: peticiones nuevas (activar, login en dos pasos, reiniciar)
- [ ] Verificación real con `curl` contra una API real con SQL Server desechable: activar con una app (o un generador externo) y comprobar el ciclo, igual que en la spec 009
- [ ] [manual] Revisión final de los capítulos 0, 1, 3-9, 11 y 12 contra el repositorio (cifras de tests, códigos de error, eventos, límites conocidos); glosario (TOTP, desafío, segundo factor, código de recuperación, bloqueo de cuenta, reserva atómica); **Anexo de preguntas del tribunal**: cerrar el límite "MFA y bloqueo" y añadir las preguntas nuevas (¿por qué no SMS?, ¿por qué se cifra y no se hashea el secreto?, ¿qué pasa si 50 peticiones llegan a la vez?, ¿por qué sirve SHA-256 en los códigos de recuperación?)
- [ ] [externo] Push de la rama y PR contra `master`; los 4 checks en verde
- [ ] [externo] Merge (con confirmación explícita)

---

## Notas / bloqueos

- **Decisiones D1-D9** confirmadas el 2026-10-15 sin cambios.
- **Numeración:** esta es la **010**; el panel de operador de la plataforma pasa a ser la **011**, porque se implementa después y necesita este MFA.
- **Estimación total:** ≈ 30 h de código y ≈ 8 h de manual (repartidas por bloques) en 8 bloques + cierre. Parecida a la 009. Se puede partir en dos PRs si prefieres: **bloque 3** (bloqueo de cuenta, valioso por sí solo y sin dependencias) y el resto (MFA). El bloqueo no depende del MFA y cierra antes el hueco más fácil de explotar.
- **Dos piezas que conviene mirar con lupa en la revisión:** el bloque 3 (la reserva atómica del intento) y el guardarraíl R12 del bloque 5 (nadie más emite sesiones). Son las que, si fallan, deshacen la promesa de la spec.
- **Primer paso concreto del bloque 2:** la API debe fallar al arrancar sin `Mfa:EncryptionKey`; los tests de arranque que ya existen para `Jwt:Secret` sirven de modelo.
- **Decidir en el plan:** ¿el 401 de la cuenta bloqueada lleva `Retry-After`? Por defecto **no** (revelaría que existe y está bloqueada); si se quiere ayudar al usuario legítimo, es mejor un aviso desde el frontend tras activar el MFA que un dato en la respuesta.
- **Bloque 1 (2026-10-15):** las piezas puras y el permiso. En `Application/Identity/Mfa/`: `Base32` (RFC 4648, solo codifica), `Totp` (HOTP/TOTP de las RFC 4226/6238 sobre `HMACSHA1` de la BCL; `Verify` devuelve `Valid`/`Invalid`/`Replayed` y el paso aceptado, calcula siempre los tres pasos de la ventana y compara con `FixedTimeEquals`), `TotpSecret` (20 bytes del CSPRNG y la URI `otpauth://` con todo escapado) y `RecoveryCodes` (16 símbolos del alfabeto de Crockford = 80 bits, `XXXX-XXXX-XXXX-XXXX`, normalización que resuelve O→0 e I/L→1, hash SHA-256). `AuthenticationPolicy` (umbral 5, bloqueo 15 min, desafío 5 min / 5 intentos, 10 códigos). En Domain: `Permission.MfaManage` para los cinco roles y `MfaErrors` (5 códigos, con sus mensajes en `ErrorMessages.resx`: 32 códigos y 40 entradas). **Desviaciones del plan:** (1) el reinicio por un gestor reutiliza `tenant_user.role_not_manageable`/`cannot_manage_self`/`not_found` en vez de un código `mfa.*` propio; (2) la generalización de `InvitationToken` a un generador de tokens opacos pasa al bloque 5, cuando llega su segundo consumidor; (3) el aviso `CA5350` se suprime con `[SuppressMessage]` y justificación en un único método, y **dos guardarraíles nuevos** en `SecurityArchitectureTests`: `HMACSHA1`/`CA5350` solo pueden aparecer en `Totp.cs`, y la comparación del código no puede ser `==`/`SequenceEqual`. **Tests:** Domain 199 (+5: la fila nueva de la matriz), Application 333 (+88: vectores de las RFC 4226 y 6238, ventana, repetición, formato, Base32, secreto, códigos de recuperación, política), Api 238 (+3); suite completa: **866 casos** en verde, 0 avisos. **Mutaciones sobre el código real (22), restauradas desde copia, todas detectadas:** máscara del offset, sin máscara 0x7F, aceptar el mismo paso, ventana 0 y 2, `IsDigit` en vez de `IsAsciiDigit` (dígitos de ancho completo), sin ceros a la izquierda, repetido informado como inválido, `Min` por `Max`, sin guarda de paso negativo, máscara de Base32, `O` sin resolver, grupos de 5, secreto de 16 bytes, cuenta sin escapar en la URI, umbral 6, Technician/Owner/Employee sin `Mfa.Manage`, mensaje de diccionario ausente, `HMACSHA1` fuera de `Totp.cs` y comparación no constante. **Esta última solo la caza el guardarraíl de arquitectura** (los 333 tests de comportamiento siguen en verde): el tiempo de una comparación no se puede probar por resultado. **Manual (mismo bloque):** cap. 3 (`Mfa/`, `AuthenticationPolicy`, `MfaErrors`), cap. 7 (32 códigos, 40 entradas), cap. 8 (fila `Mfa.Manage`) y cap. 9 (866 casos, 443 métodos, 55 celdas y las pruebas de oro con los vectores de las RFC).
