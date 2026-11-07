# Tasks — 022-mfa-obligatorio

**Spec relacionada:** [./spec.md](./spec.md)
**ADR relacionadas:** [ADR-014](../../adr/ADR-014-mfa-y-bloqueo-de-cuenta.md) (cierra su límite 3b), [ADR-012](../../adr/ADR-012-autorizacion-rbac.md) y [ADR-023](../../adr/ADR-023-crear-una-cuenta.md) (se anotan «Ampliada por ADR-024»), [ADR-013](../../adr/ADR-013-gestion-de-usuarios.md) (el patrón de revalidar el rol en cada petición) y la nueva **ADR-024** («Segundo factor obligatorio para `Owner` y `Admin`»)
**Estado general:** En curso (spec aprobada el 2026-11-07; ningún bloque empezado)

---

## Convención

- Cada tarea completable y verificable en una sesión.
- Se marca `[x]` solo cuando cumple la Definición de «Done»: compila, tests en verde, mutaciones detectadas, comprobado en vivo y manual al día.
- **Nivel de riesgo de las pruebas: alto.** Es una puerta de autorización que corre en **cada petición** de los dos roles más poderosos: un fallo hacia un lado deja a un `Owner` fuera de su propia empresa, y hacia el otro deja abierta una cuenta sin su segundo factor. Se aplica la batería completa: matriz rol × estado del factor × operación, carreras y estados límite contra SQL Server real, **medición de las consultas por petición**, escaneos de arquitectura y mutaciones sobre todo lo que decide.
- Las mutaciones se hacen a mano sobre el código real, con copia de seguridad y **`touch` al restaurar**; solo cuenta como «detectada» una con tests en rojo (`[FAIL]`); un test colgado o un error de compilación no cuentan. **Una mutación debe quitar de verdad lo que dice quitar** (lección de la spec 021). Los guiones van **en ficheros** (nunca en `node -e` con comillas invertidas dentro).
- Verificación en vivo con la receta de siempre (base desechable, API real, script fuera del repositorio) y, en el bloque 3, `ng serve` y el navegador integrado.
- Las suites se lanzan **por proyecto, una detrás de otra**. Si `dotnet build` choca con una DLL bloqueada por otro proceso (IIS Express de Visual Studio), se compila a una carpeta aparte con `-p:OutDir` y **no se toca ese proceso**.
- **Orden de entrega:** el backend (bloques 1 y 2) hace que un `Owner` sin factor reciba `403` en todo lo que no sea activarlo, y **el frontend actual no sabe qué hacer con eso**. Por eso **no se fusiona el backend solo**: el PR del backend y el del frontend salen a la vez y se fusionan seguidos.

---

## Bloques

### Bloque 1 — La puerta en la API (Domain + Application + Infrastructure + Api)
- [ ] **Domain.** `SecondFactorPolicy.RequiredFor(TenantRole)` (`Owner` y `Admin`; el resto, no): **el único sitio** que lo decide, con su test exhaustivo sobre los cinco roles.
- [ ] **Application.** Atributo `[AllowedWithoutSecondFactor]` y su lectura en el `AuthorizationBehavior`; `ICurrentUser.SecondFactorEnrollmentRequired` (falso hasta verificar la pertenencia, como `Role`: *fail-closed* en el sentido de no mirar el dato sin verificar). **El orden de las comprobaciones:** primero la pertenencia y el rol efectivo, después el **permiso** (como hoy) y, **al final**, el segundo factor. Así quien no tiene el permiso recibe `auth.forbidden` como siempre, **tenga o no el factor** (ADR-012 R3: quien no puede hacer algo no recibe información sobre ello), y `mfa.enrollment_required` solo lo recibe quien **sí** podría hacer la operación si tuviera el factor. Un test lo fija.
- [ ] **Marcar las cuatro operaciones permitidas:** `GetCurrentAccountQuery`, `GetMfaStatusQuery`, `StartMfaSetupCommand` y `EnableMfaCommand`. Ninguna más.
- [ ] **Infrastructure.** `CurrentUser.VerifyMembershipAsync` rellena el estado del factor **en la misma consulta** (una subconsulta sobre la clave de `MfaCredentials`, `ConfirmedAt` no nulo; uno **pendiente** no cuenta), como ya hace con la suspensión. Un test **cuenta las consultas por petición** y exige que no aumenten.
- [ ] **Error y diccionario:** `mfa.enrollment_required` (`403`), con su texto en `ErrorMessages.resx` (el test del diccionario completo lo exige) y el título del problema.
- [ ] **Evento de seguridad** de la denegación (familia `13xx`, el número se fija aquí): operación, `UserId`, `TenantId` y rol; nunca secretos, códigos ni emails.
- [ ] **Escaneos de arquitectura** (al estilo de los de las specs 018 y 021): la lista **exacta** de operaciones permitidas (con control positivo: son exactamente esas cuatro y llevan permiso de empresa), que el atributo no está en ninguna operación anónima ni de plataforma, y que la comprobación vive **solo** en `AuthorizationBehavior`.
- [ ] **Tests.** *Domain:* la política. *Application:* el comportamiento con dobles (cada rol × con factor, sin factor, con factor pendiente; operación permitida o no; qué pasa antes de verificar la pertenencia; el orden de las denegaciones y que **un `Owner` sin factor y sin permiso recibe `auth.forbidden`**). *Infrastructure (SQL Server real):* el estado del factor en la consulta (activo, pendiente, sin secreto, **cuenta de otra persona**, **cuenta de otra empresa**), el conteo de consultas. *Api, de extremo a extremo:* un `Owner` y un `Admin` sin factor reciben `403 mfa.enrollment_required` en clientes, trabajos, equipo, sesiones y agenda, y **funcionan** `me`, el estado y pedir y confirmar el secreto; el resto de roles sin factor, intactos; un operador de la plataforma, intacto; y el flujo completo (activar → las sesiones se cierran → entrar con el código → todo funciona).
- [ ] **Mutaciones:** la política que deja de incluir `Admin` o incluye `Manager`; la comprobación que no se hace; la que se hace antes de verificar la pertenencia; el factor pendiente que cuenta como activo; la operación permitida que deja de estarlo (cada una de las cuatro) o que se permite una de más; el rol del token en vez del efectivo; la consulta del factor de otra cuenta; el código de error equivocado (`auth.forbidden`); el evento que lleva el email o el código; la excepción para el `Owner`.
- [ ] **Comprobación en vivo** (API real, base desechable): crear una cuenta por el flujo real (spec 021) y comprobar que el `Owner` recién nacido solo puede activar el factor; activar con un código TOTP calculado; entrar con él; y que cada operación de la lista cerrada y cada una prohibida responde lo que debe. Medir el coste por petición (con y sin factor).
- [ ] **[manual]** Diferido al **bloque 4**: el manual se escribe una vez con lo implementado.
- [ ] [externo] Commit del bloque 1

### Bloque 2 — Que no se pueda deshacer, y `me` (Application + Api)
- [ ] **`DisableMfaCommandHandler`:** con un rol que lo exige responde `400 mfa.required_for_role` **antes** de pedir la contraseña ni el código (no hay nada que comprobar: no se puede), con su código en el diccionario y su evento. Quien tiene un rol que no lo exige, o ya no lo tiene, sí puede.
- [ ] **`GET /account/me`:** campo `secondFactorEnrollmentRequired` en `CurrentAccountDto` y en su handler (el mismo dato que usa la puerta, sin una consulta más).
- [ ] **Promover, degradar, reiniciar (R4, R8):** tests de extremo a extremo con el mismo token: un `Technician` promovido a `Admin` queda restringido en su **siguiente** petición; un `Admin` degradado recupera el acceso; a un `Admin` al que un gestor le reinicia el factor, su siguiente inicio de sesión lo deja restringido; una persona **invitada como `Admin`** que acepta la invitación queda restringida en su primera sesión.
- [ ] **Tests y mutaciones:** la desactivación permitida con rol que lo exige, el chequeo que se hace después de pedir la contraseña, el campo de `me` que se calcula con el rol del token, el que dice `false` con un factor pendiente.
- [ ] **Comprobación en vivo:** `me` antes y después de activar; desactivar con y sin rol que lo exige; promover y degradar con la misma sesión.
- [ ] [externo] Commit del bloque 2

### Bloque 3 — Frontend (`ofiflow-frontend`)
- [ ] **Modelo y servicio:** `CurrentAccount` gana `secondFactorEnrollmentRequired`; la constante del código `mfa.enrollment_required`.
- [ ] **Un guardián** (y la lógica del menú) que, si la cuenta debe activar el factor y no lo tiene, lleva **a la pantalla de seguridad** y **solo a ella**; el interceptor trata un `403 mfa.enrollment_required` llevando allí (no cierra la sesión, no entra en bucle).
- [ ] **La pantalla de seguridad** enseña un **aviso fijo** («Tu rol exige el segundo factor…») mientras sea obligatorio y no lo tenga, y **esconde «Desactivar»** a quien tiene un rol que lo exige. Tras activarlo, el flujo de siempre (sesiones cerradas → al acceso con aviso).
- [ ] **Tests (specs):** el guardián (cada combinación de rol y estado), el interceptor (con el código nuevo, sin bucle, sin tocar la sesión), la pantalla (el aviso, el botón escondido, el flujo de activar), el menú reducido y las rutas.
- [ ] **Mutaciones:** el guardián que no redirige, el que redirige siempre, el aviso que no sale, el botón que no se esconde, el interceptor que cierra la sesión o que entra en bucle, el campo que se lee del rol del token.
- [ ] **Comprobación en vivo** (navegador integrado, API real, Mailpit): crear una cuenta de punta a punta y **llegar directamente al asistente de activación** sin poder ir a otro sitio; activarlo con un código TOTP; volver a entrar con el código y ver todo; un `Technician` sin factor que entra y usa todo; y el ancho de móvil.
- [ ] [externo] Commit del frontend (en `ofiflow-frontend`)

### Bloque 4 — Documentación
- [ ] **ADR-024** (`docs/adr/ADR-024-segundo-factor-obligatorio.md`): el modelo de amenazas, **la comparación de D1** (por petición frente a sesión limitada), la ventana de enrolamiento como riesgo aceptado, lo que cambió al implementar, la recomendación de despliegue y lo que queda abierto.
- [ ] Anotaciones «Ampliada por ADR-024» en **ADR-014** (cierra el límite 3b), **ADR-012** (una segunda condición sobre el permiso) y **ADR-023** (la cuenta que nace de la web); `docs/README.md`, el roadmap del `README.md` si procede y `docs/ideas.md` si alguna entrada cambia.
- [ ] Manual: capítulo 0 (qué se puede hacer hoy y qué no), 6 (la parte del segundo factor: ya no es opcional para `Owner` y `Admin`; el login y la sesión restringida), 7 (los códigos nuevos y el recuento real), 8 (el mapa de amenazas, los eventos y los límites: el 3b cerrado), 9 (cifras, la sección de la spec 022, mutaciones, lecciones y errores reales), 12 (glosario e índices) y el anexo del tribunal.
- [ ] `spec.md` pasa a «Implementada» y este `tasks.md` queda completo, con la Definición de Done al día.
- [ ] [externo] Commit del bloque 4 y de `tasks.md`
- [ ] [externo] Push de las dos ramas y PR (backend y frontend), con tu orden

---

## Definición de Done (por bloque)

- Compila sin avisos nuevos y los tests de **todos los proyectos afectados** pasan, lanzados por proyecto.
- Cada regla de la spec tiene al menos un test que falla si se rompe; lo que decide (la política, el orden de las comprobaciones, la lista cerrada, la consulta del factor, la no desactivación, el efecto inmediato) tiene además su mutación detectada.
- Comprobado en vivo contra la API real y, en el bloque 3, en el navegador.
- **Ni el secreto, ni el código, ni los códigos de recuperación, ni el email** en logs, eventos, errores, ejemplos ni tests.
- Gitleaks en local con la misma imagen y orden que la CI (`detect --log-opts="--all"`, sobre **todo el historial**): sin hallazgos nuevos.
- Manual y ADR al día en el mismo bloque o en el 4, nunca desfasados al cerrar la spec.

## Notas / bloqueos

- **Efecto al desplegar:** desde el primer segundo, todo `Owner` y `Admin` **sin segundo factor** queda restringido a activarlo (D4, sin periodo de gracia). Hoy solo hay cuentas de prueba; **antes de que haya clientes reales conviene tener claro el aviso que se les da**.
- **Un `Owner` que ya tiene el factor activo y lo pierde todo** sigue sin recuperación autoservicio (límite 3d de ADR-014): esta spec no lo cambia.
- **Sin migración nueva** (el estado del factor ya está en la base de datos).
- **Dependencias del orden de entrega:** backend y frontend **salen juntos**; no se fusiona el backend solo (ver Convención).
- **Commits, push y PR solo con orden expresa**, uno por uno.
