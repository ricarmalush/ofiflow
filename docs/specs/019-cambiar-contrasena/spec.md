# Spec — 019-cambiar-contrasena

**Estado:** Aprobada (2026-11-08). Decisiones D1–D11 aprobadas; D2 (código del segundo factor) y D3 (cerrar las demás sesiones y mantener esta) confirmadas expresamente
**Fecha:** 2026-11-08
**Bounded Context:** Identity
**Depende de:** spec 018 y ADR-021 (restablecer la contraseña: la misma operación de fijar una contraseña nueva, el aviso por correo y la regla de cerrar sesiones; esta spec es la otra mitad, «estando dentro»), spec 010 y ADR-014 (confirmar la contraseña actual contando para el bloqueo, el segundo factor, el código que se pide en las operaciones sensibles), specs 013–016 y ADR-018 (las sesiones y su cierre: el access token muere con su sesión), spec 022 y ADR-024 (el segundo factor obligatorio: la puerta, y que esta operación **no** está en su lista cerrada), ADR-012 (la autorización por permisos)

> **Numeración:** 019 es el número que la ADR-021 reservó para esta spec. Su ADR será la **025** (la 024 ya existe).

---

## Objetivo

Que **una persona con sesión pueda cambiar su contraseña** (porque sospecha que se filtró, o por higiene) **sin dar ventaja a quien tenga solo una sesión robada**.

Hoy una contraseña solo se puede cambiar por el camino del olvido (spec 018: un enlace al buzón), que es **otra amenaza** distinta: allí el atacante puede ser quien controla el correo; aquí, **quien tiene una sesión robada** (un token, un navegador desatendido), y por eso aquí se exige la **contraseña actual** (y, si la cuenta tiene el segundo factor, un código). Sin esta operación, quien sospecha de su contraseña tiene que fingir que la olvidó.

## Alcance — Qué SÍ incluye

Cuatro bloques:

1. **La operación en el backend:** `PUT /api/v1/account/password` con `{ currentPassword, newPassword, code? }`. Comprueba la contraseña actual (cuenta para el bloqueo de cuenta, como en el segundo factor), el código del segundo factor si la cuenta lo tiene, y que la nueva sea distinta; y entonces, **en una sola confirmación**, fija la nueva, **cierra todas las demás sesiones y conserva esta**, anula los enlaces de recuperación pendientes y encola el aviso por correo.
2. **La API y sus pruebas de extremo a extremo:** permiso nuevo (`Account.ChangePassword`), límite de peticiones, evento de seguridad, y los escaneos de arquitectura y mutaciones de lo que decide.
3. **Frontend:** una tarjeta «Cambiar contraseña» en la pantalla de seguridad (la actual, la nueva dos veces y, si procede, el código), sin cerrar la sesión.
4. **Documentación:** ADR-025, el manual y la spec cerrada.

## Alcance — Qué NO incluye

- **NEXT:** que el aviso por correo lleve un enlace para **deshacer** («no fui yo»), y que diga desde dónde se cambió (ADR-021: son datos del dispositivo hacia un proveedor externo).
- **NEXT:** que quien cambia la contraseña pueda elegir cerrar **también** esta sesión.
- **LATER:** una lista de contraseñas filtradas conocidas (comprobar contra un servicio externo), y avisos de «hace tiempo que no la cambias».
- **NEVER:** caducidad obligatoria de contraseñas, historial de contraseñas anteriores y reglas de composición (mayúsculas, símbolos): **NIST SP 800-63B** recomienda no imponerlas (capítulo 6 del manual). La longitud 8 a 128 de siempre es la única regla.
- **No cambia:** el restablecimiento por correo (spec 018), ni el segundo factor (esta operación **no lo toca**: ni lo lee más que para pedir el código, ni lo cambia), ni el operador de la plataforma (tiene su propia identidad y sus sesiones no llegan a esta operación: no tienen permisos de empresa).

## Reglas de negocio / invariantes

**La operación**

- **R1. Hace falta la contraseña actual.** Se comprueba con la **misma** función que el segundo factor (`ConfirmPasswordAsync`): el intento se reserva **antes** de comprobar, cuenta para el bloqueo de cuenta (spec 010 R7), una cuenta bloqueada o una contraseña errónea **no se distinguen**, y acertar **no pone a cero** el contador (si no, un token robado más la contraseña reiniciaría el contador de otro ataque en curso). Error: `400 password.current_invalid`.
- **R2. Si la cuenta tiene el segundo factor activo, hace falta también un código** (de la app o de recuperación), con las mismas reglas que desactivar el factor o regenerar los códigos: los fallos cuentan para el bloqueo, y un código ya usado no vale. Error: `400 mfa.invalid_code` (el que ya existe). Sin segundo factor activo, el campo `code` se ignora.
- **R3. Orden de las comprobaciones:** la contraseña actual primero; después el código (si procede); después que la nueva **no sea igual a la actual** (`400 password.unchanged`); y solo entonces se cambia nada. Así un error de la contraseña actual **no gasta** una comprobación del código, y la regla «distinta» nunca se aplica a quien no ha demostrado la contraseña.
- **R4. La contraseña nueva** sigue las reglas de siempre: **de 8 a 128 caracteres**, sin más exigencias. Se valida en el `Validator` (error de campo `400`), **antes** de gastar ninguna comprobación de la actual. La actual y el código tienen su tope de longitud (no se hashea una cadena gigante).
- **R5. Todo o nada, en una sola confirmación.** Cuatro cosas cambian y se confirman **juntas**: la contraseña (un hash nuevo siempre), las demás sesiones (todas **menos la actual**), los enlaces de recuperación pendientes (se anulan) y el contador de intentos y bloqueo (a cero, como al restablecer). Si algo falla, no cambia nada: una contraseña cambiada con las sesiones ajenas vivas es justo lo que quien tenía sesión necesita.
- **R6. Se conserva la sesión actual** y **solo ella**. Las demás sesiones de la cuenta (otros dispositivos, o un ladrón de sesión) mueren: sus refresh tokens se borran y, por la spec 015, **sus access tokens dejan de valer en su siguiente petición**. Sin el Id de la sesión actual en el token no hay «actual» que conservar: se cierran **todas** (*fail-closed*).
- **R7. Los enlaces de recuperación pendientes se anulan.** Quien pidió un restablecimiento (o un atacante con acceso al buzón) y todavía tiene un enlace vigente **no puede usarlo después de que la persona cambie su contraseña desde dentro**.
- **R8. Aviso por correo.** Se encola el mismo aviso fijo que al restablecer («se ha cambiado la contraseña de tu cuenta»), sin enlaces ni la contraseña. Con la cola llena o el correo apagado se registra y se sigue: la contraseña ya cambió.
- **R9. No toca el segundo factor.** Cambiar la contraseña **no** activa, desactiva ni reinicia el factor, ni sus códigos de recuperación.

**La puerta del segundo factor obligatorio (spec 022)**

- **R10.** La operación **no** está en la lista cerrada de operaciones permitidas sin el factor. Un `Owner` o `Admin` que todavía no lo ha activado recibe `403 mfa.enrollment_required`: primero activa el factor, después cambia la contraseña.

**Quién y cuánto**

- **R11. Permiso `Account.ChangePassword`**, concedido a los **cinco roles** y solo sobre la **propia** cuenta (como `Account.Read` y `Account.CloseSessions`: un permiso dice *qué se hace*). La persona sale del token, nunca de la petición: no hay forma de cambiar la contraseña de otra.
- **R12. Límite de peticiones:** un cubo propio por IP (el hash PBKDF2 cuesta); la defensa real contra el abuso es el bloqueo de cuenta (R1).
- **R13. La respuesta:** `204` sin cuerpo, `Cache-Control: no-store`. Nunca devuelve tokens: la sesión actual sigue valiendo tal cual.

**Los eventos de seguridad**

- **R14.** Un evento `1023` («contraseña cambiada desde dentro»: `UserId` y cuántas sesiones se cerraron). Los fallos ya los registran `ConfirmPasswordAsync` (`1001` con el motivo) y el segundo factor (`1504`). **Nunca** ninguna contraseña, código, token ni email.

## Modelo conceptual

```
ChangePasswordCommand            { CurrentPassword, NewPassword, Code? }       [RequiresPermission(Account.ChangePassword)]
ChangePasswordCommandHandler     1. identityService.ConfirmPasswordAsync(userId, current)         → password.current_invalid
                                 2. si hay factor activo: mfaService.VerifySecondFactorAsync(...)  → mfa.invalid_code
                                 3. new != current                                                 → password.unchanged
                                 4. SetPasswordAsync                (sin confirmar)
                                    RevokeOtherRefreshTokensWithoutSavingAsync(userId, sessionId)   (sin confirmar)
                                    passwordResets.InvalidatePendingWithoutSavingAsync(userId)      (sin confirmar)
                                 5. SaveChangesAsync  (la única confirmación)
                                 6. emailQueue.TryEnqueue(PasswordChangedEmail)
ITokenService                    + RevokeOtherRefreshTokensWithoutSavingAsync(userId, currentSessionId)
IPasswordResetService            + InvalidatePendingWithoutSavingAsync(userId)
Permission                       + AccountChangePassword  (los cinco roles; 15 permisos, la matriz pasa de 70 a 75 celdas)
SecurityEventIds                 + PasswordChangedFromSession = 1023

Sin cambios de esquema ni migración: todo se apoya en tablas y columnas que ya existen.
```

## Criterios de aceptación

- Dada una persona con sesión, cuando cambia la contraseña con la actual correcta y una nueva válida, entonces `204`, **puede entrar con la nueva y ya no con la vieja**, y su sesión actual **sigue funcionando** sin renovar.
- Dada esa persona con **otras sesiones abiertas**, cuando cambia la contraseña, entonces en su siguiente petición esas sesiones reciben `401` (y su refresh token ya no sirve), y la lista de sesiones de la actual muestra solo esta.
- Dada una contraseña actual **errónea**, entonces `400 password.current_invalid`, **no cambia nada** y el intento **cuenta para el bloqueo** (el quinto fallo bloquea la cuenta).
- Dada una cuenta **con segundo factor** y un código **erróneo o ausente**, entonces `400 mfa.invalid_code` y no cambia nada; y con un código **ya usado**, igual.
- Dada una contraseña nueva **igual** a la actual, entonces `400 password.unchanged`, sin cambios.
- Dada una contraseña nueva de **7 o de 129 caracteres**, entonces un `400` de campo, **sin gastar** ningún intento de la contraseña actual.
- Dado un enlace de recuperación **vigente** de esa cuenta, cuando la persona cambia su contraseña desde dentro, entonces el enlace deja de servir (`400 password_reset.invalid`).
- Dado un cambio correcto, entonces se encola **un** aviso por correo al email de la cuenta, y el registro lleva el evento `1023` sin la contraseña, el código ni el email.
- Dada una cuenta **sin** segundo factor, entonces el campo `code` se ignora y no hace falta.
- Dado un `Owner` o `Admin` **sin** el segundo factor activado, entonces `403 mfa.enrollment_required` (y nada se cambia).
- Dada una petición **sin sesión**, entonces `401`; y con el token de una sesión de **plataforma**, no llega a la operación.
- Dados **dos cambios simultáneos** de la misma cuenta, entonces la cuenta queda con **una** de las dos contraseñas nuevas, **ninguna sesión ajena** viva y ninguna respuesta `500`: cada petición recibe `204` o, si la otra confirmó antes y la «actual» que enviaba ya no vale, `400 password.current_invalid`.
- Dado un cambio correcto, entonces el contador de intentos y el bloqueo de cuenta vuelven a cero.

Caso multi-tenant obligatorio: la persona sale **solo del token**; ni un `userId` ni un `tenantId` en la petición se leen, y el cierre de sesiones y la anulación de enlaces son **siempre de la propia cuenta** (nunca de otra persona ni de otra empresa).

## Seguridad (ADR-009)

- **Datos personales:** ninguno nuevo. El correo del aviso lleva el email de la cuenta (como el de restablecer) y texto fijo; **ninguna contraseña, código ni dato del dispositivo**.
- **Autorización:** permiso nuevo `Account.ChangePassword`, los cinco roles, solo la propia cuenta. La puerta del segundo factor obligatorio **sí** se aplica (no está en la lista de permitidas).
- **Entrada:** `CurrentPassword` y `NewPassword`: de 1 a 128 y de 8 a 128 (la constante de la política de autenticación); `Code`: hasta 64 como en el segundo factor. `ChangePasswordCommand.ToString()` **no imprime sus propiedades** (la lección de la spec 021: un `record` imprimiría las contraseñas si alguien lo registrara).
- **Aislamiento:** ninguna entidad nueva ni lectura nueva fuera de las existentes; el servicio de recuperación y el de tokens ya filtran por el Id de la cuenta.
- **SQL crudo:** ninguno.
- **Abuso:** endpoint autenticado y costoso (hash PBKDF2): límite propio por IP (R12) **y** el bloqueo por cuenta (R1), que es lo que frena de verdad a quien tenga una sesión robada y pruebe contraseñas.
- **Enumeración:** nada que enumerar: quien pregunta ya está autenticado y es la propia cuenta.
- **Eventos de seguridad:** `1023`; el resto, los de siempre.
- **Modelo de amenazas (lo importante):**
  - **Mejora:** una **sesión robada** (un token, un navegador desatendido) **ya no basta para cambiar la contraseña** y dejar fuera a su dueño: hace falta la contraseña actual y, con segundo factor, el móvil.
  - **Mejora:** quien sospecha que su contraseña se filtró puede **cambiarla y expulsar a todos los demás de golpe**, sin fingir que la olvidó.
  - **Mejora:** un **enlace de recuperación pendiente** (de un atacante con acceso al buzón) **muere** al cambiar la contraseña desde dentro.
  - **No mejora, y hay que decirlo:** si el atacante tiene **la contraseña actual y la sesión actual** de una cuenta **sin segundo factor** (un `Manager`, `Technician` o `Employee` sin él), **puede cambiarla** y expulsar al dueño: la contraseña actual es precisamente lo que demuestra «ser quien debe». Con segundo factor (y siempre para `Owner` y `Admin`), el código lo impide.
  - **Se conserva la sesión actual:** si quien cambia la contraseña es quien tiene la sesión (lo normal), sigue dentro; si la sesión conservada fuera de un atacante que ya conoce la contraseña (y el código, si lo hay), ya tenía acceso completo antes: el cambio no empeora nada.

## Decisiones técnicas relevantes

- **D1. Contraseña actual obligatoria, con la misma función que el segundo factor.** *Recomendado.* Reutiliza lo ya probado (reserva del intento antes de comprobar, bloqueo, sin distinguir bloqueada de errónea, acertar no reinicia el contador). La alternativa —una comprobación propia— duplicaría una zona delicada.
- **D2. Con segundo factor, también el código.** *Decidido con la persona dueña del proyecto.* Como desactivar el factor o regenerar los códigos: una contraseña filtrada más una sesión robada no deben bastar. La alternativa (solo la contraseña) dejaba esa combinación abierta.
- **D3. Cerrar las demás sesiones y conservar esta.** *Decidido con la persona dueña del proyecto.* La alternativa (cerrar también esta, como al restablecer por correo o al activar el factor) es más uniforme pero obliga a volver a entrar justo después de haber demostrado la contraseña. Se paga un método nuevo, `RevokeOtherRefreshTokensWithoutSavingAsync`, que no confirma por su cuenta (lo confirma el caso de uso con el resto) y que cierra **todas** si no hay Id de sesión actual.
- **D4. Anular los enlaces de recuperación pendientes.** *Recomendado.* Sin ello, quien pidió un restablecimiento antes (por ejemplo, el atacante que controla el buzón) conservaría una vía para cambiar la contraseña **después** de que la persona la cambiara. Se paga un método nuevo en el servicio de recuperación, también sin confirmar por su cuenta.
- **D5. La nueva no puede ser igual a la actual** (`password.unchanged`). *Recomendado.* No revela nada (quien lo recibe acaba de demostrar la contraseña actual) y evita el «cambio» que no cambia nada pero cierra sesiones y manda un correo. Se compara en memoria, sin hashear de nuevo.
- **D6. Un permiso propio, `Account.ChangePassword`,** y no reutilizar `Mfa.Manage` ni `Account.Read`: un permiso dice qué se hace. Los cinco roles, solo la propia cuenta.
- **D7. `PUT /account/password`, `204` sin cuerpo.** *Recomendado.* Cambia el estado de la propia cuenta, es idempotente en el sentido de que repetirlo con la nueva como «actual» y otra nueva vuelve a cambiarla; no devuelve tokens porque la sesión sigue valiendo.
- **D8. Un solo evento, `1023`.** *Recomendado.* Los fallos ya tienen sus eventos (`1001` con el motivo `ReauthenticationFailed` y `1504`); duplicarlos solo añadiría ruido.
- **D9. El aviso por correo reutiliza `PasswordChangedEmail`** (texto fijo, sin enlaces ni datos del dispositivo). *Recomendado.* «Se ha cambiado la contraseña» vale igual venga de un restablecimiento o de un cambio, y el ADR-021 ya decidió no llevar el origen al correo.
- **D10. No está en la lista cerrada de la spec 022.** Un `Owner` o `Admin` sin factor activa primero el factor. Mantiene la lista de cuatro operaciones y la regla «lo no marcado se deniega».
- **D11. Orden de trabajo:** backend de la operación (**bloque 1**), API y pruebas de extremo a extremo (**2**), frontend (**3**) y documentación (**4**). Los bloques 1 y 2 se entregan juntos; el frontend va en el **mismo par de PR** (sin él la operación existe pero nadie la usa, y no rompe nada: a diferencia de la spec 022, aquí el backend **sí** puede fusionarse solo).
- **¿Requiere ADR nueva? Sí: ADR-025**, «Cambiar la contraseña estando dentro», con el modelo de amenazas (sesión robada frente a buzón comprometido), las decisiones D2–D4 y lo que queda abierto.

## Fuera de alcance / Backlog relacionado

- Un enlace para deshacer en el aviso («no fui yo»).
- Cerrar también la sesión actual, a elección de quien cambia la contraseña.
- Comprobar contra una lista de contraseñas filtradas.
- Eliminar la cuenta y los derechos del RGPD.
- Passkeys y recuperar a un `Owner` que lo pierde todo (ADR-024).

---

## Validación antes de aprobar

> ¿Esta funcionalidad ayuda a conseguir o mantener al primer cliente?

Sí, de forma indirecta pero real: es el control que cualquier cliente que se tome en serio la seguridad echa en falta el primer día («¿puedo cambiar mi contraseña?»), y cierra el último hueco de las cuentas básicas. Además es **barata**: casi todo lo que necesita ya existe y está probado (confirmar la contraseña actual, fijar la nueva, el aviso, el cierre de sesiones), y la pantalla de seguridad ya existe. Y reduce un riesgo concreto: hoy, quien sospecha de su contraseña no tiene forma limpia de cambiarla y expulsar a otros.
