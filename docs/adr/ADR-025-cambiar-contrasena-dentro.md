# ADR-025 — Cambiar la contraseña estando dentro: la actual y el código como prueba, todo o nada en una confirmación, y cerrar las demás sesiones conservando esta

**Estado:** Aceptado (2026-11-09). Las decisiones D1–D11 de la [spec 019](../specs/019-cambiar-contrasena/spec.md) se aprobaron tal como estaban escritas (D2 y D3 confirmadas expresamente: **también el código del segundo factor** y **cerrar las demás sesiones y mantener esta**); los cambios que se hicieron al implementar están en [«Lo que cambió al implementar»](#lo-que-cambió-al-implementar).
**Fecha:** 2026-11-09
**Proyecto:** OfiFlow

---

## Contexto

Hasta ahora una contraseña solo se podía cambiar por **el camino del olvido** ([ADR-021](ADR-021-recuperacion-de-contrasena.md), spec 018): un enlace al buzón. Quien sospechaba que su contraseña se había filtrado, o quería cambiarla «por higiene», tenía que **fingir que la había olvidado**. Y esa operación responde a **otra amenaza**: allí quien puede estar al otro lado es **quien controla el correo**; aquí, quien **tiene una sesión robada** (un token, un navegador desatendido) y quiere quedarse con la cuenta cambiando la contraseña.

Las ADR-021, 023 y 024 dejaron este hueco anotado («cambiar la contraseña estando dentro, otra amenaza, por eso exige la contraseña actual»). Esta ADR lo cierra. La operación es **barata** en piezas nuevas (casi todo existía y estaba probado: confirmar la contraseña actual contando para el bloqueo, fijar una contraseña, el aviso por correo, el cierre de sesiones) pero **delicada en el orden y en la atomicidad**: toca a la vez una credencial, las sesiones y los enlaces de recuperación.

## Problema

- ¿Qué se pide para demostrar que quien cambia la contraseña es quien debe: solo la **actual**, o también el **código** del segundo factor?
- ¿En qué **orden** se comprueba, para no gastar intentos de más ni dar pistas?
- ¿Qué pasa con las **otras sesiones**: se cierran todas (como al restablecer), o todas menos esta?
- ¿Qué pasa con un **enlace de recuperación ya pedido**?
- ¿Cómo se garantiza el **todo o nada** (la contraseña cambia, las sesiones ajenas mueren y los enlaces se anulan, o nada de eso) y qué ocurre con **dos cambios a la vez**?
- ¿Está la operación abierta a quien **debe activar** el segundo factor (spec 022)?
- ¿Qué **permiso**, qué límite, qué evento?

## Opciones consideradas

### A. Qué demuestra quien cambia la contraseña

**A1. La contraseña actual y, si la cuenta tiene factor, un código.** *(elegida; D1 y D2)* La contraseña actual se comprueba con **la misma función que el segundo factor** (`ConfirmPasswordAsync`): el intento se **reserva antes** de comprobar, cuenta para el bloqueo de cuenta, una cuenta bloqueada y una contraseña errónea **no se distinguen**, y acertar **no reinicia** el contador. El código, como al desactivar el factor o regenerar los códigos. Una **contraseña filtrada más una sesión robada no bastan** en una cuenta con factor.
**A2. Solo la contraseña actual.** Deja abierta justo esa combinación. Se descarta.
**A3. Una comprobación propia de la contraseña.** Duplicaría una zona delicada (reserva del intento, bloqueo, no distinguir). Se descarta.

### B. Qué se hace con las otras sesiones

**B1. Cerrar todas menos la actual.** *(elegida; D3)* Quien cambia la contraseña es, casi siempre, quien tiene la sesión y **sigue dentro**; las demás (otros dispositivos, o un ladrón de sesión) **mueren** (sus refresh tokens se revocan y, por la [ADR-018](ADR-018-sesion-ligada-al-access-token.md), sus access tokens dejan de valer en su siguiente petición). Sin el Id de la sesión actual en el token **no hay «actual» que conservar: se cierran todas** (*fail-closed*).
**B2. Cerrar todas, también esta** (como al restablecer o al activar el factor). Más uniforme, pero obliga a volver a entrar justo después de haber demostrado la contraseña. Se deja como NEXT a elección de quien cambia.
**B3. No cerrar ninguna.** Cambiar la contraseña sin expulsar a nadie no sirve a quien sospecha. Se descarta.

### C. Los enlaces de recuperación pendientes

**C1. Anularlos.** *(elegida; D4)* Quien pidió un restablecimiento antes (por ejemplo, un atacante que controla el buzón) **no debe poder usar su enlace después** de que la persona cambie su contraseña desde dentro.
**C2. Dejarlos.** Conservaría una vía de cambio de contraseña tras el propio cambio. Se descarta.

### D. Cómo se hace el cambio

**D1. Una única confirmación al final.** *(elegida; R5)* Cuatro cosas cambian juntas: la contraseña, las demás sesiones, los enlaces y el contador de intentos. Tres piezas **no confirman por su cuenta** (`SetPasswordAsync`, `RevokeOtherRefreshTokensWithoutSavingAsync` y `InvalidatePendingWithoutSavingAsync`) y la confirma el caso de uso, una sola vez. Si algo falla, **no cambia nada**: una contraseña cambiada con las sesiones ajenas vivas es justo lo que necesita quien tenía una sesión robada.
**D2. Tres confirmaciones separadas.** Una caída entre ellas dejaría la contraseña cambiada y las sesiones ajenas vivas. Se descarta.

### E. La puerta del segundo factor obligatorio

**E1. La operación no está en la lista cerrada de la [ADR-024](ADR-024-segundo-factor-obligatorio.md).** *(elegida; D10)* Un `Owner` o `Admin` sin factor recibe `403 mfa.enrollment_required`: **primero activa el factor, después cambia la contraseña**. Mantiene la lista de cuatro operaciones y la regla «lo no marcado se deniega».
**E2. Permitirla sin factor.** Abriría una operación que cambia la credencial a una sesión que todavía no ha demostrado el segundo factor. Se descarta.

## Decisión

### 1. La operación

`PUT /api/v1/account/password` con `{ currentPassword, newPassword, code? }`, `204` sin cuerpo y `Cache-Control: no-store`. La persona sale **solo del token** (un `userId` o `tenantId` en el cuerpo ni se lee: el comando no tiene esos campos). Nunca devuelve tokens: la sesión actual sigue valiendo.

**El orden** (R3) es deliberado:

1. La **contraseña actual** (`password.current_invalid`).
2. El **código**, solo si la cuenta tiene el factor activo (`mfa.invalid_code`). Un código **ausente** se rechaza **sin gastar una comprobación**, y un error de la actual **no gasta** una del código.
3. Que la nueva **no sea igual** a la actual (`password.unchanged`), comparada **en memoria** y **solo después** de haber demostrado la actual: quien no la ha demostrado no averigua nada sobre ella.
4. `SetPasswordAsync` + cierre de las demás sesiones + anulación de enlaces, **sin confirmar**, y **una sola** `SaveChangesAsync`.
5. **Después** de confirmar: se encola el aviso (`PasswordChangedEmail`, texto fijo, sin enlaces ni datos del dispositivo) y se registra el evento `1023`.

Las **longitudes** (la nueva, de 8 a 128; la actual y el código con su tope) se validan en el `Validator`, **antes** de gastar ningún intento. La regla de contraseña es la de siempre (NIST SP 800-63B: ni caducidad, ni historial, ni composición).

### 2. Todo o nada, incluso con dos cambios a la vez

El conflicto de concurrencia al confirmar (otro cambio, o el canje de un enlace de recuperación, ganó antes) se captura y responde **`password.current_invalid`, nunca un `500`**: la «actual» que enviaba esta petición **ya no vale**. La cuenta queda con **una** de las dos contraseñas, ninguna sesión ajena viva, y la que perdió no deja ni aviso ni evento. Un test contra SQL Server real provoca **la carrera de verdad** (otro cambio gana y la confirmación de esta falla sin dejar nada).

### 3. Cerrar «las demás» = revocar, no borrar

`RevokeOtherRefreshTokensWithoutSavingAsync(userId, currentSessionId)` **revoca** (marca como revocados) los refresh tokens de todas las sesiones **de esa cuenta** menos la indicada, y devuelve cuántas cerró; con `null` cierra todas. **No los borra**: un refresh token cerrado que alguien use después **se sigue reconociendo como reutilización** ([ADR-007](ADR-007-identity-refresh-token.md), evento `1003`). La sesión conservada incluye **todas sus filas** (una renovada tiene la anterior revocada y la nueva activa). `InvalidatePendingWithoutSavingAsync(userId)` marca solo los enlaces **vigentes de esa cuenta**: los caducados y los de otras cuentas no se tocan, y los ya usados conservan la fecha de su primer uso.

### 4. Quién, cuánto y qué se registra

- **Permiso `Account.ChangePassword`**, concedido a los **cinco roles** y solo sobre la **propia** cuenta (como `Account.Read` y `Account.CloseSessions`): ya hay **15 permisos** y la matriz pasa de 70 a **75 celdas**. Un permiso dice qué se hace: no se reutiliza `Mfa.Manage` ni `Account.Read`.
- **Límite de peticiones:** un cubo propio, `account-password`, **10 por hora y por IP** (el hash PBKDF2 cuesta); la defensa real contra el abuso es **el bloqueo de cuenta**, porque el intento de la actual cuenta para él.
- **Códigos de error:** `password.current_invalid` y `password.unchanged` (ambos `400`); ya hay **50**. El código del factor reutiliza `mfa.invalid_code`.
- **Evento `1023`** (`PasswordChangedFromSession`, nivel Information): `UserId` y cuántas sesiones se cerraron. Los fallos ya los registran `ConfirmPasswordAsync` (`1001`, con el motivo) y el segundo factor (`1504`): duplicarlos solo añadiría ruido. Con él hay **53 eventos** con identificador estable. **Nunca** ninguna contraseña, código, token ni email; y `ChangePasswordCommand.ToString()` **no imprime sus propiedades** (un `record` imprimiría las contraseñas si alguien lo registrara).
- **Sin migración:** todo se apoya en tablas y columnas que ya existían.

### 5. Frontend

Una tarjeta **«Cambiar contraseña»** en la pantalla de seguridad: la actual, la nueva y su repetición (**solo en el cliente**: la API recibe una única nueva), y el **campo del código solo si la cuenta tiene el factor** (lo dice el estado que la pantalla ya carga; el código se recorta antes de enviarse y solo viaja si hay uno). **No se ofrece** sin el permiso ni a quien debe activar el factor (la API le daría `403`). Tras un cambio correcto **no se cierra la sesión**: el formulario se **vacía entero** (ninguna contraseña queda en memoria ni en la página), se avisa con un mensaje y la pantalla **refresca la lista de sesiones** (las demás han desaparecido). Con error se muestra el mensaje de la API; se borran la actual (por seguridad) y el código (de un solo uso) y se conserva lo nuevo para no teclearlo otra vez. El botón no envía dos veces mientras espera.

## Consecuencias

### Positivas

- **Una sesión robada ya no basta para cambiar la contraseña y dejar fuera a su dueño:** hace falta la contraseña actual y, con segundo factor, el móvil. El intento de la actual **cuenta para el bloqueo**, que es lo que frena de verdad a quien pruebe contraseñas con una sesión robada.
- **Quien sospecha de su contraseña la cambia y expulsa a todos los demás de golpe**, sin fingir que la olvidó, y sin tener que volver a entrar.
- **Un enlace de recuperación pendiente muere** al cambiar la contraseña desde dentro.
- **Todo o nada**, probado contra SQL Server real, incluida una carrera de dos cambios.
- **Casi nada nuevo:** dos métodos sin confirmar, un permiso, dos errores y un evento; ninguna tabla.

### Negativas / riesgos aceptados

- **Una cuenta sin segundo factor cuyo atacante ya tiene la contraseña actual y una sesión puede cambiarla y expulsar al dueño** (un `Manager`, `Technician` o `Employee` sin factor): la contraseña actual es justo lo que demuestra «ser quien debe». Con factor, y **siempre** para `Owner` y `Admin` (spec 022), el código lo impide. Obligar el factor a más roles queda como NEXT (ADR-024).
- **Se conserva la sesión actual.** Si la sesión conservada fuera de un atacante que ya conoce la contraseña (y el código, si lo hay), ya tenía acceso completo antes: el cambio no empeora nada, pero tampoco lo expulsa. Cerrar también esta sesión a elección de quien cambia es NEXT.
- **El aviso por correo no dice desde dónde se cambió ni permite deshacerlo.** Es el mismo texto fijo del restablecimiento (ADR-021: llevar datos del dispositivo al correo sería un dato personal más hacia un proveedor externo). Un enlace «no fui yo» es NEXT.
- **Un conflicto al confirmar dice «la contraseña actual no es correcta»** aunque lo fuera: la actual **ya no vale** porque otro cambio ganó. Es el único mensaje que no da pistas ni inventa un código nuevo; la cuenta queda con una de las dos contraseñas.
- **Quien tenga una sesión robada puede bloquear la cuenta a propósito:** cada contraseña actual errónea cuenta para el bloqueo de 15 minutos (el precio de bloquear por cuenta, ADR-014). Es mejor que lo contrario: sin contar, esa misma sesión podría probar contraseñas sin límite.
- **Sin comprobar contra contraseñas filtradas conocidas** (LATER): la única regla es la longitud de 8 a 128.

### Lo que cambió al implementar

1. **El test de «nada sensible en `me`» falló por el nombre del permiso nuevo.** Buscaba la palabra `password` en toda la respuesta y `Account.ChangePassword` la contiene. Ahora comprueba que los permisos son nombres con la forma `Grupo.Acción` y busca las palabras prohibidas en **todo lo demás**; no se relajó la búsqueda.
2. **Los tests que cuentan permisos y operaciones fallaron a propósito** (14 → 15 permisos, 70 → 75 celdas, tabla de operaciones 46 → 47): un permiso nuevo en la matriz es una decisión, no un descuido.
3. **Una sesión cerrada que se usa después revoca todas, incluida la actual.** Mi primer test de extremo a extremo renovaba con el refresh token de una sesión **ya cerrada** y, por la detección de reutilización, mataba también la sesión que se quería comprobar viva. No era un fallo del producto, sino la protección funcionando; el test se reordenó (la actual se comprueba **antes** de usar el token de la cerrada).
4. **Renovar una sesión relee la pertenencia a la empresa** y mis cuentas de prueba de Infrastructure no tenían ninguna (`NoTenantMembership`): los dos primeros tests de renovación fallaron por eso y se sembró empresa y pertenencia.
5. **El frontend tiene un servicio propio** (`PasswordService`) y no amplía el del segundo factor: no comparten estado. Su cuerpo lleva dos campos o tres según haya código.
6. **Hay 53 eventos y 50 códigos de error** (52 y 48 antes), recontados contra el código.

### Hallazgos de la implementación (útiles para el tribunal)

- **La protección de reutilización se vuelve contra tu propio test:** la lección 3 es el mejor argumento a favor de **revocar y no borrar**: si se borraran, el token de una sesión cerrada daría un «no existe» silencioso y nadie sabría que alguien lo está usando.
- **«El factor se pide» no basta como prueba: hay que probar el orden.** Un código ausente que gasta una comprobación, o una actual errónea que gasta una del código, son errores que ningún test de «pide el código» detecta; por eso hay mutaciones del orden y de los intentos reservados.
- **Una mutación debe quitar de verdad lo que dice quitar** (la lección de las specs 021 y 022): «el registro lleva la contraseña» solo se detecta con un test que **manda** la contraseña y comprueba que no está en ningún registro.
- **Una carrera real vale más que un doble:** la confirmación que falla porque **otro** cambio ganó solo se ve con dos contextos contra SQL Server de verdad; con un doble se comprobaría el `catch`, no el todo o nada.
- **El navegador cuenta una historia que los specs no:** se verificó con tres cuentas (un `Technician` sin factor, un `Owner` con factor y un `Admin` restringido) que la tarjeta aparece, pide el código y se esconde donde debe; a 375 px no desborda.

### Cifras

- **Backend:** Domain 352, Application 1053, Infrastructure 841 y Api 863 = **3109 casos**, todo en verde. **56 mutaciones a mano sobre el código real, todas detectadas** (37 del caso de uso y 19 de la API). Comprobado en vivo con la API real, SQL Server real, Mailpit y TOTP calculado: **34/34**.
- **Frontend:** **586 specs** en verde, **35/35 mutaciones** detectadas y comprobado en el navegador integrado contra la API real.
- **Gitleaks** sobre cada commit: sin hallazgos.

### Recomendación de despliegue (no es código)

1. **El backend puede desplegarse solo** (añade una operación y no rompe a ningún cliente), pero tiene poco sentido sin el frontend: **fusionar el par seguido** evita una operación que nadie puede usar.
2. **No hay migración nueva.** Las migraciones pendientes de otras specs (016, 018, 020, 021) siguen su propio ritmo.
3. **Vigilar `1023` y los intentos fallidos de la actual (`1001` con el motivo de reautenticación).** Un `1023` es normal; una ráfaga de `1001` de reautenticación contra una cuenta es alguien con una sesión robada probando contraseñas, y el quinto fallo bloquea la cuenta.
4. **Un `1023` seguido de otro en poco tiempo** puede ser quien cambia la contraseña por segunda vez porque no se fía de la primera, o alguien que acaba de ganar una carrera; no es alarma por sí solo.
5. **Decir en las instrucciones de soporte** que cambiar la contraseña cierra las demás sesiones y que **no** toca el segundo factor.
6. **Probar de punta a punta con una cuenta real** antes de abrir a clientes: cambiarla con y sin factor, ver morir otra sesión y comprobar el correo de aviso.

### Qué queda cerrado (no se debe reabrir sin una razón nueva y explícita)

- **La contraseña actual es obligatoria y se comprueba con la función del segundo factor** (el intento se reserva antes y cuenta para el bloqueo).
- **Con segundo factor, también el código.**
- **Se cierran las demás sesiones y se conserva esta;** sin Id de sesión actual se cierran todas.
- **Revocar, no borrar** (la reutilización se sigue detectando).
- **Los enlaces de recuperación pendientes se anulan.**
- **Todo o nada en una sola confirmación;** un conflicto nunca es un `500`.
- **La operación no está en la lista cerrada del segundo factor** (un `Owner` o `Admin` activa primero el factor).
- **La persona sale del token,** nunca de la petición.
- **Ni contraseñas, ni códigos, ni secretos en los registros ni en `ToString`.**
- **No hay caducidad, historial ni reglas de composición de contraseñas** (NEVER; NIST SP 800-63B).

### Qué queda abierto para revisar más adelante

- **Un enlace para deshacer** («no fui yo») en el aviso, y que diga desde dónde se cambió.
- **Elegir cerrar también esta sesión.**
- **Comprobar contra una lista de contraseñas filtradas** y avisos de «hace tiempo que no la cambias».
- **Obligar el segundo factor a más roles** (ADR-024), que cerraría el riesgo de las cuentas sin factor.
- **Passkeys** y **recuperar a un `Owner` que lo pierde todo** (ADR-024).
- **Eliminar la cuenta y los derechos del RGPD.**

---

## Relación con otros ADR

- Cierra: el pendiente que reservaban la **[ADR-021](ADR-021-recuperacion-de-contrasena.md)** («cambiar la contraseña estando dentro: otra amenaza, por eso exige la contraseña actual»), la **[ADR-023](ADR-023-crear-una-cuenta.md)** y la **[ADR-024](ADR-024-segundo-factor-obligatorio.md)**.
- Amplía: **ADR-012** (un permiso nuevo, `Account.ChangePassword`, y la matriz de 75 celdas), **ADR-014** (el código del segundo factor y la contraseña actual, ahora también en otra operación sensible), **ADR-018** (cerrar «las demás» sesiones conservando la actual), **ADR-021** (la otra mitad: la misma operación de fijar una contraseña, el mismo aviso y la regla de cerrar sesiones), **ADR-009** (evento `1023`; el catálogo pasa a 53), **ADR-011** (códigos nuevos `password.current_invalid` y `password.unchanged`; ya hay 50).
- Relacionada con: ADR-024 (la puerta: esta operación no está en su lista cerrada), ADR-007 (la reutilización de un refresh token cerrado), ADR-008 (el SQL Server real que prueba la atomicidad y la carrera), ADR-020 (el aviso por correo, encolado después de confirmar).
- Prepara: obligar el factor a más roles, el enlace para deshacer y la comprobación contra contraseñas filtradas.
