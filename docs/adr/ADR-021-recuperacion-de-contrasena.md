# ADR-021 — Recuperar la contraseña: respuesta uniforme, suelo de tiempo medido, cola de correo en memoria y todas las sesiones cerradas

**Estado:** Aceptado (2026-11-02). Las decisiones D1–D15 de la [spec 018](../specs/018-password-recovery/spec.md) se aprobaron tal como estaban escritas; los cambios que se hicieron al implementar están en [«Lo que cambió al implementar»](#lo-que-cambió-al-implementar).
**Fecha:** 2026-11-02
**Proyecto:** OfiFlow

---

## Contexto

Hasta ahora **una contraseña olvidada era una cuenta perdida**: no había ninguna vía para volver a entrar sin que alguien con acceso a la base de datos interviniera. Para un primer cliente real es insostenible (cada olvido sería una llamada al desarrollador), y bloquea el piloto.

Con el correo saliente (ADR-020) ya existe el canal. Pero recuperar la contraseña **no es un formulario**: es el flujo más atacado de cualquier sistema con cuentas, porque **quien controla el buzón de una cuenta puede, con este flujo, controlar la cuenta**. Esta decisión es, sobre todo, un modelo de amenazas.

## Problema

- ¿Cómo se pide el restablecimiento **sin revelar si un email tiene cuenta**? (La respuesta, y también el tiempo que tarda.)
- ¿Cómo se entrega el token? En ADR-020 se rechazó una cola persistida porque el correo lleva un secreto; aquí el correo **no puede** enviarse dentro de la petición sin crear un oráculo de tiempo. ¿Qué queda?
- ¿Qué es el token, cuánto vale y cómo se evita que se use dos veces o que haya varios vivos?
- ¿Cómo se evita que OfiFlow sirva para llenar de correo el buzón de otra persona?
- Si el atacante ya tenía una sesión (por eso hay que cambiar la contraseña), ¿qué pasa con ella? ¿Y con el bloqueo de cuenta? ¿Y con el segundo factor?
- ¿Qué pasa sin correo configurado?

## Opciones consideradas

### A. La respuesta de «he olvidado mi contraseña»

**A1. Siempre `202` sin cuerpo y con las mismas cabeceras.** *(elegida)* Exista la cuenta, no exista, esté bloqueada, suspendida o sea de un operador de la plataforma, esté el correo apagado o se haya pasado el límite de la cuenta. Solo un email **mal formado** da `400`, y eso se decide con el texto recibido, sin consultar nada.

**A2. `404` o un mensaje distinto si no existe.** Cómodo para quien se equivoca al teclear, pero entrega la lista de clientes a quien pruebe emails. Se descarta.

### B. Cómo sale el correo

**B1. Dentro de la petición**, como las invitaciones (ADR-020). Tardaría **segundos solo cuando la cuenta existe**: un oráculo de tiempo. En las invitaciones no importaba (quien pide ya está autenticado y puede saber si el email tiene cuenta); aquí quien pide es anónimo.

**B2. Una cola persistida en la base de datos.** Guardaría el correo **con el token en claro**, justo lo que ADR-013 evita (solo se guarda el hash) y la razón de que ADR-020 rechazara el *outbox*.

**B3. Una cola en memoria con un único consumidor.** *(elegida)* El token solo vive en la memoria del proceso unos segundos y la petición no espera al proveedor. Coste asumido: lo pendiente se pierde si la API se reinicia, no hay reintentos y con varias instancias cada una envía lo que recibió. Es aceptable **porque pedirlo otra vez es gratis** para la persona.

### C. El tiempo de respuesta

**C1. Medirlo, y poner un suelo si se distingue.** *(elegida)* El tiempo es la fuga que se olvida. Se midió con la API real y SQL Server real (ver [«La medición»](#la-medición-del-tiempo)): **se distinguía sin dudar**, y el suelo lo oculta.

**C2. Poner un suelo sin medir.** Añade latencia que quizá no hace falta y no sabría decir si funciona.

**C3. No hacer nada.** Es lo que se hace si no se mide.

### D. Al restablecer: las sesiones, el bloqueo y el segundo factor

**D1. Cerrar todas las sesiones y reiniciar el bloqueo, todo en una transacción; no tocar el segundo factor.** *(elegida)* Cerrar las sesiones es lo que hace útil el cambio cuando la contraseña vieja estaba comprometida: sin ello, el atacante que ya tenía sesión la conserva. El bloqueo se reinicia porque quien demuestra que controla el buzón no es quien probaba contraseñas. El segundo factor no se toca porque es la defensa que queda si **el buzón** está comprometido.

**D2. Revocar las sesiones y cambiar la contraseña por separado.** Un fallo entre las dos deja la contraseña cambiada y las sesiones vivas (o al revés). Se descarta.

**D3. Restablecer también el segundo factor por correo.** Anularía justo la defensa que protege de un buzón comprometido. Perder el móvil y los códigos de recuperación sigue resolviéndose como en la spec 010.

### E. Sin correo configurado

**E1. `202`, ninguna fila, un `Warning`.** *(elegida)* Es coherente con la spec 017: sin correo, la función no existe.

**E2. Devolver el token en la respuesta**, como hace la invitación sin correo. Se descarta con mucha claridad: **entregaría la cuenta a quien la pida**.

## Decisión

### 1. Pedir el enlace: `POST /api/v1/auth/forgot-password`

1. **Anónimo, `202` sin cuerpo** (`Cache-Control: no-store`), idéntico byte a byte —estado, cuerpo y cabeceras— en todos los casos de la opción A1. Un test lo compara con una cuenta, sin cuenta, un operador y una cuenta limitada.
2. **El token** es un `OpaqueToken` (256 bits aleatorios, ADR-013): **solo se guarda su hash SHA-256**, **caduca a la hora** (y no a los 7 días de una invitación: el daño de un token robado es mayor, cambia la contraseña de una cuenta que existe) y **se usa una sola vez**. Una cuenta tiene **como mucho uno vigente**: pedir otro anula los anteriores.
3. **Dos límites**, porque abusan de cosas distintas: **5 por hora y por IP** (una política más del limitador global, `429` al sexto) y **3 tokens por hora y por cuenta, en silencio**: las siguientes peticiones reciben el mismo `202` pero no crean token ni encolan nada. El primero frena a quien prueba muchos emails; el segundo protege a la **víctima** de un buzón inundado desde muchas IPs, y es silencioso para no ser un oráculo.
4. **Un operador de la plataforma cuenta como «no existe»**: es otra identidad (otra tabla, otra clave, ADR-016); su contraseña se gestiona con la consola de administración.
5. **El correo se encola, no se envía** (opción B3). Un servicio en segundo plano (`EmailDispatchService`, el segundo tras la purga de ADR-019) lo vacía de uno en uno. La cola es **acotada (100)**: con la cola llena el mensaje se descarta **al instante** (no espera hueco) y se registra sin la dirección; la respuesta no cambia.
6. **Plantilla fija** (`PasswordResetEmail`, función pura): asunto fijo, texto fijo, **ningún dato de la persona**. Lo único que varía es el enlace. Quien pide el restablecimiento de la cuenta de otra persona no consigue que OfiFlow le entregue ningún texto suyo.
7. **El enlace** es `{Email:LinkBaseUrl}/auth/reset-password#token=…`, construido en **un solo sitio** (`EmailLinks`, ADR-020) y solo con la configuración, con el token en el fragmento.
8. **Sin correo configurado**: `202`, **ninguna fila** (no se crea un token que nadie podrá usar) y un `Warning`.

### 2. Restablecer: `POST /api/v1/auth/reset-password`

1. **Anónimo**; recibe `{ token, newPassword }`. La **forma** del token (`OpaqueToken.IsWellFormed`) y la contraseña (las reglas del registro: 8 a 128, el máximo evita ejecutar un hash lento sobre entradas enormes) se validan sin consultar nada. **Límite de 10 por hora y por IP**.
2. **Un solo error** para un token desconocido, caducado, ya usado o anulado por otro pedido: **`400 password_reset.invalid`**. El motivo real va al registro, nunca a la respuesta. Un test compara la respuesta completa de los cuatro casos: es la misma.
3. **Todo o nada, en una sola confirmación** (`SaveChangesAsync`): (a) la contraseña nueva, con el mismo PBKDF2 del registro; (b) el token pasa a usado y los demás de la cuenta se anulan; (c) **todas las sesiones de la cuenta se revocan**, en todas sus empresas (sus access tokens dejan de valer en la petición siguiente, ADR-018); (d) el contador de intentos y el bloqueo vuelven a cero.
   Para que sea de verdad una sola confirmación, las tres piezas **no confirman por su cuenta**: `IPasswordResetService.ConsumeAsync`, `IIdentityService.SetPasswordAsync` (con seguimiento, no con `ExecuteUpdate`, para que el reinicio del bloqueo comparta la transacción) y `ITokenService.RevokeAllRefreshTokensWithoutSavingAsync`. Tres escaneos del código lo vigilan: el caso de uso confirma exactamente una vez, cierra las sesiones con la variante que no confirma, y las tres piezas no contienen `SaveChanges`, `ExecuteUpdate` ni `ExecuteDelete`.
4. **Dos restablecimientos a la vez con el mismo token: gana uno.** El token lleva una columna de versión de fila (`rowversion`); la perdedora falla al confirmar —con su cambio de contraseña y su cierre de sesiones— y recibe `password_reset.invalid`. Se probó con una carrera de verdad contra SQL Server.
5. **El segundo factor no se toca**: ni el caso de uso ni `SetPasswordAsync` contienen la palabra «Mfa» (otro escaneo). **Una empresa suspendida no se desbloquea** (R13): la contraseña cambia, el login sigue dando `403 tenant.suspended`.
6. **No abre sesión.** Se entra con el login de siempre, y con el segundo factor si la cuenta lo tiene: *controlar el buzón basta para elegir una contraseña, no para entrar*.
7. **Aviso de cambio** (`PasswordChangedEmail`): se encola, con asunto y texto fijos y **sin enlace** (no hay nada que abrir). Lo que hace sospechoso un restablecimiento que la persona no pidió es enterarse.

### 3. Dónde vive cada cosa

- **`PasswordResetToken`** (Domain): entidad **global** (la cuenta, no la empresa: sin `ITenantOwned`), sin clave foránea y sin ninguna columna que pueda llevar un email. Está en la lista de entidades globales del test de aislamiento, y **un solo fichero** (`PasswordResetService.cs`) lee la tabla (lista blanca con su motivo). Por eso `ConsumeAsync` está en el servicio de Infrastructure y no en el handler: el `DbSet` es interno.
- **La purga** de ADR-019 borra también los tokens caducados hace más de 7 días, por lotes y con su evento (`1012`). Sin ello la tabla crecería sin límite. Dar de baja a una cuenta borra sus tokens.
- **La cola**: `IEmailQueue` (Application), `EmailQueue` (un canal acotado) y `EmailDispatchService`, con `EmailDispatchOptions` (`Enabled`, `QueueCapacity` de 1 a 10000, validadas al arrancar). **No corre en la consola de administración** (un test lo fija).
- **Frontend**: dos pantallas públicas. La de pedir dice siempre «Si existe una cuenta con ese email, te enviaremos un enlace…» (nunca «te hemos enviado», que no sabe); la de restablecer lee el token del fragmento y **lo borra de la barra de direcciones y del historial al entrar**.

### 4. Eventos de seguridad (familia `10xx`)

| Id | Nivel | Qué dice | Qué **no** lleva |
|---|---|---|---|
| `1012` | Información | Purga de tokens de restablecimiento (cuántos) | hashes, Ids de cuenta |
| `1013` | Warning | Fallo al enviar un correo de la cola, con la **categoría** | dirección, texto del servidor, contenido |
| `1014` | Información | Se pidió restablecer: si la cuenta existía y su `UserId` (**solo en el registro**) | email, token, enlace |
| `1015` | Warning | Un pedido frenado por el límite de la cuenta | email, token |
| `1016` | Información | Restablecida: `UserId` y sesiones cerradas | contraseña, token, email |
| `1017` | Warning | Rechazada, con el **motivo real** (`Unknown`, `Expired`, `UsedOrReplaced`, `PlatformOperator`, `AccountGone`, `Concurrent`) y el `UserId` si se encontró | token, contraseña |

Una ráfaga de `1017` con `Unknown` desde una IP es alguien probando tokens; una de `1015` sobre una cuenta, alguien intentando inundar un buzón; `1014` con «la cuenta existe: False» repetido, alguien probando emails.

### La medición del tiempo

Se midió, con la API real y SQL Server real, alternando las dos series y con cientos de muestras:

| | Mediana con cuenta | Mediana sin cuenta | Estadístico |
|---|---|---|---|
| **Sin suelo** | ≈ 17,3 ms | ≈ 4,8 ms | t de Welch ≈ 54, Mann-Whitney z ≈ 24: **se distinguen sin dudar** |
| **Con suelo de 250 ms** | — | — | t = 1,47 / z = 1,67 y t = 0,96 / z = 1,12 en dos tandas: **indistinguibles** |

La diferencia (≈ 12,5 ms) es el trabajo de crear el token (buscar, contar, anular, insertar, confirmar). El suelo (`ForgotPassword:ResponseFloor`, 250 ms por defecto, entre 0 y 5 s, validado al arrancar) se aplica **en el endpoint, después del trabajo**: si fuera antes, solo retrasaría las dos respuestas por igual y no ocultaría nada (un test de arquitectura lo vigila). 250 ms son unas 7 veces el p99 medido (≈ 34 ms).

## Consecuencias

### Positivas

- **Una contraseña olvidada ya no es una cuenta perdida**, sin que nadie de OfiFlow intervenga.
- **No se puede usar como oráculo** ni de respuesta ni de tiempo, ni de límites (el de cuenta es silencioso).
- **El token no está en claro en ningún sitio persistente**: solo su hash en la base, y el valor en la memoria del proceso unos segundos.
- **Si la contraseña vieja estaba comprometida, el atacante pierde su sesión** en el mismo instante en que cambia la contraseña.
- **Con segundo factor, un buzón comprometido no basta para entrar.**
- **El correo está resuelto una vez más:** el tercer uso será un mensaje y una plantilla.

### Negativas / riesgos aceptados

- **Quien controla el buzón de una cuenta *sin* segundo factor controla la cuenta.** Es el modelo de amenazas de cualquier recuperación por correo. Se mitiga con la hora de caducidad, el uso único, el aviso de cambio y las sesiones cerradas; el remedio de fondo es **obligar el segundo factor a `Owner` y `Admin`** (límite 3b de ADR-014, ya anotado).
- **El suelo de tiempo tiene un límite honesto:** si el trabajo de una cuenta que existe se pasara de 250 ms (una base de datos muy lenta), esa cola volvería a delatarla. Por eso se puede subir por configuración y el valor está muy por encima del p99 medido.
- **El suelo mantiene la petición abierta 250 ms**: es el precio, y el límite de 5 por hora y por IP lo hace inofensivo.
- **La cola en memoria pierde lo pendiente si la API se reinicia, no reintenta y, con varias instancias, cada una envía lo que recibió.** La persona vuelve a pedirlo.
- **«El servidor lo aceptó» no es «llegó a la bandeja».** Sin SPF, DKIM y DMARC (ADR-020) el enlace acabará en el spam justo cuando más se necesita.
- **Los límites son en memoria y por instancia** (ADR-009), y se reinician con la API. Con varias instancias, el efectivo sería su suma; el de cuenta, en cambio, vive en la base de datos y sí se comparte.
- **El registro sigue revelando «ese email ya está registrado».** No lo empeora esta spec; se cerrará con la confirmación de email.
- **Reutilizar el refresh token de una sesión cerrada por el restablecimiento es una reutilización** (ADR-007): revoca las demás sesiones de la cuenta, **incluida la que acaba de abrirse con la contraseña nueva**. Lo vimos en la comprobación en vivo; es el comportamiento existente y correcto, pero conviene saberlo si un cliente móvil reintenta con un token viejo justo después.
- **El aviso no dice desde dónde se cambió la contraseña.** Los datos del dispositivo (ADR-019) existen, pero llevarlos al correo sería un dato personal más hacia el proveedor.

### Lo que cambió al implementar

1. **Los eventos son `1012`–`1017`**, y no estaban asignados en el borrador. Se fijaron al implementar cada bloque.
2. **El suelo de tiempo está en el endpoint**, no en el caso de uso (la spec no decía dónde): el caso de uso no tiene por qué conocer cuánto debe tardar la petición HTTP, y el suelo se prueba por separado con sus propios tests.
3. **Una carrera al crear el token se trata como «límite»**, no como un `500`: un error solo para cuentas que existen sería un oráculo raro pero real.
4. **`ConsumeAsync` vive en `IPasswordResetService`**, no en el handler: la tabla solo la lee un fichero (aislamiento), así que el handler no puede tocar su `DbSet`.
5. **«Usado» y «anulado por otro pedido» salen juntos en el registro** (`UsedOrReplaced`): el dominio deja la misma marca (`UsedAt`) en los dos.
6. **`ResetPasswordCommand.ToString()` no imprime sus propiedades**: un `record` las escribe todas, y dos son secretos.

### Hallazgos de la implementación (útiles para el tribunal)

- **Una mutación colgó el test host.** La de «la cola acepta sin escribir» hizo que un test esperara para siempre un mensaje que nunca llegaba, y mi ejecutor contaba como «detectada» cualquier ejecución fallida: los procesos huérfanos bloqueaban las DLL y 12 mutaciones dieron por buenas sin ni compilar. Se corrigió: una mutación solo cuenta con tests en rojo, un test colgado se mata a los 90 s y un error de MSBuild no cuenta.
- **El método de medir encontró lo que el razonamiento no.** Nadie dudaba de que crear un token tarda más que no crearlo; lo que no se sabía era que la diferencia (12 ms) se distingue con una prueba estadística de manual. El suelo se añadió por la medición, no por la sospecha.
- **Una mutación equivalente que sí interesa.** Quitar el paso a mayúsculas del email en la búsqueda no rompe ningún test porque SQL Server compara sin distinguir mayúsculas. La regla sigue ahí, pero depende de la ordenación de la columna.
- **Un reloj controlado lo prueba todo de una vez.** En los tests de la API el mismo reloj gobierna el segundo factor, el bloqueo y los tokens de restablecimiento: un test avanza una hora sin esperar y comprueba que el enlace caduca, y los de Infrastructure comprueban que lo hace en el instante exacto y no un tick después.
- **El configurador de tema desconecta el Chrome de las pruebas.** `document.startViewTransition` mata el navegador sin interfaz de Karma antes de ejecutar nada; los specs de pantallas lo sustituyen por un doble vacío.

### Recomendación de despliegue (no es código)

1. Todo lo de [ADR-020](ADR-020-correo-saliente.md): proveedor SMTP, dominio propio, SPF/DKIM/DMARC, secretos por variables de entorno y `Email__LinkBaseUrl`. **Sin ello, esta función no sirve:** el correo con el enlace es la única vía.
2. **Una recuperación real de punta a punta** con el proveedor elegido, antes de dar nada por hecho: pedir, recibir, abrir el enlace, cambiar y entrar.
3. **Vigilar `1017`, `1015` y `1013`** (ver la tabla de eventos). Y `1014` con «la cuenta existe: False» desde pocas IPs.
4. **No bajar `ForgotPassword:ResponseFloor`** a cero en producción: lo hace útil el suelo.

### Qué queda cerrado (no se debe reabrir sin una razón nueva y explícita)

- **La respuesta de pedir el enlace es siempre la misma**, y el tiempo también.
- **El token nunca está en claro en un sitio persistente**, y no se devuelve en ninguna respuesta.
- **Restablecer cierra todas las sesiones y no toca el segundo factor.**
- **Un solo error para un token que no sirve**, con el motivo real solo en el registro.
- **Ni la contraseña, ni el token, ni el enlace, ni el email** en logs, eventos ni errores.
- **No hay preguntas de seguridad ni contraseña temporal por correo.**

### Qué queda abierto para revisar más adelante

- **Obligar el segundo factor a `Owner` y `Admin`** (límite 3b de ADR-014): es el remedio de fondo del buzón comprometido.
- **Confirmar el email al registrarse**, que cierra la enumeración por el registro.
- ~~**Cambiar la contraseña estando dentro** (spec 019): otra amenaza (una sesión robada), por eso exige la contraseña actual.~~ **Hecho en la spec 019 ([ADR-025](ADR-025-cambiar-contrasena-dentro.md)):** exige la contraseña actual y, con segundo factor, un código; cierra las demás sesiones y **conserva** esta; y **anula los enlaces de recuperación pendientes**.
- **Cola de correo persistida con reintentos**, con el primer correo que no lleve un secreto.
- **Comprobar la contraseña contra listas de contraseñas filtradas** y una política más exigente que 8 a 128.
- **Recuperación de operadores de la plataforma**, con la consola de administración.
- **Los límites compartidos entre instancias**, si se despliega más de una.
- **Revisar los valores iniciales** (5 y 10 por hora y por IP, 3 por cuenta, una hora de caducidad, 250 ms) con datos reales.

---

## Relación con otros ADR

- Amplía: **ADR-020** (segundo uso del puerto de correo; la cola en memoria que allí se dejó abierta para el primer correo con un secreto no interactivo), **ADR-009** (eventos `1012`–`1017`; el email, la contraseña y el token siguen sin aparecer en ningún registro; límites por IP), **ADR-007** (restablecer cierra todas las sesiones; la reutilización de un refresh token cerrado), **ADR-014** (el bloqueo de cuenta se reinicia al restablecer; el segundo factor no se toca; el límite 3b es el remedio de fondo).
- Relacionada con: ADR-013 (el token con solo su hash; el rol), ADR-018 (los access tokens mueren con su sesión), ADR-019 (la purga y el segundo servicio en segundo plano), ADR-016 (los operadores son otra identidad), ADR-011 (código de error nuevo `password_reset.invalid`), ADR-008 (servidor SMTP de pruebas), ADR-010 (versiones fijadas).
- Ampliada por: la [ADR-025](ADR-025-cambiar-contrasena-dentro.md) (spec 019), la otra mitad: cambiar la contraseña **estando dentro**, con el mismo aviso por correo (`PasswordChangedEmail`) y fijando la contraseña con la misma operación. Un enlace de recuperación vigente **deja de servir** cuando la persona la cambia desde dentro.
- Prepara: la confirmación de email.
