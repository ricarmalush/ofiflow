# ADR-014 — Segundo factor (MFA) y bloqueo de cuenta

**Estado:** Aceptado (2026-10-15)
**Fecha:** 2026-10-15
**Proyecto:** OfiFlow

---

## Contexto

La autenticación de OfiFlow se apoya en una sola prueba: la contraseña, protegida por un límite de 5 intentos por minuto **por IP** (ADR-009). Eso deja dos huecos:

1. **Una contraseña robada o filtrada basta** para entrar con todos los permisos de esa persona.
2. **Nada limita los intentos contra una cuenta concreta**: quien reparte los intentos entre muchas IP no choca con el límite por IP.

Además, la cuenta de operador de la plataforma (spec 011) verá todas las empresas y no puede protegerse solo con una contraseña. Hay que decidir seis cosas:

1. **Qué segundo factor** ofrecer.
2. **Cómo guardar el secreto** que lo sostiene, que (a diferencia de una contraseña) el servidor necesita conocer.
3. **Cómo se parte el login en dos pasos** sin abrir un camino a la sesión que se salte el segundo.
4. **Qué pasa si se pierde el móvil.**
5. **Cómo se bloquea una cuenta** sin que el bloqueo se esquive con peticiones en paralelo, sin revelar qué cuentas existen y sin regalar una denegación de servicio fácil.
6. **A quién se obliga** de momento.

## Problema

Cada decisión tiene una opción cómoda que es insegura: el SMS, guardar el secreto en claro, devolver un token "provisional" tras la contraseña, recuperar el acceso por el mismo canal débil, contar los fallos con "leer, comprobar, sumar" o contestar distinto a una cuenta bloqueada.

## Opciones consideradas

### 1. Qué segundo factor

**A. TOTP (RFC 6238).** Código de 6 dígitos que cambia cada 30 s en una app del móvil. Gratis, sin proveedor, sin datos personales nuevos y entendido por todas las apps. **No resiste al *phishing***: una web falsa puede reenviar el código en directo.

**B. Passkeys / WebAuthn.** El más fuerte: resiste al *phishing* porque la clave está ligada al dominio. Exige el navegador y un frontend que no existen aún. Es el paso siguiente (NEXT).

**C. SMS.** Débil (duplicado de SIM, intercepción), de pago y con un dato personal más. Descartada.

**D. Código por email.** No hay servicio de correo todavía, y el email es el canal que se usaría para recuperar la cuenta: dos pruebas por el mismo canal no son dos factores.

### 2. Cómo guardar el secreto TOTP

El servidor tiene que **calcular** el código, así que no puede guardar solo un hash.

**A. En claro.** Quien robe la base de datos genera los códigos de todos. Descartada.

**B. Cifrado con la API de Data Protection de ASP.NET.** Cómodo, pero su anillo de claves se guarda normalmente en la misma base de datos (no protege contra su robo) y requiere decidir dónde persistirlo en cada despliegue.

**C. AES-256-GCM con una clave de configuración** (`Mfa:EncryptionKey`), fuera de la base de datos y con el mismo trato que `Jwt:Secret` (sin valor por defecto: la API no arranca sin ella). Cifrado autenticado: manipular el texto cifrado se detecta. El formato guardado lleva un byte de versión para poder rotar la clave.
- Desventaja: la clave es un secreto más que gestionar; y quien tenga **a la vez** la base de datos y la clave puede descifrar. Es el mismo modelo de amenaza que el resto de secretos de la aplicación (ADR-009 R8).

### 3. Cómo se parte el login

**A. Token parcial firmado (JWT) tras la contraseña.** Sin estado, pero no se puede hacer de un solo uso ni contar intentos sin guardar estado, y es un JWT del mismo emisor: un error de audiencia lo convertiría en un token de acceso.

**B. Desafío opaco guardado** (256 bits aleatorios, solo el hash, 5 minutos, un solo uso, 5 intentos). Como patrón ya está probado en este proyecto (refresh token, invitaciones). **No es un JWT**, así que ni por error se acepta como token de acceso.

### 4. Si se pierde el móvil

**A. Recuperación por email o SMS.** Convierte el canal más débil en la llave de todo el sistema. Descartada.

**B. Códigos de recuperación de un solo uso** (10, de 80 bits, solo su hash SHA-256) **y reinicio por un gestor** con la jerarquía de la spec 009. SHA-256 es suficiente porque los códigos son aleatorios de 80 bits (no hay diccionario que probar), a diferencia de una contraseña elegida por una persona.
- Límite: el `Owner` no lo reinicia nadie de su empresa (coherente con la jerarquía). Si pierde móvil y códigos, solo el operador de la plataforma podrá ayudarle, tras comprobar su identidad (spec 011).

### 5. Cómo se bloquea la cuenta

**A. Leer el contador, comprobar la contraseña y sumar si falla** (lo que hace `UserManager`). Con 50 peticiones simultáneas, las 50 leen "0 fallos", las 50 se comprueban y el bloqueo llega tarde: el atacante obtiene muchos más intentos de los permitidos.

**B. Reservar el intento antes de comprobarlo**, con una sola sentencia atómica en la base de datos (sumar y comprobar el límite a la vez). Solo los intentos reservados llegan a verificar la contraseña, así que el máximo es el umbral **aunque sean concurrentes**. Si el paso era correcto y falta el siguiente factor, la reserva se devuelve; el contador solo se pone a cero con un login **completo**.

**Qué ve el que ataca:** bloqueada, desconocida o contraseña errónea dan **la misma respuesta y tardan lo mismo** (se hace siempre una verificación completa, con un hash ficticio si no hay uno real). Ni siquiera la contraseña correcta entra durante el bloqueo.

**Política:** 5 fallos → 15 minutos, por cuenta, sobre las columnas `AccessFailedCount` y `LockoutEnd` que la tabla ya tiene. Se ignora `LockoutEnabled` (si estuviera a `false` por defecto, la cuenta saldría sin bloqueo: el valor por defecto sería inseguro). Bloqueo permanente descartado (cualquiera podría dejar sin acceso a otro); bloqueo progresivo, NEXT.
- Desventaja aceptada: quien conozca el email puede mantener la cuenta bloqueada fallando a propósito cada 15 minutos. Se mitiga con el límite por IP, eventos que hacen visible la ráfaga y los NEXT de bloqueo progresivo y desbloqueo manual.

### 6. A quién se obliga

**A. A todos, ya.** Sin frontend obliga a cada dueño a activar el MFA con `curl` antes de usar nada, y requiere un estado de "sesión solo para activar el MFA".

**B. Opcional ahora, obligatorio para el operador de la plataforma en la spec 011**, y obligatorio para `Owner`/`Admin` cuando exista una pantalla que lo guíe (NEXT).

## Decisión (propuesta)

- **TOTP** (1.A), sobre la BCL sin paquete nuevo y validado con los vectores oficiales de las RFC 4226 y 6238. El analizador `CA5350` (uso de HMAC-SHA1) se suprime en un único sitio, con la razón escrita: SHA-1 está roto como función hash, no como base de un HMAC, y el estándar lo exige para ser compatible con las apps.
- **Secreto cifrado con AES-256-GCM** (2.C), clave de configuración, sin valor por defecto. Se muestra una vez; nunca se registra.
- **Login en dos pasos con desafío opaco** (3.B), atómico y de un solo uso. Nunca se emiten tokens sin el segundo factor si la cuenta lo tiene, y un test de arquitectura fija quién puede emitirlos.
- **Recuperación** (4.B): 10 códigos de 80 bits más reinicio por un gestor según `ManageableRoles`.
- **Bloqueo con reserva atómica del intento** (5.B), 5 fallos / 15 minutos, respuesta y tiempo idénticos, y verificado con 50 peticiones en paralelo contra SQL Server real.
- **MFA opcional** de momento (6.B), con permiso `Mfa.Manage` para los cinco roles sobre su propia cuenta.
- **Activar, desactivar o reiniciar el MFA cierra todas las sesiones** de la cuenta.

## Consecuencias

### Positivas
- Una contraseña robada ya no basta en las cuentas con MFA, y adivinarla está limitado por cuenta, no solo por IP.
- Cierra dos NEXT de ADR-009 (lockout por cuenta; igualar el tiempo del login) y es requisito del panel de operador.
- Sin dependencias nuevas que vigilar (ADR-010).
- El bloqueo resiste las peticiones en paralelo, que es donde suelen fallar las implementaciones habituales.

### Negativas / riesgos aceptados
- **El TOTP no resiste al *phishing*.** Las passkeys (NEXT) sí.
- **Bloqueo malicioso:** se puede dejar sin acceso a una cuenta conocida fallando a propósito.
- **Un `Owner` sin móvil ni códigos** queda fuera hasta que intervenga el operador de la plataforma.
- **Un secreto más** (`Mfa:EncryptionKey`): perderla deja sin MFA a todas las cuentas que lo usan; filtrarla junto con la base de datos permite descifrar los secretos.
- ~~**El token de acceso ya emitido** sigue valiendo hasta 30 minutos tras cerrar las sesiones.~~ **Resuelto por la spec 015 / ADR-018:** activar, desactivar o reiniciar el MFA cierra las sesiones y su access token cae en la petición siguiente.
- Una consulta atómica más por intento de login.

### Qué queda cerrado
- Una cuenta con MFA nunca recibe sesión sin un segundo factor válido.
- Bloqueada, desconocida y contraseña errónea son indistinguibles desde fuera.
- El secreto TOTP y los códigos de recuperación nunca están en claro en la base de datos, ni se devuelven dos veces, ni se registran.

### Qué queda abierto para revisar más adelante
- **NEXT:** MFA obligatorio para `Owner`/`Admin` (con frontend); passkeys/WebAuthn; bloqueo progresivo; desbloqueo manual; rotación de la clave de cifrado; claim `amr`; recuperar y cambiar contraseña (que también cerrará sesiones).
- **LATER:** "recordar este dispositivo"; avisos por email de bloqueos y reinicios.

---

## Nota de estado (2026-10-15)

Implementada por la spec 010 (bloques 1 a 7), con 1192 tests en verde. Lo que la implementación aclaró y que conviene tener presente:

- **La anotación atómica no usa `rowversion`.** El último paso TOTP aceptado y el consumo de un código de recuperación se escriben con un `UPDATE` condicional (`... WHERE LastUsedTimeStep < @paso`, `... WHERE UsedAt IS NULL`): una fila o ninguna, sin depender del seguimiento de EF ni lanzar excepciones. El canje del desafío es igual (`... WHERE ConsumedAt IS NULL AND ExpiresAt > ahora AND FailedAttempts < 5`).
- **Todo el control del bloqueo vive dentro de `IdentityService.AuthenticateAsync`.** El llamante no puede olvidarse de devolver o limpiar la reserva. La única sentencia SQL fuera de LINQ del proyecto es la reserva del intento (`UPDATE ... OUTPUT`), parametrizada y en una lista blanca vigilada por un test.
- **Un fallo real, solo cazado por el test de extremo a extremo.** Con 4 fallos previos, el quinto intento *reserva*, llega a 5 y fija el bloqueo antes de saber si la contraseña es buena; con segundo factor, devolver la reserva no levantaba el bloqueo y una persona legítima quedaba fuera. `ReleaseAsync` levanta ahora el bloqueo si el contador baja del umbral. Está probado en tres niveles (servicio, login y recorrido HTTP).
- **Los fallos al confirmar el primer código no cuentan para el bloqueo** (se limitan por IP): quien se equivoca al escanear el QR no debe quedarse sin poder entrar. Sí cuentan la contraseña y el código al pedir el secreto, desactivar o regenerar.
- **El cifrado ata el secreto al Id de la cuenta** (dato asociado de GCM): un secreto copiado a la fila de otra cuenta no se descifra. Refuerza la decisión 2.C.
- **El desafío no cuenta contra la cuenta si no sirve** (desconocido o caducado): si contara, cualquiera podría bloquear cuentas ajenas inventando desafíos. Un desafío erróneo gasta uno de sus 5 intentos; un código erróneo, además, suma al contador de la cuenta.
- **Solo dos sitios emiten sesiones** (el login sin MFA y el canje del desafío), fijado por un test de arquitectura que también comprueba que, en el login, la rama del segundo factor termina antes de la emisión.
- **Riesgos aceptados que se mantienen:** el TOTP no resiste al *phishing*; se puede mantener bloqueada una cuenta ajena fallando a propósito cada 15 minutos; un `Owner` sin móvil ni códigos queda fuera hasta que intervenga el operador de la plataforma; ~~el token de acceso ya emitido vale hasta 30 minutos tras cerrar las sesiones~~ (resuelto por la spec 015 / ADR-018); y el `catch` de `DbUpdateException` de *activar* no tiene un test que garantice su ejecución.

---

## Relación con otros ADR

- Amplía: ADR-007 (más credenciales, sin `UserManager`), ADR-012 (permiso `Mfa.Manage`) y ADR-013 (el reinicio de MFA usa `ManageableRoles`; quitar a un miembro borra también su MFA).
- Cierra, al aceptarse: los NEXT de ADR-009 "Lockout por cuenta" y "Enumeración de usuarios: igualar el tiempo del login".
- Afecta a: ADR-009 (eventos `1005`, `1006` y `1501`–`1507` en el catálogo de R5; `Mfa:EncryptionKey` entre los secretos de R8).
- Es requisito de, y lo reutiliza: la spec 011 (ADR-016, panel de operador de la plataforma). El segundo factor es ahí **obligatorio sin excepción**: el alta lo enrola y lo confirma en el mismo acto, el login de plataforma siempre devuelve un desafío, y el desafío lleva una audiencia para que no cruce de mundo. El bloqueo de cuenta protege también al operador.
