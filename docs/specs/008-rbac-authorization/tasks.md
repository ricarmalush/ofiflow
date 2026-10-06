# Tasks — 008-rbac-authorization

**Spec relacionada:** [./spec.md](./spec.md)
**ADR relacionada:** [../../adr/ADR-012-autorizacion-rbac.md](../../adr/ADR-012-autorizacion-rbac.md)
**Estado general:** Implementada (2026-10-06); pendiente de PR y merge

---

## Convención

- Cada tarea completable y verificable en una sesión (1-4h).
- Se marca `[x]` solo cuando cumple la Definición de "Done" (sección 86).
- Orden por defecto: Domain → Application → Infrastructure → API → Tests.
- Cada guardarraíl se verifica **rompiendo a propósito el código real** y viendo el test en rojo antes de restaurarlo (lección de la spec 007).
- ⚠️ Las tareas **[externo]** (push, PR, merge) requieren confirmación explícita en el momento de hacerlas.

## Checklist

### Bloque 1 — Domain (≈ 2 h)
- [x] `Permission` (enumerado) en `Domain/Tenancy`, con los 8 permisos de la matriz
- [x] `RolePermissions`: la tabla única rol → permisos
- [x] Test de la matriz: cada celda de la tabla de la spec, para los 5 roles y los 8 permisos
- [x] Test: todo rol del enumerado `TenantRole` aparece en la tabla (un rol nuevo sin permisos declarados falla)
- [x] Test: `Owner` tiene todos los permisos del enumerado

### Bloque 2 — Application (≈ 4 h)
- [x] `RequiresPermissionAttribute` y `AllowAnonymousRequestAttribute`
- [x] `ICurrentUser` (`UserId`, `Role`) en `Common/Abstractions`
- [x] `ForbiddenException` con el código `auth.forbidden`
- [x] `AuthorizationBehavior`: deniega sin permiso, sin declaración (fail-closed) o sin rol válido; las anónimas pasan
- [x] Registrarlo **antes** que `ValidationBehavior`
- [x] Declarar el permiso en las 13 operaciones autenticadas y marcar las 3 anónimas
- [x] Evento de seguridad `1301` (`AccessDenied`) en `SecurityEventIds` y su registro en el behavior
- [x] Tests con un `ICurrentUser` de prueba: cada rol frente a cada tipo de operación, sin declaración, rol ausente, y el orden respecto a la validación

### Bloque 3 — Infrastructure y Api (≈ 2 h)
- [x] `CurrentUser` en Infrastructure: lee los claims del usuario autenticado (comprobar el nombre real del claim del rol con un token emitido por `TokenService`) — hecho en el bloque 2
- [x] Registro en `AddInfrastructure` — hecho en el bloque 2
- [x] `GlobalExceptionHandler`: `ForbiddenException` → 403 con `code`
- [x] Mensaje de `auth.forbidden` en `ErrorMessages.resx` (el test del diccionario ya exige que no falte) — hecho en el bloque 2
- [x] Título del ProblemDetails de 403 en el diccionario

### Bloque 4 — Guardarraíl fail-closed (≈ 2 h)
- [x] Test de arquitectura: toda operación (`IRequest`) de Application declara `[RequiresPermission]` o `[AllowAnonymousRequest]`, con mensaje que indica qué hacer
- [x] Meta-tests con operaciones sintéticas mal declaradas (sin atributo; con los dos a la vez)
- [x] Demostración con mutaciones del código real: quitar el atributo de una operación; desactivar el behavior; invertir un permiso en la tabla. Ver el rojo y restaurar con git

### Bloque 5 — Extremo a extremo por HTTP (≈ 3 h)
- [x] Sembrar en la base de datos de prueba usuarios de cada rol (credenciales + `User` + `TenantUser`) en la empresa A, y hacer login por HTTP para obtener tokens reales
- [x] Un test por rol y operación relevante contra la matriz (403 / éxito), con control positivo para cada denegación
- [x] 403 antes que 400: datos inválidos sin permiso
- [x] 403 idéntico para el `Id` de otra empresa y para uno inexistente
- [x] Token sin claim de rol o con rol inválido → 403
- [x] Las operaciones anónimas siguen funcionando
- [x] Los tests de aislamiento de la spec 007 pasan sin cambios

### Documentación y cierre (≈ 1-2 h)
- [x] ADR-012: de "Propuesto" a "Aceptado", con una nota de estado sobre lo que aclaró la implementación (el renombrado de claims de JwtBearer)
- [x] ADR-009: RBAC cerrado en "Qué queda abierto" y evento `1301` en el catálogo de R5. **ADR-003:** su nota de estado vive en la rama `docs/manual-tecnico` (aún no en `master`); se actualiza allí, para no chocar al publicar el manual
- [x] `docs/README.md`: filas de la ADR-012 (Aceptado) y de la spec 008 (Implementada)
- [ ] Manual técnico (rama `docs/manual-tecnico`): actualizar la mención de "RBAC pendiente" en los capítulos 0, 2 y 4
- [ ] [externo] Push de la rama y PR contra `master`; los 4 checks en verde
- [ ] [externo] Merge (con confirmación explícita)

---

## Notas / bloqueos

- **Decisiones a confirmar antes de aprobar:** la matriz de permisos (sobre todo `Employee` y `Manager`) y que `Owner` y `Admin` sean idénticos por ahora.
- El rol viaja en el token: un cambio de rol no se nota hasta renovar la sesión (riesgo aceptado en ADR-012).
- Sin invitaciones, los usuarios de cada rol solo existen sembrados en los tests. La spec de invitar y gestionar usuarios es el siguiente paso natural.
- **Bloque 1 (2026-10-06):** `Permission` y `RolePermissions` en `Domain/Tenancy` (tabla inmutable, `FrozenDictionary`/`FrozenSet`; un rol fuera de la tabla no tiene ningún permiso). Las filas de `Owner` y `Admin` se listan una a una a propósito, sin derivarlas del enumerado: un permiso nuevo no se concede a nadie, tampoco al `Owner`, hasta decidirlo. `RolePermissionsTests` (46 tests): la matriz aprobada copiada literal en el test, aparte de la tabla de producción (40 celdas, una por rol y permiso), más 6 de coherencia (la matriz del test cubre todos los permisos y roles; todo rol tiene fila; `Owner` los tiene todos; ningún permiso queda sin rol; un rol fuera del enumerado no puede nada; los conjuntos no se pueden modificar desde fuera). Mutaciones, restauradas desde copia: (1) `Technician` recibe `CustomersDelete` → rojo en su celda; (2) `Owner` pierde `JobsCancel` → rojo en su celda y en "Owner tiene todos"; (3) se borra la fila de `Employee` → rojo en sus 4 celdas permitidas y en "todo rol tiene fila"; (4) permiso nuevo sin decidir quién lo tiene → rojo en 3 tests. Build con 0 avisos; suite completa en verde: Domain 100, Application 53, Api 61, Infrastructure 34.
- **Bloque 2 (2026-10-06):** en `Application`: `RequiresPermissionAttribute`, `AllowAnonymousRequestAttribute`, `ICurrentUser`, `ForbiddenException`, `AuthorizationBehavior` (registrado antes que `ValidationBehavior`; fail-closed: sin declarar, o anónima y con permiso a la vez, se deniega; todos los permisos repetidos se exigen) y el evento `1301`; las 16 operaciones declaradas (13 con permiso, 3 anónimas). **Cambio de orden respecto al plan:** `CurrentUser` (Infrastructure), su registro y el mensaje `auth.forbidden` del diccionario se hicieron aquí y no en el bloque 3, para que cada commit siga en verde (sin `ICurrentUser` registrado toda petición fallaba, y el test del diccionario exige un mensaje por cada código). Al bloque 3 le quedan el mapeo a 403 y el título del ProblemDetails. 35 tests nuevos en `Application.Tests` (11 del behavior con operaciones sintéticas mal y bien declaradas; 17 de la tabla operación → permiso de la spec, que además falla si aparece una operación sin listar; 7 con el pipeline real de MediatR, incluido "403 antes que 400" y su control) y 17 en `Infrastructure.Tests` sobre `CurrentUser` y el claim del rol. Mutaciones sobre el código real: Delete de cliente con otro permiso → rojo en la tabla y en el pipeline; validación registrada antes que la autorización → rojo en 3 tests; behavior fail-open → rojo; behavior que no comprueba el permiso → 8 en rojo; quitar el atributo a `GetJobsQuery` → rojo en la tabla.
- **Hallazgo 1 (lo cazó el test de extremo a extremo, el riesgo que la spec pedía verificar):** `JwtBearer` renombra por defecto los claims al validar (`role` → `ClaimTypes.Role`, `sub` → `NameIdentifier`), así que `CurrentUser` no encontraba el rol y **el Owner recibía 403 en todo**. Corregido con `options.MapInboundClaims = false` en `AddInfrastructure` (el `tenant_id` no tiene renombrado y por eso el aislamiento nunca lo notó). Mi primer test unitario del claim validaba con un manejador de JWT suelto y dio falsa seguridad; ahora usa la configuración **real** de `JwtBearer` que registra `AddInfrastructure`, y se comprobó que se pone en rojo con el renombrado activado. `TokenService` escribe ahora el claim como `OfiFlowClaimTypes.Role` (`"role"`) de forma explícita, con test que fija el nombre.
- **Hallazgo 2:** `CurrentUser` aceptaba el rol `"999"` (`Enum.TryParse` acepta cualquier número y, para un valor sin nombre, `ToString()` devuelve ese número); lo cazó su test. Ahora exige además que el valor esté definido.
- **Error de método (corregido):** restauré tres ficheros tras las mutaciones con `git checkout` cuando mis cambios aún no estaban commiteados, y perdí esas tres anotaciones; las reapliqué. Para el resto de mutaciones se usaron copias de seguridad. Lección: `git checkout` solo restaura lo ya commiteado.
- **Bloque 3 (2026-10-06):** `GlobalExceptionHandler` traduce `ForbiddenException` a **403** con `code` `auth.forbidden`, título propio (`problem.title.forbidden`, "Acceso denegado") y el mensaje del diccionario (añadido en el bloque 2). El cuerpo no revela el permiso que faltaba ni el rol (eso va solo al evento `1301`). 2 tests nuevos en `ExceptionMappingContractTests` (una fila de la tabla de excepciones y un test con las comprobaciones de que no se filtra nada). Mutaciones, restauradas desde copias de seguridad: sin el mapeo → rojo (el 403 sale como 500); sin el título del diccionario → rojo; sin el mensaje `auth.forbidden` → rojo (lo detectan además los tests de arquitectura del diccionario).
- **Aparte de la spec (misma rama, commit propio):** `OfiFlow.Api.http` deja de ser la plantilla por defecto y pasa a ser una colección de 33 peticiones para Visual Studio (cuenta, clientes, ciclo de vida de los trabajos, errores a propósito y aislamiento entre dos empresas). **Verificada ejecutando la misma secuencia con `curl` contra una API real** arrancada en un SQL Server desechable en Docker (migraciones reales, puerto 5291, sin tocar LocalDB ni los user-secrets): las 33 peticiones devuelven el estado esperado y el log de la API no tiene errores. No se ha podido ejecutar el propio editor `.http` de Visual Studio, así que la sintaxis de variables de respuesta (`{{loginA.response.body.$.accessToken}}`) está escrita según su documentación pero sin probar en el editor. Gitleaks sobre el fichero: sin hallazgos. Al verificarla se vio que `curl` en este Windows envía mal los acentos pasados como argumento (400 `request.invalid_body`); una API real recibe UTF-8 sin problema, y el script usa ficheros.
- **Bloque 4 (2026-10-06):** `AuthorizationDeclarationRules` (funciones puras: reciben tipos, devuelven incumplimientos) y `AuthorizationArchitectureTests` en `OfiFlow.Api.Tests/Architecture`. Reglas: toda operación declara `[RequiresPermission]` o `[AllowAnonymousRequest]`; no ambas a la vez; y cada permiso declarado existe y lo tiene al menos un rol (**regla añadida respecto a la spec**: una operación que exige un permiso que nadie tiene sería inejecutable hasta para el Owner). Cada mensaje dice el nombre de la operación y qué hacer. 6 tests: uno sobre las operaciones reales (con salvaguarda de que se encuentran al menos las 16 actuales, para que no pase en vacío) y 5 meta-tests con operaciones inventadas (correctas, sin declarar, contradictoria, con permiso inexistente, y que se informan todos los problemas y no solo el primero). Mutaciones sobre el código real, restauradas con `git checkout` (todo estaba ya commiteado): quitar el atributo de `GetJobsQuery` → rojo con su nombre; `DeleteCustomerCommand` anónima y con permiso a la vez → rojo; ningún rol con `Jobs.Assign` → rojo; una operación nueva sin declarar → rojo con su nombre. **Desviación de las mutaciones previstas:** "desactivar el behavior" y "invertir un permiso" no aplican a este test estático (el behavior desactivado ya lo cubren las mutaciones del bloque 2 y la tabla las del bloque 1); se sustituyeron por las dos de arriba que sí ejercitan estas reglas. Solape consciente con `TheTableCoversEveryOperationOfTheApplication` (bloque 2, en `Application.Tests`): aquel exige que la operación esté en la tabla aprobada de la spec; este, que tenga una declaración válida aunque no esté en la tabla.
- **Bloque 5 (2026-10-06):** `RolesFixture` registra al Owner por la API, siembra en la misma empresa un usuario `Admin`, `Manager`, `Employee` y `Technician` (credenciales por `IIdentityService` + `User` + `TenantUser`, ya que no existe forma de invitar) y hace **login real por HTTP con los cinco** (exactamente 5: el límite del login es 5 por minuto; los tokens extra se fabrican con el secreto de la factoría). `RbacEndToEndTests`, 76 tests contra la API real y SQL Server real: **la matriz entera, 13 operaciones × 5 roles = 65 casos**, con una tabla escrita aparte de `RolePermissions` y de la del test de Domain (tres copias independientes); cada caso crea datos nuevos con el Owner, y comprueba que una denegación es un 403 `auth.forbidden` **y que el dato no cambió**, y que un permiso concedido da el éxito esperado **y tuvo efecto** (el trabajo se inicia, se cancela, se completa, se asigna...). Además: 403 antes que 400 (con su control en `Employee`); el mismo 403 para un `Id` de otra empresa y para uno inexistente (con control: el `Owner` recibe 404 para ese cliente ajeno, así que el aislamiento sigue actuando después de la autorización); tokens fabricados sin rol o con rol `Superadmin`, `owner`, `1` o vacío → 403, con control de que un token fabricado con `Owner` sí se acepta; las operaciones anónimas (registro y renovar) siguen funcionando; y la denegación llega al log con el evento `1301`, el usuario, la empresa, el rol y el nombre de la operación, **sin ningún dato de la petición** (se envía un nombre de cliente reconocible y se comprueba que no aparece en ningún registro). Para eso la factoría expone `JwtSecret`, `JwtIssuer` y `JwtAudience` y captura el log (`CapturingLoggerProvider`). Los tests de aislamiento de la spec 007 pasan sin cambios. **Mutaciones sobre el código real (7), restauradas con `git checkout` (todo lo mutado estaba commiteado):** borrar cliente pasa a exigir `Customers.Write` → rojo exactamente en `Manager` y `Employee`; el `Technician` recibe `Customers.Write` → rojo en crear y modificar; el behavior no se registra → 21 en rojo; `CurrentUser` nunca encuentra el rol → 69 en rojo; `MapInboundClaims = true` (el fallo real del bloque 2) → 69 en rojo; `ForbiddenException` sin traducir a 403 → 21 en rojo; autorización después de la validación → exactamente 1 en rojo (el test dedicado). Una primera versión de la mutación de `CurrentUser` no compilaba (mi `sed` producía código inválido) y se rehízo. El test del log falló en su primera versión porque la matriz ya había generado otras denegaciones del técnico en el mismo fixture; ahora compara el recuento antes y después de la petición.
