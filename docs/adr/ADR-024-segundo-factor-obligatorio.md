# ADR-024 — Segundo factor obligatorio para `Owner` y `Admin`: una puerta por petición en la API, una lista cerrada de excepciones y que no se pueda deshacer

**Estado:** Aceptado (2026-11-07). Las decisiones D1–D11 de la [spec 022](../specs/022-mfa-obligatorio/spec.md) se aprobaron tal como estaban escritas (D1, D3 y D4 confirmadas expresamente: restricción por petición en la API, solo `Owner` y `Admin`, efecto inmediato sin periodo de gracia); los cambios que se hicieron al implementar están en [«Lo que cambió al implementar»](#lo-que-cambió-al-implementar).
**Fecha:** 2026-11-07
**Proyecto:** OfiFlow

---

## Contexto

El segundo factor existe desde la spec 010 ([ADR-014](ADR-014-mfa-y-bloqueo-de-cuenta.md)), pero era **opcional**, también para quien más puede: el `Owner` crea usuarios, gestiona roles y ve todos los datos de la empresa, y el `Admin` casi igual. Con la spec 021 ([ADR-023](ADR-023-crear-una-cuenta.md)) la cuenta que nace desde la web **nace sin segundo factor**, así que **la cuenta más valiosa de cada empresa quedaba protegida solo por una contraseña** y por el bloqueo de cuenta y el límite por IP, que frenan el adivinar pero no el *phishing* ni una contraseña filtrada en otro sitio.

Esto lo habían señalado tres ADR anteriores: el **límite 3b de la ADR-014** («obligarlo exige un estado de "sesión solo para activar el MFA" y una pantalla que guíe»), la **ADR-021** («quien controla el buzón de una cuenta sin segundo factor controla la cuenta») y la **ADR-023** («el remedio de fondo es obligar el segundo factor a `Owner` y `Admin`»). La pantalla que guía ya existía (la de seguridad). Faltaba la parte difícil: **cómo se impone sin romper nada y sin que se pueda deshacer.**

## Problema

- ¿**Dónde y cuándo** se impone: en cada petición de la API, o en una sesión «limitada» que se decide al iniciar sesión?
- ¿**A quién**: solo al `Owner`, a `Owner` y `Admin`, o a todos?
- ¿Qué se **permite** a quien debe activarlo y no lo ha hecho, y cómo se evita que una operación nueva quede abierta por olvido?
- ¿Se puede **desactivar** una vez activado? ¿Y qué pasa al promover, degradar o reiniciar?
- ¿Con **periodo de gracia** o con efecto inmediato?
- ¿Cómo se entera la interfaz sin provocar un `403`?
- ¿Cuánto **cuesta** por petición, y cómo se prueba una regla que, para que los cientos de tests existentes sigan valiendo, hay que poder apagar en los tests?

## Opciones consideradas

### A. Dónde y cuándo se impone

**A1. Una comprobación por petición en `AuthorizationBehavior`, con el rol efectivo y sin cambiar el token.** *(elegida)* Es **el mismo patrón** que ya revalida el rol y la pertenencia en cada petición ([ADR-013](ADR-013-gestion-de-usuarios.md)): quien pasa a `Admin` con una sesión abierta queda restringido en su **siguiente** petición, con el mismo token; quien lo activa queda libre en la suya. No hay un tipo de token nuevo, ni un esquema de validación nuevo, ni un estado que viaje con el token.

**A2. Una sesión limitada al iniciar sesión** (un token con un claim «solo para activar el factor», emitido cuando el rol lo exige y no hay factor). Es la forma «de manual», pero añade un tipo de token, un esquema de validación y un estado que **viaja con el token y no cubre la promoción**: quien pasa a `Admin` con la sesión ya abierta conservaría acceso completo hasta renovarla (30 minutos o más). Se descarta.

### B. A quién

**B1. `Owner` y `Admin`.** *(elegida)* Es lo que dice la ADR-014: los dos roles que gestionan usuarios y roles. La lista vive **en un solo sitio** (una política de Domain con su test exhaustivo sobre los cinco roles), de modo que ampliarla es cambiar una línea.
**B2. Solo `Owner`.** Más corto, pero un `Admin` también crea usuarios y cambia roles: una contraseña robada de un `Admin` sería casi tan útil.
**B3. Todos los roles.** Obligaría a cada técnico a instalar una app para consultar su agenda: coste de adopción alto sin un riesgo equivalente. Queda como NEXT, cuando haya una razón.

### C. Qué se permite a quien debe activarlo

**C1. Una lista cerrada de operaciones permitidas, marcadas con un atributo explícito (`[AllowedWithoutSecondFactor]`).** *(elegida)* Es *fail-closed*: una operación nueva se deniega hasta que alguien decida lo contrario, y un test de arquitectura fija **la lista exacta** (cuatro operaciones: quién soy, el estado del factor, pedir el secreto y confirmarlo).
**C2. Una lista de operaciones denegadas.** Olvidaría la próxima que se añada y la dejaría abierta. Se descarta.

### D. Desactivarlo

**D1. No se puede desactivar con un rol que lo exige** (`400 mfa.required_for_role`, **antes** de pedir la contraseña ni el código). *(elegida)* Es la única forma de que «obligatorio» sea cierto: sin esto, activarlo y desactivarlo en el acto anularía toda la spec. Quien deja de tener un rol que lo exige (lo degradan) sí puede quitarlo.
**D2. Permitirlo.** Convierte la obligación en un trámite. Se descarta.

### E. Periodo de gracia

**E1. Efecto inmediato tras el despliegue.** *(elegida)* Hoy solo hay cuentas de prueba; un periodo de gracia añadiría una fecha límite por cuenta, una pantalla de aviso y casos de borde para proteger a nadie. Cuando haya clientes reales, una política de gracia sería otra spec.
**E2. Gracia por cuenta.** Pospuesta (LATER).

### F. Cómo se entera la interfaz

**F1. Dos campos en `GET /account/me`.** *(elegida; la segunda mitad es una desviación de la spec, ver más abajo.)* `secondFactorEnrollmentRequired` («tu rol lo exige y no lo tienes activo») y `secondFactorRequired` («tu rol lo exige, lo tengas activo o no»). Con el primero la interfaz lleva a activar sin provocar un `403`; con el segundo sabe que no debe ofrecer «Desactivar» **sin copiar al cliente qué roles lo exigen**.
**F2. Que la interfaz lo deduzca del rol.** Copia la política al cliente y se desfasa en cuanto cambie. Se descarta.

## Decisión

### 1. La puerta

1. **Dónde:** en `AuthorizationBehavior`, **después** de verificar la pertenencia y el rol efectivo y **después** de comprobar el permiso: `si ICurrentUser.SecondFactorEnrollmentRequired y la operación no lleva [AllowedWithoutSecondFactor] → 403 mfa.enrollment_required`. El orden es deliberado: quien no tiene el permiso recibe `auth.forbidden` como siempre, **tenga o no el factor** ([ADR-012](ADR-012-autorizacion-rbac.md) R3: no se informa sobre lo que no se puede hacer); `mfa.enrollment_required` solo lo oye quien **sí** podría hacer la operación si tuviera el factor.
2. **Quién lo exige:** `SecondFactorPolicy.RequiredFor(TenantRole)` en Domain, con el **rol efectivo** (el de la base de datos en esta petición), nunca el del token.
3. **Qué cuenta como «lo tiene»:** una credencial **confirmada** (`ConfirmedAt` no nulo). Un secreto pedido y sin confirmar **no protege nada** y no libra de la obligación.
4. **Qué se permite (lista cerrada, atributo explícito):** `GetCurrentAccountQuery`, `GetMfaStatusQuery`, `StartMfaSetupCommand` y `EnableMfaCommand`. Nada más: tampoco las sesiones, ni regenerar códigos, ni desactivar. Cerrar sesión y renovarla son anónimas y no pasan por la puerta. El atributo solo puede ir en operaciones **con permiso de empresa** (un escaneo lo comprueba: ni anónimas ni de plataforma).
5. **Lo que no cambia:** las operaciones anónimas, las de plataforma (el operador ya tiene el factor obligatorio sin excepción, [ADR-016](ADR-016-panel-de-operador-de-la-plataforma.md)) y los roles que no lo exigen.
6. **La respuesta:** `403` con el código estable `mfa.enrollment_required` (**no** `auth.forbidden`: la persona está autenticada, puede saber por qué se le deniega y el frontend decide por el código, nunca por el texto). Texto del diccionario: «Tu rol exige el segundo factor. Actívalo para continuar.».

### 2. Ni una consulta más

El estado del factor viaja **en la misma consulta** que la pertenencia y el rol: una subconsulta `EXISTS` sobre `MfaCredentials` por la clave de la propia cuenta del token, como ya viaja la suspensión de la empresa. Un test contra SQL Server real **cuenta las consultas** y exige una (tres veces `VerifyMembership` + leer la marca = una sola), y la comprobación en vivo leyó el SQL real: una sentencia sobre `TenantUsers` con el `EXISTS` de `MfaCredentials` dentro y **ninguna consulta aparte** a esa tabla en una petición normal.

### 3. La costura de pruebas

La regla de a quién se obliga pasa por una interfaz (`ISecondFactorRequirement`) cuya **única implementación registrada en `src` es la real** (`SecondFactorRequirement` → `SecondFactorPolicy`). Los tests de la API **sustituyen** esa implementación por una que no exige a nadie (`NoSecondFactorRequirement`), porque casi todos usan un `Owner` o un `Admin` que **no tratan de esto**: sin la sustitución habría que activar el factor en cada fixture y reescribir cientos de tests por una regla que ninguno prueba. Las clases que **sí** la prueban (`SecondFactorGateFixture`, `EnforceSecondFactor => true`) usan la regla real de producción. Los límites de este diseño están en [«Consecuencias»](#negativas--riesgos-aceptados) y los protegen escaneos de arquitectura (ver «La lista de cosas que un test de arquitectura vigila»).

### 4. Que no se pueda deshacer

- **`POST /account/mfa/disable`** responde `400 mfa.required_for_role` a quien tiene un rol que lo exige. La decisión se toma **lo primero**, con el rol efectivo y *fail-closed* sin rol, y por la misma interfaz que la puerta. Como no se llega a pedir la contraseña ni el código, **un intento no gasta intentos del bloqueo de cuenta ni da pistas** (un e2e repite ocho intentos erróneos y la cuenta no se bloquea).
- **Promover, degradar, reiniciar, invitar como `Admin`:** todo surte efecto en la siguiente petición con el mismo token. Reiniciar el factor de un `Admin` sigue permitido (la jerarquía de la spec 009 manda quién puede; un `Admin` **no** puede reiniciar a otro `Admin`): cierra sus sesiones y su siguiente sesión nace restringida.
- **Activarlo cierra las sesiones** (spec 010, sin cambios): quien lo activa vuelve a entrar con el segundo paso.

### 5. «Quién soy» y el frontend

- `GET /account/me` lleva **dos** campos nuevos (ahora tiene diez), leídos de `ICurrentUser` (cero consultas más). Son **informativos**: quien impone es siempre la API en cada petición.
- **Frontend:** `secondFactorGuard` como `canActivateChild` de la zona privada (se aplica a **toda** ruta hija, también una URL escrita a mano; compara la ruta entera, sin prefijo ni consulta, y no añade `returnUrl`), el menú reducido a «Seguridad», y el interceptor trata `mfa.enrollment_required`: vuelve a pedir la cuenta y, **solo si de verdad debe activarlo y no está ya allí**, navega a la pantalla de seguridad (sin cerrar la sesión, sin reintentar, sin bucle). La pantalla muestra un aviso fijo, **abre directamente el asistente**, esconde «Desactivar» y no pide la lista de sesiones a quien la API se la negaría.

### 6. Eventos de seguridad

| Id | Nivel | Qué dice | Qué **no** lleva |
|---|---|---|---|
| `1304` | Warning | Una operación denegada porque el rol exige el segundo factor y la cuenta no lo tiene activo: operación, `UserId`, `TenantId` y rol | email, contraseña, token, el cuerpo de la petición |
| `1508` | Warning | Alguien con un rol que lo exige intentó desactivar el factor y se le denegó: `UserId`, `TenantId` y rol | contraseña, código (ni se llegan a pedir) |

Una persona legítima casi nunca produce un `1508` (la pantalla esconde el botón): suele ser un cliente a mano, o alguien con un token robado probando qué puede hacer. Un `1304` puede repetirse (cada petición de una sesión restringida) y es **información**, no alarma, salvo una ráfaga desde una IP. Con ellos hay **52 eventos** con identificador estable, y un test (`SecurityEventIdsTests`) impide que se repita un número o que uno se salga de las familias conocidas.

## Consecuencias

### Positivas

- **Una contraseña robada de un `Owner` o `Admin` ya no basta para actuar como él una vez activado el factor**, que es lo que el bloqueo de cuenta y el límite por IP no podían garantizar. Se **cierra el límite 3b** de la ADR-014 y el remedio que pedían las ADR-021 y ADR-023.
- **Efecto inmediato y sin estado nuevo:** promover, reiniciar o activar surten efecto en la siguiente petición, con el mismo token, por el mismo mecanismo que ya revalida el rol.
- ***Fail-closed* por construcción:** una operación nueva con permiso de empresa se deniega a un `Owner` sin factor hasta que alguien la marque, y la lista exacta de las marcadas la fija un test.
- **No se puede deshacer:** «obligatorio» es cierto porque desactivar se rechaza con el rol efectivo, antes de pedir nada.
- **Coste cero en consultas** (una subconsulta en la consulta que ya existía) y **cero esquema nuevo** (ninguna migración).
- **La interfaz no provoca un `403` para enterarse**, y cuando choca con uno (promoción con la sesión abierta) se corrige sola.

### Negativas / riesgos aceptados

- **La ventana de enrolamiento (confianza en el primer uso).** Una cuenta que **todavía no ha activado el factor** tiene una ventana en la que **quien conozca su contraseña puede entrar con la sesión restringida y activar el factor él mismo**, quedándose con la cuenta y dejando fuera a su dueño. Es la confianza en el primer uso de cualquier enrolamiento. Mitigaciones: activarlo **vuelve a pedir la contraseña**, **cierra todas las sesiones**, y para la cuenta que **nace de la web la ventana es mínima** (la persona acaba de elegir su contraseña y la primera pantalla que ve es la de activar). Para las cuentas que ya existen, la ventana es el tiempo hasta su primer inicio de sesión tras el despliegue. **No hay solución sin un canal aparte** (por ejemplo, que el primer factor se active con un código enviado por correo); queda abierto.
- **El TOTP no resiste el *phishing*:** una página falsa que pida contraseña y código en tiempo real se los queda. Passkeys / WebAuthn queda como NEXT.
- **Un `Owner` con el factor activo que pierde el móvil y los códigos** sigue necesitando la herramienta de plataforma (límite 3d de la ADR-014, sin cambios; esta spec no lo empeora para quien ya lo tiene activo, pero hace que ya no baste con quitarle el factor).
- **La mayor parte de la suite de la API corre con la puerta apagada** (la costura de la sección 3). Es un coste consciente: lo que protege que no se debilite es (a) que **un solo fichero** de `src` registra la regla y delega en la política, (b) que **ningún fichero lee una configuración** para apagar la obligación (no existe un interruptor: un valor olvidado en producción no puede desactivarla), (c) que un test arranca **la API real, sin ninguna sustitución**, y comprueba que resuelve la regla real y que exige a `Owner` y `Admin` y a nadie más, y (d) que las clases de la puerta, de `me`, de desactivar y de los cambios de rol usan la regla real.
- **El guardián del frontend es solo interfaz** (se salta con las herramientas del navegador). No importa: la protección real es que la API da `403` a cada petición.
- **Entre el despliegue del backend y el del frontend, un `Owner` sin factor vería errores `403` en todas las pantallas.** Por eso van en el mismo par de PR y no se fusiona el backend solo.
- **El rol que exige el factor se decide en un sitio y se *informa* al cliente en otro** (`secondFactorRequired`): si el cliente lo ignora, solo consigue un botón que da `400`.
- **Las cuentas que tengan una sesión abierta al desplegar** quedan restringidas en su siguiente petición (sin periodo de gracia): hoy son cuentas de prueba; con clientes reales hay que avisar antes.

### Lo que cambió al implementar

1. **Una costura de pruebas (`ISecondFactorRequirement`) que la spec no tenía.** Al ver que cientos de tests de la API usan un `Owner` o un `Admin` sin factor, la alternativa era reescribirlos todos o apagar la regla con una configuración (peligroso: un valor olvidado la desactivaría). La interfaz se registra siempre con la implementación real en producción y los tests la sustituyen; los escaneos de la sección «Consecuencias» vigilan que no se pueda abusar.
2. **Un segundo campo en `me`: `secondFactorRequired`** (desviación de R11, decidida con la persona dueña del proyecto al llegar al frontend). La spec solo nombraba `secondFactorEnrollmentRequired`, que vale `false` en cuanto el factor está activo, y entonces la pantalla no puede saber que ese rol no debe ver «Desactivar». Se añadió en vez de copiar «Owner y Admin» al cliente; es un cambio pequeño (`ICurrentUser.SecondFactorRequired`, sin consulta extra) con tests en tres capas y 8 de 8 mutaciones.
3. **El evento de la desactivación denegada es `1508`, no de la familia `13xx`** que preveía D10: habla del segundo factor y su sitio natural es la familia `15xx`. El de la denegación es `1304`.
4. **El orden «permiso antes que puerta» no se puede observar con un rol real**: hoy `Owner` y `Admin` tienen **todos** los permisos, así que ninguno de los dos recibiría jamás `auth.forbidden`. El test usa un doble que **fuerza** el estado sobre un `Manager` (que no tiene `Customers.Delete`), de modo que si alguien pone la puerta antes del permiso, falla aunque hoy no se pueda dar en producción.
5. **La regla de las entidades globales falló con razón:** `CurrentUser` ahora lee `MfaCredentials` (la subconsulta) y `GlobalEntities_AreOnlyReadInTheAllowList` se negó. Se añadió el fichero a la lista con su justificación (un booleano por la clave de la propia cuenta), no se relajó la regla.
6. **Dos tests que fijan los campos exactos de `me` fallaron a propósito** (uno en Application y otro en la API): un campo nuevo en una respuesta con la regla «nada sensible se cuela por descuido» es una decisión, no un descuido. Se actualizaron a diez campos.
7. **Desactivar decide antes de pedir la contraseña** (R7 lo decía; se probó con ocho intentos erróneos que no bloquean la cuenta). Lo contrario habría gastado intentos del bloqueo con una operación que nunca puede tener éxito.
8. **Hay 52 eventos y 48 códigos de error** (50 y 46 antes), recontados contra el código.

### Hallazgos de la implementación (útiles para el tribunal)

- **Dos mutaciones sobrevivieron y cada una destapó un hueco real.** «Dos eventos con el mismo número» no rompía ningún test porque **nada fijaba que los números fueran únicos**; ahora un test lo comprueba para los 52. Y «navegar aunque la cuenta no se haya podido cargar» sobrevivió en el frontend porque el test que la cubría **no distinguía el caso** (sin cuenta previa, los dos caminos dan «no navegar»); se añadió el caso con una cuenta vieja que dice «debe activarlo» y un refresco fallido (la conducta elegida: no mover a nadie por un dato sin confirmar).
- **Una mutación debe quitar de verdad lo que dice quitar** (la lección de la spec 021, otra vez): «la puerta vuelca la petición entera al registro» solo se detecta con un test que **manda la contraseña en el cuerpo** de la operación denegada y comprueba que no está en ningún registro; con `GetCustomersQuery`, que no lleva campos, no habría salido nunca.
- **El servidor rechaza cualquier paso TOTP anterior al último usado, no solo el repetido.** Mi primer selector de códigos para la comprobación en vivo elegía a veces el paso `−1` (válido por la ventana) y fallaba con `400`; el selector correcto elige siempre un paso **mayor** que el último consumido y espera al siguiente si no hay. Un fallo de un guion de prueba, no de la API, pero enseña cómo es la regla.
- **El campo exacto de `me` es un buen detector:** los dos tests que lo fijan pillaron el campo nuevo al primer intento, y obligaron a decidir en cada capa.
- **Un escaneo de código para algo que ya existía** (`SecurityArchitectureTests`) frenó un cambio legítimo con una explicación clara: así es como debe comportarse una red de seguridad.
- **Un resultado raro en el navegador no es necesariamente un fallo:** el primer clic de un botón a veces no se registra en el navegador integrado y la captura pilla la animación de entrada (se ve el contenido doble). Se verificó cada paso por la URL y la red antes de dar nada por bueno.
- **Prettier no estaba limpio en el repositorio del frontend:** se formateó solo lo que ya cumplía antes, para no ensuciar el historial con cambios ajenos.

### La lista de cosas que un test de arquitectura vigila

- Las operaciones permitidas son **exactamente** las cuatro, llevan permiso de empresa (solo `Account.Read` o `Mfa.Manage`) y **no** son anónimas ni de plataforma; ninguna operación de clientes, trabajos o equipo lleva la marca.
- La puerta va **después** de la comprobación del permiso.
- `mfa.enrollment_required` solo lo lanza `AuthorizationBehavior`; la decisión por rol solo vive en la política (y en la implementación de la interfaz que la envuelve).
- La regla real es **la única** registrada en `src`, delega en la política y no consulta configuración ni entorno; y la API real la resuelve.

### Recomendación de despliegue (no es código)

1. **Backend y frontend juntos**, fusionados seguidos. Entre uno y otro, un `Owner` sin factor vería errores `403`.
2. **Avisar antes a las personas con una cuenta `Owner` o `Admin` sin segundo factor:** desde la primera petición tras el despliegue solo podrán activarlo. Necesitan una app de autenticación (Google Authenticator, Microsoft Authenticator, Aegis…).
3. **Tener lista la herramienta de plataforma** (límite 3d de la ADR-014) antes de abrir a clientes reales: quien pierda el móvil y los códigos no se recupera solo.
4. **Probar de punta a punta con una cuenta real** antes de dar nada por hecho: crear la cuenta (spec 021), ver el aviso, activar el factor, volver a entrar con el código y comprobar todas las pantallas.
5. **Vigilar `1304` y `1508`:** una ráfaga de `1304` desde una IP es un cliente o una sesión robada tanteando; `1508` casi nunca debería aparecer.
6. **No hay un interruptor por configuración, a propósito:** si hace falta aflojar la obligación, es un cambio de código y de despliegue.

### Qué queda cerrado (no se debe reabrir sin una razón nueva y explícita)

- **La restricción es por petición en la API, con el rol efectivo.** No hay token «limitado».
- **Lo no marcado se deniega** (lista cerrada de operaciones permitidas).
- **No se puede desactivar con un rol que lo exige.**
- **Un código propio, `mfa.enrollment_required`**, distinto de `auth.forbidden`.
- **La decisión por rol vive en un solo sitio**, y no se copia al cliente.
- **No hay periodo de gracia ni excepción «porque es el dueño»** (NEVER).
- **Ni contraseñas, ni códigos, ni secretos, ni el cuerpo de la petición en los registros.**

### Qué queda abierto para revisar más adelante

- **Passkeys / WebAuthn** (resistentes al *phishing*).
- **Obligar el factor a más roles** (`Manager`, `Technician`, `Employee`), cuando haya una razón.
- **Que Equipo enseñe quién no lo ha activado.**
- **Recuperar a un `Owner` que lo pierde todo** desde la herramienta de plataforma (límite 3d).
- **Un periodo de gracia por cuenta** y **«recordar este dispositivo»**, cuando haya clientes reales; avisos por correo al promover a alguien.
- **Cerrar la ventana de enrolamiento** con un canal aparte (primer factor por correo), si el riesgo lo pide.
- ~~**Cambiar la contraseña estando dentro** (spec 019).~~ **Hecho en la spec 019 ([ADR-025](ADR-025-cambiar-contrasena-dentro.md)).**

---

## Relación con otros ADR

- Cierra: el **límite 3b de la [ADR-014](ADR-014-mfa-y-bloqueo-de-cuenta.md)** («el segundo factor es opcional también para `Owner` y `Admin`»).
- Amplía: **ADR-012** (una segunda condición sobre el permiso, en el mismo sitio y *fail-closed*), **ADR-014** (obligatorio para `Owner` y `Admin`; `mfa.required_for_role`), **ADR-023** (la cuenta que nace de la web ya no se queda sin segundo factor), **ADR-013** (el patrón de revalidar el rol en cada petición, ahora también para el factor), **ADR-009** (eventos `1304` y `1508`; el catálogo pasa a 52).
- Relacionada con: ADR-016 (el operador de la plataforma ya lo tenía obligatorio sin excepción), ADR-021 (quien controla el buzón de una cuenta sin factor la controla: ahora, solo hasta que active el factor), ADR-011 (códigos nuevos `mfa.enrollment_required` y `mfa.required_for_role`), ADR-008 (el SQL Server real que mide las consultas), ADR-018 (la sesión ligada al access token: activar cierra las sesiones).
- Prepara: passkeys y la herramienta de recuperación de un `Owner`.
