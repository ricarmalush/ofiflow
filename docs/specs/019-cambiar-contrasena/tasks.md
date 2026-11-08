# Tasks — 019-cambiar-contrasena

**Spec relacionada:** [./spec.md](./spec.md)
**ADR relacionadas:** [ADR-021](../../adr/ADR-021-recuperacion-de-contrasena.md) (la otra mitad: restablecer; el aviso y el cierre de sesiones), [ADR-014](../../adr/ADR-014-mfa-y-bloqueo-de-cuenta.md) (confirmar la contraseña actual y el código; el bloqueo de cuenta), [ADR-018](../../adr/ADR-018-sesion-ligada-al-access-token.md) (el access token muere con su sesión), [ADR-024](../../adr/ADR-024-segundo-factor-obligatorio.md) (la puerta del segundo factor), [ADR-012](../../adr/ADR-012-autorizacion-rbac.md) (el permiso nuevo) y la nueva **ADR-025** («Cambiar la contraseña estando dentro»)
**Estado general:** En curso (spec aprobada el 2026-11-08; bloque 1 hecho y verificado — Domain 352, Application 1053, Infrastructure 841, Api 821 en verde; 37 mutaciones detectadas)

---

## Convención

- Cada tarea completable y verificable en una sesión.
- Se marca `[x]` solo cuando cumple la Definición de «Done»: compila, tests en verde, mutaciones detectadas, comprobado en vivo y manual al día.
- **Nivel de riesgo de las pruebas: alto.** Toca una zona delicada de autenticación: comprueba una contraseña, cierra sesiones y escribe un hash. Se aplica la batería completa: matriz de casos (contraseña actual, código, igual, longitud, con y sin factor), el bloqueo de cuenta, **la atomicidad** (todo o nada, contra SQL Server real), **la carrera de dos cambios a la vez**, escaneos de arquitectura y mutaciones sobre todo lo que decide.
- Las mutaciones se hacen a mano sobre el código real, con copia de seguridad y **`touch` al restaurar**; solo cuenta como «detectada» una con tests en rojo (`[FAIL]`); un test colgado o un error de compilación no cuentan. **Una mutación debe quitar de verdad lo que dice quitar** (lección de las specs 021 y 022): p. ej. «el registro lleva la contraseña» solo se detecta con un test que **manda** la contraseña. Los guiones van **en ficheros** (nunca en `node -e` con comillas invertidas dentro).
- Verificación en vivo con la receta de siempre (base desechable, API real, Mailpit, TOTP calculado con un selector de pasos **monótono**: el servidor rechaza un paso anterior al último usado) y, en el bloque 3, `ng serve` y el navegador integrado.
- Las suites se lanzan **por proyecto, una detrás de otra**. Si `dotnet build` choca con una DLL bloqueada por otro proceso (IIS Express), se compila a una carpeta aparte con `-p:OutDir` y **no se toca ese proceso**.
- **Orden de entrega:** a diferencia de la spec 022, el backend **puede fusionarse solo** (añade una operación; no rompe a ningún cliente). El frontend va en el mismo par de PR por limpieza, no por necesidad.

---

## Bloques

### Bloque 1 — La operación (Domain + Application + Infrastructure)
- [x] **Permiso `Account.ChangePassword`** (Domain): concedido a los cinco roles; la tabla de permisos, su nombre estable y la matriz de tests pasan de 14 a 15 permisos y de 70 a 75 celdas. Los tests que cuentan permisos se actualizan **a propósito**. *Hecho:* en los cinco roles; los tests que fijan la matriz (`RolePermissionsTests`, `PermissionNamesTests`), la tabla de operaciones (contador 46 → 47) y la lista de permisos de `me` se actualizaron **a propósito** (14 → 15 permisos, 70 → 75 celdas). Un cambio no previsto: el test de «nada sensible en `me`» buscaba la palabra `password` en toda la respuesta y falló por el **nombre** del permiso nuevo; ahora comprueba que los permisos son nombres con la forma `Grupo.Acción` y busca las palabras prohibidas en todo lo demás.
- [x] **Errores y diccionario:** `password.current_invalid` y `password.unchanged` (`400`), con su texto en `ErrorMessages.resx` (el test del diccionario completo lo exige), y el caso en `ExceptionMappingContractTests`. El código del segundo factor reutiliza `mfa.invalid_code`. *Hecho:* `password.current_invalid` y `password.unchanged` (`400`), con su texto en `ErrorMessages.resx` y dos casos nuevos en `ExceptionMappingContractTests`.
- [x] **`ITokenService.RevokeOtherRefreshTokensWithoutSavingAsync(userId, currentSessionId)`:** revoca los refresh tokens de **todas las sesiones menos la actual**, **sin confirmar**; sin Id de sesión actual (`null`), revoca todos (*fail-closed*). Devuelve cuántas cerró. *Hecho:* **revoca** (no borra: un refresh token cerrado que alguien use después se sigue detectando como reutilización, `1003`). La sesión conservada incluye todas sus filas (una renovada tiene la anterior revocada y la nueva activa).
- [x] **`IPasswordResetService.InvalidatePendingWithoutSavingAsync(userId)`:** marca como usados/anulados los enlaces vigentes de la cuenta, **sin confirmar**, conservando la fecha del primer uso (como al anular los demás en `ConsumeAsync`). *Hecho:* solo los vigentes de esa cuenta; los caducados y los de otras cuentas no se tocan, y los ya usados conservan su fecha.
- [x] **`ChangePasswordCommand` (+ `Validator`, `Handler`)**, en el orden de la spec (R3): la contraseña actual (`ConfirmPasswordAsync`), el código si hay factor activo (`VerifySecondFactorAsync`), la nueva distinta (comparada en memoria), y entonces `SetPasswordAsync` + cierre de las demás sesiones + anulación de enlaces + **una única `SaveChangesAsync`**, y el aviso encolado **después** de confirmar. El `Validator` comprueba longitudes **antes** de gastar nada. `ToString()` sin propiedades. El handler captura `DbUpdateException` como el de restablecer (`Reject` sin dejar nada). *Hecho:* con una única `SaveChangesAsync` y el aviso **después** de confirmar. Un conflicto al confirmar (otro cambio u otro canje del enlace lo ganó) responde `password.current_invalid`, nunca un `500`, y no deja nada ni aviso ni evento.
- [x] **Evento `1023`** (`PasswordChangedFromSession`: `UserId` y sesiones cerradas; nunca contraseña, código ni email). *Hecho:* `PasswordChangedFromSession`. El registro lleva solo `UserId` y cuántas sesiones se cerraron.
- [x] **Tests.** *Domain:* el permiso en los cinco roles. *Application (dobles):* cada rama del orden (actual errónea no mira el código; código erróneo no cambia nada; igual; cada longitud; sin factor ignora el código; con código de recuperación), **que no confirma hasta el final** (un doble que falla al confirmar no deja nada cambiado), el aviso solo tras confirmar y **no se encola si falla**, el evento sin secretos, sin Id de sesión cierra todas, y `ToString`. *Infrastructure (SQL Server real):* `RevokeOther...` (conserva exactamente la sesión indicada, cierra las demás **de esa cuenta y de ninguna otra**, sin Id cierra todas, no confirma por su cuenta), `InvalidatePending...` (solo de esa cuenta, conserva la fecha del primer uso, no confirma por su cuenta) y **el todo o nada** de punta a punta contra la base real (si la confirmación falla, la contraseña, las sesiones y los enlaces quedan como estaban). *Hecho:* Domain 352 (+6), Application 1053 (+40: la operación rama por rama, el validador, el orden, el todo o nada con dobles, el aviso y el evento sin secretos), Infrastructure 841 (+18 contra SQL Server: `RevokeOther…`, `InvalidatePending…` y **una carrera real** en la que otro cambio gana y la confirmación de esta falla sin dejar nada), Api 821 (+2 casos del contrato de errores). **Total backend: 3067 casos** (3001 antes). Una lección: renovar una sesión relee la pertenencia a la empresa, y mis cuentas de prueba no tenían ninguna (`NoTenantMembership`): los dos primeros tests de renovación fallaron por eso, y se sembró empresa y pertenencia.
- [x] **Mutaciones:** el orden roto (código antes de la contraseña, «igual» antes de la contraseña), la contraseña actual que no se comprueba, el factor que no se pide, la comprobación de «igual» que no se hace, la que cierra **también** la sesión actual o **ninguna**, la que cierra las de **otra** cuenta, los enlaces que no se anulan, confirmar dos veces o por separado, el aviso que sale aunque falle, el aviso antes de confirmar, el registro que lleva la contraseña o el código, el contador que no se reinicia, y el permiso que falta a un rol. *Hecho:* **37/37 detectadas** (`mutate019b1.js`): el orden roto de dos formas, la actual o el factor que no se piden o se piden siempre, el código ausente que gasta una comprobación, «igual» que no se comprueba o ignora mayúsculas, la sesión actual que se cierra o ninguna o todas, los enlaces que no se anulan o se anulan los de otra cuenta o los caducados, confirmar dos veces o por separado, el aviso antes de confirmar, el conflicto tragado, el registro con la contraseña, el evento duplicado, el comando que imprime sus secretos, y el permiso que falta a un rol.
- [ ] **[manual]** Diferido al **bloque 4**.
- [x] [externo] Commit del bloque 1

### Bloque 2 — La API y sus pruebas de extremo a extremo (Api)
- [ ] **`PUT /api/v1/account/password`:** `204`, `Cache-Control: no-store`, sin cuerpo; la persona sale solo del token. Documentado en OpenAPI y en el `.http`.
- [ ] **Límite de peticiones:** cubo propio por IP (`account-password`), validado como los demás.
- [ ] **No está en la lista cerrada del segundo factor** (spec 022): un `Owner` o `Admin` sin factor recibe `403 mfa.enrollment_required`. Un escaneo fija que la lista sigue siendo **exactamente cuatro**.
- [ ] **Escaneos de arquitectura** (al estilo de las specs 018, 021 y 022): una **sola confirmación** en el handler, `SetPasswordAsync` y los dos métodos nuevos **no confirman por su cuenta**, la contraseña no se registra (ninguna llamada de registro recibe `CurrentPassword`/`NewPassword`/`Code`), el aviso va **después** de la confirmación, y que la operación declara su permiso (la tabla de operaciones se actualiza **a propósito**).
- [ ] **Tests de extremo a extremo** (API entera, SQL Server real, Mailpit o su doble): el flujo completo (cambia, entra con la nueva y no con la vieja, la sesión actual sigue), **otras sesiones mueren** (`401` en su siguiente petición y su refresh no sirve), actual errónea **cuenta para el bloqueo** (el quinto fallo bloquea), con factor: código erróneo / ausente / ya usado / de recuperación, sin factor: `code` ignorado, «igual», longitudes **sin gastar intentos**, un enlace de recuperación vigente que **deja de servir**, el aviso encolado una vez y sin secretos, **los cinco roles pueden** y una cuenta sin sesión, no; un `Owner` sin factor: `403 mfa.enrollment_required`; **dos cambios simultáneos** (sin `500`); el límite por IP; y los registros sin contraseña ni código.
- [ ] **Mutaciones** de la capa API y de lo que pasa por HTTP: el endpoint sin el permiso o con otro, sin `no-store`, el límite sin aplicar, la operación añadida a la lista cerrada del factor, la sesión actual cerrada.
- [ ] **Comprobación en vivo** (API real, base desechable, Mailpit, TOTP): cambiar con y sin factor, ver morir otra sesión, el enlace de recuperación anulado, el aviso en Mailpit sin secretos, el bloqueo por fallos, el `403` de un `Owner` sin factor, y el registro de la API sin contraseñas ni códigos.
- [ ] **[manual]** Diferido al **bloque 4**.
- [ ] [externo] Commit del bloque 2

### Bloque 3 — Frontend (`ofiflow-frontend`)
- [ ] **Servicio:** `changePassword(current, new, code?)` en el servicio de la cuenta, con el mismo tratamiento de errores que el segundo factor (el mensaje de la API, nunca el texto interno).
- [ ] **La pantalla de seguridad** gana una tarjeta **«Cambiar contraseña»**: la actual, la nueva y su repetición (solo en el cliente), y el **código** solo si la cuenta tiene el factor activo (lo dice el estado del factor que la pantalla ya carga). La repetición distinta se avisa sin enviar nada; las longitudes, como en el resto de formularios.
- [ ] **Tras un cambio correcto no se cierra la sesión:** se limpia el formulario (**ninguna contraseña se queda en memoria ni en la página**), se avisa con un mensaje, y se **refresca la lista de sesiones** (las demás han desaparecido). Con error, el mensaje de la API en su campo o arriba, y la contraseña **no se borra** del control salvo la actual por seguridad.
- [ ] **La tarjeta no se ofrece** a quien debe activar el factor (la API le daría `403`) ni sin el permiso `Account.ChangePassword`.
- [ ] **Tests (specs):** el servicio, la tarjeta (qué campos salen según el factor, la repetición distinta, el botón bloqueado mientras envía, el éxito, cada error), que no cierra la sesión, que refresca las sesiones y que se esconde a quien debe activar el factor.
- [ ] **Mutaciones:** la repetición que no se comprueba, el código que se pide siempre o nunca, la sesión que se cierra, las contraseñas que no se limpian, la lista que no se refresca, la tarjeta que no se esconde.
- [ ] **Comprobación en vivo** (navegador integrado, API real): cambiar la contraseña con y sin factor, ver desaparecer otra sesión, volver a entrar con la nueva, el error con una actual errónea, y el ancho de móvil.
- [ ] [externo] Commit del frontend (en `ofiflow-frontend`)

### Bloque 4 — Documentación
- [ ] **ADR-025** (`docs/adr/ADR-025-cambiar-contrasena-dentro.md`): el modelo de amenazas (sesión robada frente a buzón comprometido), las decisiones D1–D5, lo que cambió al implementar, la recomendación de despliegue y lo que queda abierto.
- [ ] Anotaciones «Ampliada por ADR-025» en **ADR-021** (la otra mitad), **ADR-012** (el permiso nuevo y la matriz de 75 celdas), **ADR-014** (el código del factor en otra operación sensible) y **ADR-018** (cerrar «las demás»); `docs/README.md` y el roadmap del `README.md`.
- [ ] Manual: capítulo 0 (la operación nueva), 6 (una parte nueva: cambiar la contraseña, y el límite «cambiarla estando dentro» cerrado), 7 (los códigos nuevos y el recuento real), 8 (el mapa de amenazas, el evento `1023`, los permisos y los límites), 9 (cifras, la sección de la spec 019, mutaciones y lecciones), 12 (glosario e índices) y el anexo del tribunal.
- [ ] `spec.md` pasa a «Implementada» y este `tasks.md` queda completo, con la Definición de Done al día.
- [ ] [externo] Commit del bloque 4 y de `tasks.md`
- [ ] [externo] Push de las dos ramas y PR (backend y frontend), con tu orden

---

## Definición de Done (por bloque)

- Compila sin avisos nuevos y los tests de **todos los proyectos afectados** pasan, lanzados por proyecto.
- Cada regla de la spec tiene al menos un test que falla si se rompe; lo que decide (el orden de las comprobaciones, que la actual y el código se piden, «igual», el todo o nada, cerrar las demás y **no** la actual, anular los enlaces, el bloqueo por intentos) tiene además su mutación detectada.
- Comprobado en vivo contra la API real y, en el bloque 3, en el navegador.
- **Ni la contraseña (actual o nueva), ni el código, ni los códigos de recuperación, ni el email** en logs, eventos, errores, ejemplos ni tests (los valores de prueba se generan en ejecución, no se escriben a mano).
- Gitleaks en local con la misma imagen y orden que la CI (`detect --log-opts="--all"`, sobre **todo el historial**): sin hallazgos nuevos.
- Manual y ADR al día en el mismo bloque o en el 4, nunca desfasados al cerrar la spec.

## Notas / bloqueos

- **Sin migración nueva:** todo se apoya en tablas y columnas que ya existen (`ApplicationUsers`, `RefreshTokens`, `PasswordResetTokens`).
- **Un permiso nuevo cambia la matriz:** los tests de RBAC y los que cuentan permisos (14 → 15; 70 → 75 celdas) se actualizan **a propósito**, no se relajan.
- **El fichero `appsettings.Development.json`** tiene un cambio local de la persona dueña del proyecto (`localdb` → `localhost`): **no se incluye en ningún commit**.
- **Commits, push y PR solo con orden expresa**, uno por uno.
