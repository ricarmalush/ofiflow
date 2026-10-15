# Spec — 010-mfa-and-account-lockout

**Estado:** Aprobada (2026-10-15), con las decisiones D1-D9 tal como se recomendaron
**Fecha:** 2026-10-15
**Bounded Context:** Identity (transversal: Application, Infrastructure y Api; el permiso nuevo vive en Domain)
**Depende de:** ADR-014 (MFA y bloqueo de cuenta, redactada junto a esta spec), ADR-007 (credenciales y refresh tokens), ADR-009 (seguridad base: cierra dos de sus NEXT), ADR-011 (errores con código), ADR-012 (permisos), ADR-013 (jerarquía de roles y borrado al quitar), spec 009 (gestión de usuarios)

---

## Objetivo

Que **robar o adivinar una contraseña no baste para entrar** en una cuenta de OfiFlow. Hoy la única barrera de la autenticación es la contraseña, protegida solo por un límite de 5 intentos por minuto **por IP**: un atacante con muchas IP (o con una contraseña filtrada en otro servicio) prueba contraseñas contra una misma cuenta sin que nada lo detenga. Esta spec añade dos defensas que se refuerzan entre sí:

1. **Segundo factor (MFA)**, opcional para cualquier miembro: además de la contraseña, un código de 6 dígitos que cambia cada 30 segundos y sale de la app del móvil (TOTP).
2. **Bloqueo de cuenta** por intentos fallidos, para **todas** las cuentas (tengan o no MFA).

Es el primer límite que cierra el anexo del manual y el **requisito previo del panel de operador** de la plataforma (spec 011): la cuenta que verá todas las empresas no puede protegerse solo con una contraseña.

## Alcance — Qué SÍ incluye

1. **Activar MFA** (TOTP) en la propia cuenta: pedir un secreto, escanearlo en la app del móvil y confirmarlo con un primer código.
2. **Códigos de recuperación** de un solo uso, mostrados una única vez al activar, para cuando se pierda el móvil.
3. **Login en dos pasos** para las cuentas con MFA: la contraseña no da sesión; da un *desafío* de pocos minutos que solo se canjea por sesión con un código válido.
4. **Desactivar** MFA y **regenerar** los códigos de recuperación (ambos exigen contraseña y un código).
5. **Reiniciar el MFA de un miembro** (para quien perdió el móvil y los códigos), aplicando la jerarquía de la spec 009.
6. **Bloqueo temporal de la cuenta** tras 5 fallos (contraseña o segundo factor), con la **misma respuesta** que un fallo cualquiera.
7. **Igualar el tiempo del login** cuando el email no existe o la cuenta está bloqueada (NEXT de ADR-009).
8. **Rate limiting** de los endpoints nuevos y **eventos de seguridad** nuevos, sin datos personales ni secretos.
9. **Cerrar las sesiones** de la cuenta cuando cambian sus factores de acceso (activar, desactivar, reiniciar).
10. **Guardarraíles**: un test de arquitectura que fija quién puede emitir sesiones, y las pruebas de carrera contra SQL Server real.
11. **Tests de extremo a extremo** de todo el ciclo y de cada ataque descrito, con las mutaciones sobre el código real que ya son costumbre.

## Alcance — Qué NO incluye

- **NOW** (necesario ahora, pero fuera de esta spec concreta): nada pendiente.
- **NEXT** (siguiente fase):
  - **MFA obligatorio** para `Owner` y `Admin`. Exige un estado de "sesión limitada a activar el MFA" y una pantalla que lo guíe; sin frontend, obligaría a cada dueño a usar `curl` (ver D2). Para la cuenta de **operador de la plataforma** sí será obligatorio, en la spec 011.
  - **Passkeys / WebAuthn**: resistente al *phishing* (el TOTP no lo es). Necesita el navegador y el frontend (D1).
  - **Recuperar y cambiar contraseña**, confirmar email. Cuando existan, cambiar la contraseña cerrará también las sesiones y estará protegido por este MFA.
  - **Bloqueo progresivo** (la duración crece con los bloqueos repetidos) y **desbloqueo manual** por un gestor.
  - **Claim `amr`** en el token de acceso (cómo se autenticó la sesión), si algún día hace falta distinguir sesiones con y sin MFA.
  - **Rotar la clave** que cifra los secretos (el formato guardado lleva un número de versión para que sea posible; el procedimiento se escribe entonces).
- **LATER** (interesante, no prioritario): "recordar este dispositivo 30 días"; notificar por email un bloqueo o un reinicio de MFA; historial consultable de accesos.
- **NEVER**: **SMS** como segundo factor (la línea se puede secuestrar con un duplicado de SIM, y exige un proveedor de pago con datos personales); guardar el secreto TOTP en claro; que una cuenta "se salte" el bloqueo por tener un rol alto.

## Reglas de negocio / invariantes

**El código TOTP y su secreto**

- **R1.** TOTP según RFC 6238 (sobre HOTP, RFC 4226): HMAC-SHA1, 6 dígitos, paso de 30 segundos. Es la combinación que entienden Google Authenticator, Microsoft Authenticator, Authy y los gestores de contraseñas. Se implementa **sobre la BCL, sin paquete nuevo** (D3) y se valida con los **vectores de prueba oficiales** de las dos RFC. El analizador `CA5350` avisa de `HMACSHA1`; se suprime **en un solo sitio y con la justificación escrita**: SHA-1 está roto como *función hash* (colisiones), no como base de un HMAC, y el estándar lo exige para que las apps lo entiendan.
- **R2.** El secreto son **20 bytes aleatorios** de un generador criptográfico. Se muestra **una única vez**, al pedirlo, como texto Base32 y como URI `otpauth://` (con `Cache-Control: no-store`). El servidor **no genera el QR**: lo dibuja el frontend con esa URI, así el secreto no pasa por ningún servicio de terceros.
- **R3.** El secreto se guarda **cifrado** (AES-256-GCM, cifrado autenticado) con una clave de configuración (`Mfa:EncryptionKey`, 32 bytes en Base64), con el mismo trato que `Jwt:Secret`: **sin valor por defecto, la API no arranca si falta o es inválida**. Lo guardado lleva un byte de versión de la clave. El secreto no se devuelve nunca más, no se registra nunca y no se cifra dos veces con el mismo *nonce*.
- **R4.** Verificación: se aceptan los códigos del paso actual y de **uno anterior y uno posterior** (±30 s de desfase de reloj), comparados en **tiempo constante**. Debe ser exactamente un texto de 6 dígitos. **Un mismo paso no se acepta dos veces** (RFC 6238 §5.2): se guarda el último paso aceptado, y cualquier código de un paso menor o igual se rechaza. Esa anotación es **atómica**: dos peticiones simultáneas con el mismo código, solo una pasa.

**Activar, desactivar y mantener el MFA** (todas sobre la propia cuenta)

- **R5.** *Pedir el secreto* exige la **contraseña actual** (un token de acceso robado no basta para enrolar el móvil del atacante) y que el MFA **no esté activo**. Deja el secreto como *pendiente*; pedirlo otra vez antes de confirmar lo sustituye.
- **R6.** *Confirmar* exige un código válido del secreto pendiente. Entonces el MFA queda **activo**, se generan los **códigos de recuperación** (R9) y se cierran las sesiones (R14).
- **R7.** *Desactivar* y *regenerar los códigos* exigen **contraseña y un código válido** (del móvil o de recuperación). Desactivar borra secreto y códigos y cierra las sesiones. Regenerar invalida todos los anteriores.
- **R8.** Ver el estado (¿activo?, ¿cuántos códigos de recuperación quedan?) no devuelve nunca secretos ni códigos.

**Códigos de recuperación**

- **R9.** Se generan **10** códigos de **16 caracteres** (80 bits) de un alfabeto sin caracteres ambiguos, en grupos de cuatro (`XXXX-XXXX-XXXX-XXXX`). Se muestran **una única vez** (`no-store`) y solo se guarda su **hash SHA-256**. SHA-256 sirve aquí, y no para una contraseña, porque cada código es aleatorio de 80 bits: nadie puede probar 2⁸⁰ candidatos aunque robe la base de datos. Se aceptan sin distinguir mayúsculas ni guiones. Cada uno **se consume al usarse**, de forma atómica. Un código de recuperación vale como segundo factor (con el mismo límite de intentos) y deja un evento propio.

**Login en dos pasos**

- **R10.** Si la cuenta **no** tiene MFA activo, el login funciona como hoy. Si lo tiene, una contraseña correcta **no devuelve tokens**: devuelve `mfaRequired` y un **desafío**.
- **R11.** El desafío es un valor **aleatorio de 256 bits y opaco (no es un JWT)**, del que solo se guarda el hash; caduca a los **5 minutos**, se usa **una sola vez** y admite como mucho **5 códigos erróneos** (después muere y hay que volver a poner la contraseña). Como no es un JWT, **no se puede presentar como token de acceso**. Canjearlo (con un código de 6 dígitos o uno de recuperación) por tokens es atómico: dos canjes simultáneos con el mismo desafío, solo uno gana.
- **R12.** **Nunca se emiten tokens de sesión sin pasar el segundo factor** en una cuenta que lo tiene. Quién puede emitirlos está fijado por un test de arquitectura: solo el login (sin MFA) y el canje del desafío.
- **R13.** Todo fallo del segundo paso (desafío desconocido, caducado, usado o agotado; código erróneo; código ya usado) devuelve **exactamente la misma respuesta**, igual que R7 de la spec 009: quien lo intenta no aprende cuál fue. El motivo real solo va al log.

**Cerrar sesiones**

- **R14.** Activar, desactivar y reiniciar el MFA **borran todos los refresh tokens** de la cuenta (como al quitar a un miembro, spec 009 R14): lo que cambia es *cómo se demuestra quién eres*, y las sesiones abiertas se abrieron con las reglas antiguas. El token de acceso que ya esté emitido sigue valiendo, como mucho, sus 30 minutos (límite conocido, el mismo que tenía la spec 009 antes de la revalidación).

**Bloqueo de cuenta**

- **R15.** Cada cuenta lleva un contador de intentos fallidos y, en su caso, una hora hasta la que está bloqueada (se reutilizan las columnas `AccessFailedCount` y `LockoutEnd` que la tabla de credenciales ya tiene; D7). Fallan **la contraseña errónea y el segundo factor erróneo**; no cuentan el email desconocido (no hay cuenta que bloquear) ni el desafío caducado.
- **R16.** Al llegar a **5 fallos**, la cuenta se bloquea **15 minutos** y el contador vuelve a cero cuando el bloqueo termina. **Mientras está bloqueada, ni la contraseña correcta entra**: si entrara, bloquear no serviría de nada frente a quien sigue probando.
- **R17.** El contador solo se pone a cero con un **login completo** (contraseña y, si hay MFA, segundo factor). Una contraseña correcta en una cuenta con MFA **no** lo reinicia: quien conoce la contraseña no puede reiniciarlo para seguir adivinando códigos.
- **R18.** **El intento se reserva antes de comprobarlo**, de forma atómica en la base de datos. Así, **50 peticiones simultáneas no dan 50 intentos, sino 5**, aunque todas lean "no bloqueada" a la vez. Con un "leer, comprobar, sumar" el bloqueo se salta lanzando las peticiones en paralelo. La reserva se **devuelve** cuando el paso era correcto y falta el siguiente (contraseña correcta con MFA), y el contador se **reinicia** con el login completo.
- **R19.** Bloqueada, no registrada o con contraseña errónea: **la respuesta es idéntica** (401, mismo cuerpo) y **tarda lo mismo** (en los tres casos se hace una verificación de contraseña completa, con un hash ficticio si no hay uno real). La ayuda al usuario legítimo ("espera 15 minutos") no se da en la respuesta; el motivo real va al log.
- **R20.** Quitar a un miembro (spec 009 R14) **borra también** su credencial TOTP, sus códigos de recuperación y sus desafíos: no queda ninguna fila huérfana.

**Permisos nuevos**

| Permiso | Operaciones | Owner | Admin | Manager | Employee | Technician |
|---|---|:-:|:-:|:-:|:-:|:-:|
| `Mfa.Manage` | Ver estado, pedir secreto, confirmar, desactivar, regenerar códigos (**de la propia cuenta**) | ✅ | ✅ | ✅ | ✅ | ✅ |

Todos los roles pueden gestionar su **propio** segundo factor: no es un dato de la empresa y nadie debe depender de otro para protegerse. **Reiniciar el MFA de otro** miembro exige `Users.Manage` más la jerarquía de la spec 009 (`ManageableRoles`): el `Owner` reinicia a cualquiera, el `Admin` a `Manager`, `Technician` y `Employee`, y **nadie reinicia al `Owner` ni a sí mismo** (para uno mismo está "desactivar").

### Decisiones a confirmar antes de aprobar

| # | Decisión | Mi recomendación | Alternativa descartada y por qué |
|---|---|---|---|
| **D1** | **Tipo de segundo factor** | **TOTP** (app de autenticación): gratis, sin proveedor externo, sin datos personales nuevos, lo conoce todo el mundo | *Passkeys/WebAuthn:* el más fuerte (resiste al *phishing*), pero necesita navegador y frontend; es el siguiente paso natural. *SMS:* el más débil (duplicado de SIM) y de pago. *Código por email:* no hay servicio de correo (NEXT de la spec 009) y el email ya es el canal de recuperación de la cuenta |
| **D2** | **A quién se obliga** | **Opcional para todos los roles** ahora, con el mecanismo listo. Obligatorio para el operador de la plataforma en la spec 011 | *Obligatorio ya para `Owner` y `Admin`:* sin frontend, cada dueño que se registre tendría que activar el MFA con `curl` antes de poder usar nada, y hace falta un estado nuevo de "sesión solo para activar el MFA". Es el NEXT inmediato cuando haya pantallas |
| **D3** | **Quién calcula el TOTP** | **Implementación propia sobre la BCL** (HMAC + 30 líneas) con los vectores oficiales de las RFC | *Paquete (p. ej. Otp.NET):* una dependencia más que vigilar (ADR-010) para algo que cabe en una página y es verificable con vectores públicos. No se inventa ninguna criptografía: se usa `HMACSHA1` de .NET tal como manda el estándar |
| **D4** | **Dónde y cómo se guarda el secreto** | **AES-256-GCM** con una clave de configuración que no vive en la base de datos | *En claro:* quien robe la base de datos podría generar los códigos de todos. *Data Protection de ASP.NET:* guarda su anillo de claves normalmente en la misma base de datos (no protege frente a su robo) y añade piezas de despliegue que aún no existen. *Hash:* imposible, el servidor necesita el secreto para calcular el código |
| **D5** | **Cómo se hace el segundo paso** | **Desafío opaco guardado hasheado**, de un solo uso, con tope de intentos | *"Token parcial" JWT firmado:* no se puede contar intentos ni hacerlo de un solo uso sin guardar estado, y lo aceptaría cualquier sitio que valide el JWT si alguien se equivoca de audiencia. El opaco ni siquiera es un token de acceso |
| **D6** | **Si se pierde el móvil** | **10 códigos de recuperación (80 bits, solo hash)** y, si faltan, **reinicio por un gestor** con la jerarquía existente. El `Owner` no lo puede reiniciar nadie de su empresa | *Recuperación por email o SMS:* convierten el canal más débil en la llave de todo el MFA. *Reinicio de cualquiera por cualquiera con `Users.Manage`:* un `Admin` podría reiniciar al `Owner`, rompe la jerarquía de la spec 009 (D2). **Riesgo aceptado:** un `Owner` que pierde móvil **y** códigos queda fuera; lo resolverá el operador de la plataforma (spec 011) tras comprobar su identidad |
| **D7** | **Política de bloqueo** | **5 fallos → 15 minutos**, por cuenta, respuesta idéntica, intento reservado antes de comprobar, y reutilizando las columnas `AccessFailedCount` y `LockoutEnd` (se **ignora** `LockoutEnabled`: el bloqueo vale para todos, falla cerrado) | *Solo límite por IP (lo actual):* no frena a quien reparte los intentos entre muchas IP. *Bloqueo permanente hasta que lo levante un gestor:* cualquiera podría bloquear a quien quisiera. *`UserManager`/`SignInManager`:* su contador se lee y se escribe en dos pasos, así que las peticiones en paralelo lo esquivan, y ADR-007 lo dejó fuera a propósito (en el backlog de ADR-009). *Bloqueo progresivo:* mejor, pero otra columna y más reglas; NEXT |
| **D8** | **Sesiones al cambiar los factores** | **Se cierran todas** al activar, desactivar o reiniciar el MFA | *Dejarlas:* quien ya tenía una sesión robada la conserva justo después de que la víctima active el MFA, que es cuando lo hace por sospecha |
| **D9** | **Igualar el tiempo del login** | **Incluido**: hace falta para que bloqueada, desconocida y errónea no se distingan | *Dejarlo para después:* con el bloqueo, medir cuánto tarda la respuesta enseñaría qué cuentas existen y cuáles están bloqueadas. Cierra un NEXT de ADR-009 |

Valores iniciales (se revisan con datos reales): ventana ±1 paso, desafío 5 min / 5 intentos, bloqueo 5 fallos / 15 min, 10 códigos de recuperación, canje del desafío **10 por minuto y por IP**, gestión del MFA **10 por hora y por IP**.

## Modelo conceptual

```
Infrastructure/Identity  (credenciales: viven junto a ApplicationUser, no en Domain; ADR-007)
- ApplicationUser   (sin columnas nuevas: reutiliza AccessFailedCount y LockoutEnd; no se usa TwoFactorEnabled ni LockoutEnabled)
- MfaCredential     UserId (único) · SecretCipher (versión + nonce + texto cifrado + etiqueta)
                    ConfirmedAt? (null = pendiente) · LastUsedTimeStep · RowVersion
- RecoveryCode      Id · UserId · CodeHash (único) · UsedAt?
- MfaChallenge      Id · UserId · TokenHash (único) · ExpiresAt · FailedAttempts · ConsumedAt? · RowVersion
   (ninguna es de una empresa: ni ITenantOwned ni filtro de tenant, igual que ApplicationUser y RefreshToken)

Domain/Tenancy
- Permission        + MfaManage
- RolePermissions   (tabla única, ampliada: los cinco roles)

Application (nuevas operaciones)
- GetMfaStatusQuery                 [Mfa.Manage]
- StartMfaSetupCommand              [Mfa.Manage]   (contraseña)
- EnableMfaCommand                  [Mfa.Manage]   (código)
- DisableMfaCommand                 [Mfa.Manage]   (contraseña + código)
- RegenerateRecoveryCodesCommand    [Mfa.Manage]   (contraseña + código)
- ResetUserMfaCommand               [Users.Manage + ManageableRoles]
- VerifyMfaChallengeCommand         [anónima + rate limiting]
- LoginCommand                      (cambia: devuelve sesión o desafío)
- Puertos nuevos: ITotp (puro), ISecretProtector, IMfaService; IIdentityService gana la reserva de intento
```

Operaciones tras esta spec: **30** (25 con permiso, 5 anónimas).

## Criterios de aceptación

**Activar el MFA**
- Dado un miembro de cualquier rol, cuando pide el secreto con su contraseña, entonces recibe el secreto y la URI `otpauth://` una sola vez y con `no-store`; y el estado sigue siendo "inactivo".
- Dado un secreto pendiente, cuando confirma con el código de su app, entonces el MFA queda activo, recibe 10 códigos de recuperación **una sola vez** y sus refresh tokens anteriores dejan de servir.
- Dada una contraseña errónea, cuando pide el secreto, entonces falla y la contraseña errónea **cuenta** para el bloqueo (R15).
- Dado el MFA ya activo, cuando pide el secreto otra vez, entonces falla con `mfa.already_enabled`.
- Dado un código erróneo al confirmar, entonces el MFA sigue inactivo.
- Dado el secreto en la base de datos, entonces **no aparece en claro** en ninguna columna, ni en ningún log, ni en ninguna respuesta posterior.

**Login con MFA**
- Dada una cuenta con MFA, cuando pone la contraseña correcta, entonces **no recibe tokens** sino `mfaRequired` y un desafío; cuando lo canjea con un código válido, entonces recibe tokens iguales a los del login de siempre (misma empresa y rol).
- Dada una cuenta con MFA, cuando se intenta llegar a cualquier operación con solo la contraseña, el desafío o los dos, **sin código válido**, entonces nunca hay sesión (R12).
- Dado un desafío ya usado, caducado, agotado o inventado, o un código erróneo, entonces la respuesta es **idéntica** en todos (R13) y no se crea nada.
- Dado un desafío con 5 códigos erróneos, entonces el sexto intento falla aunque el código sea el bueno.
- Dado un código TOTP ya aceptado, cuando se presenta otra vez (incluso dentro de su ventana), entonces se rechaza (R4); y dos canjes simultáneos con el mismo código y desafío producen una sola sesión.
- Dado un código de un paso anterior o posterior, entonces se acepta; dado uno de hace dos pasos, se rechaza.
- Dado un código de recuperación, cuando se usa, entonces entra una vez y la segunda vez falla; y quedan 9.
- Dada la cuenta **sin** MFA, entonces el login se comporta exactamente como antes (los tests de la spec 002 siguen en verde).

**Desactivar, regenerar y reiniciar**
- Dado el MFA activo, cuando desactiva con contraseña y código válidos, entonces se borran secreto y códigos, se cierran las sesiones y el login vuelve a ser de un paso; sin contraseña **o** sin código válido, nada cambia.
- Dado que regenera los códigos, entonces los anteriores dejan de valer, los nuevos se muestran una sola vez.
- Dado un `Owner`, cuando reinicia el MFA de un `Technician`, entonces ese miembro queda sin MFA y sin sesiones; dado un `Admin`, cuando reinicia a otro `Admin`, al `Owner` o a sí mismo, entonces 403; dado un `Manager`, `Employee` o `Technician`, entonces 403.
- Dado el `Id` de un miembro de **otra empresa** o inexistente, entonces el mismo 404 en ambos casos.

**Bloqueo**
- Dada una cuenta con 5 contraseñas erróneas seguidas, cuando se prueba la correcta, entonces falla con la **misma respuesta** que una contraseña errónea; y cuando pasan los 15 minutos (reloj de pruebas), entonces entra.
- Dadas 50 peticiones erróneas **simultáneas** contra una cuenta (SQL Server real), entonces se hicieron **como mucho 5 comprobaciones de contraseña** y la cuenta queda bloqueada (R18).
- Dada una contraseña correcta en una cuenta con MFA, cuando se llega al segundo paso tras 4 fallos previos, entonces el contador **no** se reinicia (R17): un quinto fallo bloquea.
- Dado un login completo, entonces el contador vuelve a cero.
- Dados los tres casos (email inexistente, cuenta bloqueada, contraseña errónea), entonces la respuesta es byte a byte la misma y **se ejecuta la misma verificación de contraseña** (comprobado por conteo de llamadas, no por cronómetro).
- Dado un email que no existe, entonces no se crea nada ni se bloquea nada.

**Aislamiento y borrado (caso multi-tenant obligatorio)**
- Dado un `Owner` de la empresa A, cuando usa el `Id` de un miembro de la empresa B para reiniciar su MFA, entonces recibe el mismo 404 que con un `Id` inexistente; y el MFA de ese miembro sigue intacto.
- Dado un miembro de la empresa A, cuando gestiona su MFA, entonces solo ve y modifica **el suyo**: no hay ningún Id de otra cuenta en las rutas de gestión propia.
- Dado un miembro quitado (spec 009 R14), entonces no quedan filas suyas en `MfaCredential`, `RecoveryCode` ni `MfaChallenge`.

**Guardarraíles**
- Dado un código nuevo que llame a `ITokenService.IssueTokensAsync` fuera de los dos sitios permitidos, entonces el test de arquitectura falla (R12).
- Dada una operación nueva sin declarar, entonces el test de arquitectura de la spec 008 sigue fallando; y el test de cobertura de la matriz de permisos incluye `Mfa.Manage` (5 celdas copiadas literal de esta spec).
- Dada una API sin `Mfa:EncryptionKey` o con una clave que no mide 32 bytes, entonces **no arranca** y el mensaje dice qué falta.
- Dado un cambio en el modelo sin migración, entonces falla `TheMigrations_MatchTheModel` (spec 009).
- Dados los vectores de prueba de la RFC 4226 (Apéndice D) y de la RFC 6238 (Apéndice B), entonces el `Totp` propio produce exactamente los códigos publicados.

## Seguridad (ADR-009)

- **Datos personales:** no se añade ninguno nuevo. Se guardan **secretos de autenticación** (el secreto TOTP, cifrado; los hashes de los códigos de recuperación y de los desafíos). **Ninguno aparece en logs ni en respuestas posteriores**; los eventos llevan solo Ids, el motivo y los recuentos.
- **Autorización:** `Mfa.Manage` para los cinco roles sobre **la propia cuenta** (se justifica arriba: no hay datos de la empresa); reiniciar a otro exige `Users.Manage` más `ManageableRoles`. El Id de la cuenta sale del **token**, nunca del cuerpo de la petición. La revalidación por petición de la spec 009 sigue delante de todo.
- **Entrada:** código TOTP = exactamente 6 dígitos; código de recuperación = 16 caracteres del alfabeto (con guiones opcionales); desafío = longitud exacta esperada (no se hashea texto arbitrario enorme); contraseña = `PasswordRules` (límite máximo, para no hashear megabytes). Todos con su validador y sus constantes.
- **Aislamiento:** las tres entidades nuevas son de la **cuenta**, no de la empresa (no son `ITenantOwned`, como `ApplicationUser` y `RefreshToken`); el test centinela de la spec 007 las clasificará a propósito. **Usos nuevos de `IgnoreQueryFilters()`:** el canje del desafío ocurre sin empresa activa (lo busca por hash, como la aceptación de invitaciones) y **emite sesión para la empresa de la pertenencia**, no para una que mande el cliente; va a la lista blanca con su motivo. El reinicio por un gestor sí se hace con el filtro de empresa activo.
- **SQL crudo:** posiblemente **una** sentencia (`ExecuteUpdate`/SQL parametrizado) para reservar el intento de forma atómica (R18); se decide en el bloque 3 y, si es SQL, va parametrizada (`FromSql`/`ExecuteSql`), sin concatenar, y con su test contra SQL Server real.
- **Abuso:** el canje del desafío es anónimo y cuesta CPU y base de datos: límite de **10 por minuto y por IP**. La gestión del MFA, **10 por hora y por IP**. El login conserva sus 5 por minuto. **Riesgo aceptado:** quien conozca un email puede mantener esa cuenta bloqueada fallando a propósito cada 15 minutos (denegación de servicio dirigida). Es el precio de bloquear por cuenta; se mitiga con el límite por IP, los eventos `1005`/`1006` (una ráfaga se ve) y los NEXT de bloqueo progresivo y desbloqueo manual.
- **Eventos de seguridad (nuevos, sin datos personales):**
  - `1005` cuenta bloqueada · `1006` intento mientras estaba bloqueada
  - `1501` secreto pedido · `1502` MFA activado · `1503` MFA desactivado
  - `1504` segundo factor rechazado (motivo: código erróneo, repetido, desafío no válido, agotado…)
  - `1505` código de recuperación usado (cuántos quedan) · `1506` códigos regenerados
  - `1507` MFA reiniciado por un gestor (quién y a quién, solo Ids)
  - Un analista querría ver, por ejemplo, una ráfaga de `1504` sobre un mismo `UserId`, o un `1503`/`1507` inesperado seguido de un login desde una IP nueva.
- **Riesgos técnicos a verificar con tests reales:** (1) la **carrera del contador** (R18): 50 peticiones en paralelo contra SQL Server real; (2) la **carrera del código repetido** (R4) y del **canje del desafío** (R11) y del **código de recuperación** (R9): cada una con control de concurrencia en la base de datos, no solo un `if`; (3) que la **comparación** del código sea en tiempo constante; (4) que el **secreto cifrado** no se pueda descifrar con una clave distinta ni con el texto manipulado (GCM lo detecta); (5) que **ninguna ruta emita sesión** sin pasar el segundo factor (R12); (6) que el borrado de una cuenta no deje filas huérfanas (R20).
- **Lo que esta spec NO protege (a decir con claridad):** el TOTP **no resiste al *phishing***: una web falsa que pida contraseña y código en directo se los reenvía a la real. Lo resuelven las passkeys (NEXT). Y un `Owner` sin móvil ni códigos queda fuera (D6).

## Decisiones técnicas relevantes

- Se apoya en: ADR-007 (credenciales como entidad EF simple, sin `UserManager`), ADR-009, ADR-011, ADR-012, ADR-013.
- **¿Requiere una ADR nueva?** Sí: **ADR-014 (Propuesto)**, redactada junto a esta spec. Recoge el factor elegido, el cifrado del secreto, el desafío de dos pasos, la política de bloqueo con reserva atómica y lo que queda sin cubrir.
- **Cambia ADR-009:** se cierran dos de sus NEXT (lockout por cuenta; igualar el tiempo del login) y se amplía el catálogo de eventos de seguridad. Se anota al cerrar.
- **Cambia el contrato del login:** `POST /api/v1/auth/login` devuelve la sesión **o** un desafío. Hoy no hay frontend, así que no rompe a nadie; se anota en la colección `.http` y en el manual.

## Fuera de alcance / Backlog relacionado

- MFA obligatorio por rol, passkeys, bloqueo progresivo, desbloqueo manual, claim `amr`, rotación de la clave de cifrado, notificaciones por email, "recordar dispositivo", recuperar/cambiar contraseña.
- Purga de desafíos caducados: se borran los de la cuenta cada vez que se crea uno nuevo (decidir en el plan si hace falta algo más).
- Panel de operador de la plataforma (spec 011), que usará este MFA como requisito de acceso.

---

## Validación antes de aprobar

> ¿Esta funcionalidad ayuda a conseguir o mantener al primer cliente? (sección 84 del prompt maestro)

**Sí, indirectamente pero de forma clara.** Un autónomo guarda en OfiFlow los datos de sus clientes y, pronto, sus facturas: que le roben la contraseña no puede significar que le roben todo. Es una de las primeras preguntas de cualquier cliente con algo de criterio ("¿y si me roban la contraseña?") y hoy la respuesta honesta es "nada lo impide". Además es el **requisito del panel de operador**: no se puede dar a una cuenta la vista de todas las empresas con solo una contraseña. Lo que **no** hace es ayudar a cobrar o a trabajar más rápido: por eso el MFA es opcional ahora (D2) y no estorba el alta del primer cliente.
