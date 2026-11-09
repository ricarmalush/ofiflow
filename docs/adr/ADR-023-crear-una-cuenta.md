# ADR-023 — Crear una cuenta: sin cuenta hasta abrir el enlace, contraseña elegida al completar, respuesta y tiempo uniformes y condiciones de uso aceptadas

**Estado:** Aceptado (2026-11-06). Las decisiones D1–D15 de la [spec 021](../specs/021-crear-cuenta/spec.md) se aprobaron tal como estaban escritas; los cambios que se hicieron al implementar están en [«Lo que cambió al implementar»](#lo-que-cambió-al-implementar).
**Fecha:** 2026-11-06
**Proyecto:** OfiFlow

---

## Contexto

Hasta ahora, **la primera persona de una empresa nueva solo podía crearse llamando a la API a mano**: el frontend no tenía pantalla para registrarse. Para un primer cliente real eso bloquea todo lo demás (sin cuenta no hay clientes, trabajos, agenda ni facturas).

Y el registro que existía (`POST /auth/register`) tenía dos problemas de fondo:

- **Decía «ese email ya está registrado»**, que entrega la lista de clientes a quien pruebe emails (el límite 3 del capítulo 6 del manual).
- **No demostraba nada del buzón**: quien escribía un email obtenía una cuenta con ese email, fuese suyo o no.

Con el correo saliente (ADR-020) y el patrón de la recuperación de contraseña (ADR-021) ya existe todo lo necesario. Pero crear una cuenta **no es un formulario**: es la segunda puerta más atacada de cualquier sistema con cuentas, y la elección que más la condiciona es **cuándo existe la cuenta**. Esta decisión es, sobre todo, un modelo de amenazas.

## Problema

- ¿Cuándo nace la cuenta: **al registrarse** (y se bloquea hasta confirmar) o **al abrir el enlace del correo**?
- ¿Quién elige la contraseña y cuándo?
- ¿Cómo se evita revelar si un email ya tiene cuenta (la respuesta, el tiempo y los límites)?
- ¿Cómo se evita que OfiFlow sirva para llenar de correo el buzón de otra persona, **incluido el aviso de «ya tienes cuenta»**?
- ¿Qué se guarda de quien aún no tiene cuenta, durante cuánto tiempo, y cómo se purga?
- ¿Cómo se registran las **condiciones de uso** aceptadas sin que el código decida qué dicen?
- ¿Qué pasa con el registro inmediato que ya existe? ¿Y sin correo o sin condiciones configuradas?

## Opciones consideradas

### A. Cuándo nace la cuenta

**A1. «Pedir → enlace → completar»: no existe ninguna cuenta hasta que se abre el enlace.** *(elegida)* Pedir el registro guarda lo escrito en una tabla de **pendientes** (sin empresa, sin usuario, sin contraseña) y envía un enlace; **la cuenta nace al completar**.

**A2. Crear la cuenta al registrarse, sin confirmar, y bloquear el login hasta confirmar.** Se descarta porque abre el *pre-hijacking*: quien registra el email de **otra persona** deja una cuenta con **su** contraseña, y si la víctima pulsa el enlace de confirmación, **el atacante ya controla la cuenta**. Además obliga a bloquear el login (un caso más que no se confunda con «contraseña mala»), a purgar empresas y usuarios sin confirmar (con el riesgo de borrar lo que no toca) y a migrar el campo `EmailConfirmed`. Con A1 desaparecen las cuatro cosas.

### B. Quién elige la contraseña y cuándo

**B1. Al completar, con el enlace del correo.** *(elegida)* Es lo que mata el *pre-hijacking* aunque alguien abra un enlace que no pidió: **la contraseña la elige quien abre el enlace**. Y evita guardar una contraseña (ni su hash) de alguien que aún no ha demostrado nada.

**B2. Al pedir el registro.** Obligaría a guardar el hash de una contraseña elegida por alguien que no ha demostrado controlar el buzón, y el atacante que registre el email de otra persona habría elegido la contraseña de esa cuenta. Se descarta.

### C. Los datos pendientes

**C1. Una tabla de pendientes.** *(elegida)* Permite que el enlace valga **una sola vez**, que haya **uno vigente por email** y medir el **límite por email**. Se paga guardar 24 h unos datos personales mínimos (nombre, empresa y email), con purga.

**C2. Un token firmado con los datos dentro, sin tabla.** Evita guardar nada, pero **no permite** ninguna de las tres cosas anteriores. Se descarta.

### D. La respuesta de pedir el registro y el tiempo

**D1. Siempre `202` sin cuerpo y con las mismas cabeceras, y un suelo de tiempo medido.** *(elegida)* Igual que ADR-021: exista la cuenta o no, esté el límite superado, el correo apagado o las condiciones sin configurar. Si el email ya tiene cuenta, se envía un **aviso fijo** («ya tienes una cuenta») en vez del enlace, para que quien tecleó bien su email y ya estaba registrado no se quede esperando un correo que no llega.

**D2. Responder distinto (un error) si el email ya existe.** Cómodo, pero es exactamente el problema original. Se descarta.

### E. Sin correo o sin condiciones configuradas

**E1. El registro está cerrado (`202`, nada se crea, un `Warning`).** *(elegida)* Es *fail-closed*: abrir el registro exige configurar **a la vez** el correo y `Legal:TermsVersion`.

**E2. Crear la cuenta ya confirmada cuando no hay correo.** Dejaría abierta una cuenta sin demostrar nada si en producción se olvida configurar el correo. Se descarta.

### F. El registro inmediato (`POST /auth/register`)

**F1. Retirarlo.** *(elegida)* Dos formas de crear una cuenta serían dos formas de saltarse la confirmación del buzón. Lo usaban solo los tests, la colección `.http` y la documentación; el frontend nunca lo usó: es un cambio de contrato **sin clientes que lo sufran**.

**F2. Dejarlo como segunda vía.** Anula A1. Se descarta.

## Decisión

### 1. Pedir el registro: `POST /api/v1/auth/registration`

1. **Anónimo, `202` sin cuerpo** (`Cache-Control: no-store`), idéntico byte a byte —estado, cuerpo y cabeceras— en todos los casos de la opción D1: email nuevo, email que ya tiene cuenta (de una empresa **o de un operador de la plataforma**), límite superado, correo apagado o condiciones sin configurar. Un test lo compara.
2. **Entrada:** `companyName`, `userName`, `email` y `acceptedTermsVersion`, con las **constantes de Domain** que ya usa EF como longitudes máximas. **No hay contraseña.** Un dato mal formado da `400` de campo: depende solo de lo escrito, nunca de si el email existe. La versión de las condiciones debe ser **la vigente** (`Legal:TermsVersion`); si no, `400 registration.terms_not_accepted` en su campo.
3. **El token** es un `OpaqueToken` (256 bits, ADR-013): **solo se guarda su hash**, **caduca a las 24 horas** (no a la hora de ADR-021: aquí no hay ninguna cuenta en juego hasta abrirlo y la persona puede tardar) y **se usa una sola vez**. **Un solo pendiente vigente por email**, garantizado por un **índice único filtrado** (`UsedAt IS NULL`): pedir otra vez anula el anterior y **hace de «reenviar»**.
4. **Tres límites**, porque abusan de cosas distintas: **3 por hora y por IP** (`429` al cuarto), **3 pendientes por hora y por email, en silencio** (en la base de datos), y **3 avisos por hora y por email, en silencio** (en memoria, ver [«Lo que cambió al implementar»](#lo-que-cambió-al-implementar)). Los dos últimos protegen a la **víctima** de un buzón inundado desde muchas IP.
5. **El correo se encola, no se envía** (la cola en memoria de ADR-021). Dos plantillas de función pura y de texto fijo: `RegistrationEmail` (el enlace `{Email:LinkBaseUrl}/auth/complete-registration#token=…`, construido solo en `EmailLinks`) y `AccountExistsEmail` (el aviso, con un enlace **sin token** a recuperar la contraseña).
6. **El tiempo.** Se midió con la API real y SQL Server real (ver [«La medición del tiempo»](#la-medición-del-tiempo)): sin suelo **se distinguía**; con el suelo (`Registration:ResponseFloor`, 250 ms por defecto, de 0 a 5 s, validado al arrancar), **no**. El suelo se aplica **en el endpoint, después del trabajo**.
7. **Cerrado** (correo apagado o sin `Legal:TermsVersion`): `202`, **ninguna fila**, un `Warning`.

### 2. Completar el registro: `POST /api/v1/auth/registration/complete`

1. **Anónimo**; recibe `{ token, newPassword }`. La **forma** del token y la contraseña (8 a 128, como siempre) se validan **sin tocar el token**: una contraseña mala da un `400` de campo y **el enlace sigue sirviendo**. **Límite de 10 por hora y por IP**, con cubo propio (hay un hash PBKDF2 que pagar en cada intento).
2. **Un solo error** para un token desconocido, caducado, ya usado, sustituido por otro pedido **o cuyo email ya tiene cuenta desde que se pidió**: **`400 registration.invalid`**. El motivo real va al registro (`Unknown`, `UsedOrReplaced`, `Expired`, `EmailTaken`, `Concurrent`), nunca a la respuesta. Un test compara la respuesta completa de los cinco casos: es la misma.
3. **Aquí nace la cuenta, todo o nada, en una sola confirmación:** se canjea el pendiente, se crea la cuenta de acceso (el hash PBKDF2), la empresa, el usuario (con la versión y la fecha de las condiciones aceptadas **al pedir**) y su pertenencia como `Owner`. **El orden es deliberado:** primero el canje (barato), después el hash (caro), después las filas y al final la única confirmación: un token que no sirve **no llega a pagar el hash**. `ConsumeAsync` y `CreateUserAsync` **no confirman por su cuenta**; escaneos del código lo vigilan.
4. **Dos aperturas a la vez con el mismo token: gana una.** El pendiente lleva versión de fila; la perdedora falla al confirmar y recibe `registration.invalid`, y **no deja nada**. Los **dos** fallos posibles de la base de datos (la versión de fila y el índice único del email, si la otra llegó a crear la cuenta antes) se capturan **juntos** como `DbUpdateException`: capturar solo el de concurrencia dejaría escapar el otro como un `500`.
5. **No abre sesión** (`204`, sin cuerpo y `no-store`). Se entra con el login de siempre: *un enlace de correo no es una llave de entrada*.

### 3. Las condiciones de uso

- Se guardan en `User` la **versión** y la **fecha** de aceptación. **Qué dicen las condiciones y la política de privacidad no lo decide el código**: lo redacta el dueño del proyecto. La API **solo compara una etiqueta** (`Legal:TermsVersion`, por ejemplo `2026-11`); quien tenga abierto un formulario antiguo recibe un `400` en vez de aceptar sin saber qué.
- **También al aceptar una invitación** (D8): toda persona que crea una cuenta las acepta, no solo quien registra la empresa. Es *fail-closed* igual que el registro: sin versión configurada **ninguna** es la vigente.
- Las cuentas que ya existen **no cambian** (`TermsVersion` nulo); que acepten las condiciones es una spec posterior. `EmailConfirmed` de Identity **no se usa**: la garantía es estructural (una cuenta solo nace tras abrir el enlace).

### 4. Dónde vive cada cosa

- **`PendingRegistration`** (Domain): entidad **global** (todavía no hay empresa: sin `ITenantOwned`), sin clave foránea y sin ninguna columna que pueda llevar la contraseña ni el token. Está en la lista de entidades globales del test de aislamiento, y **un solo fichero** (`PendingRegistrationService.cs`) lee la tabla (lista blanca con su motivo).
- **La purga** de ADR-019 borra también los pendientes caducados hace más de **7 días**, por lotes y con su evento (`1022`): son datos personales de gente que quizá nunca llegó a tener cuenta.
- **El registro viejo se retiró:** `RegisterCommand`, su `Handler` y su `Validator`, el endpoint y la política `auth-register`. Los tests crean ahora sus empresas por el flujo real con un ayudante (`AccountRegistrar`) que lee el enlace del correo capturado. Un test de arquitectura fija que **solo el caso de uso de completar crea un `Tenant`**.
- **Frontend:** `/auth/register` (siempre el mismo mensaje tras pedir, sin afirmar que se envió nada) y `/auth/complete-registration` (lee el token del fragmento y **lo borra de la barra de direcciones y del historial al entrar**); la casilla de condiciones es un componente compartido que también usa la pantalla de aceptar una invitación.

### 5. Eventos de seguridad (familia `10xx`)

| Id | Nivel | Qué dice | Qué **no** lleva |
|---|---|---|---|
| `1018` | Información | Se pidió crear una cuenta: si el email ya tenía cuenta (**solo en el registro**) | email, nombres, token, enlace |
| `1019` | Warning | Un pedido frenado por el límite del email (pendientes o avisos) | email, token |
| `1020` | Información | Registro completado: `TenantId` y `UserId` | contraseña, token, email, nombres |
| `1021` | Warning | Rechazado al completar, con el **motivo real** (`Unknown`, `UsedOrReplaced`, `Expired`, `EmailTaken`, `Concurrent`) | token, contraseña, email |
| `1022` | Información | Purga de pendientes caducados (cuántos y el corte) | emails, nombres |

Una ráfaga de `1021` con `Unknown` desde una IP es alguien probando tokens; una de `1019`, alguien intentando inundar un buzón; `1018` con «el email ya tenía cuenta: True» repetido, alguien probando emails.

### La medición del tiempo

Se midió con la API real y SQL Server real, alternando las dos series (**200 + 200** peticiones, cada una desde una IP de bucle local distinta para que el límite por IP no cortara la prueba, tras 20 + 20 de calentamiento):

| | Mediana con cuenta | Mediana email nuevo | Diferencia | Estadístico |
|---|---|---|---|---|
| **Sin suelo** | 11,38 ms | 14,80 ms | ≈ 3,4 ms | t de Welch = −9,40, Mann-Whitney z = −10,62: **se distinguen sin dudar** |
| **Con suelo de 250 ms** | 266,45 ms | 266,35 ms | 0,09 ms | t = 1,03, z = 0,75: **indistinguibles** |

La diferencia (≈ 3,4 ms) es **menor que la de la recuperación de contraseña** (≈ 12,5 ms) porque aquí el camino de «ya tiene cuenta» **también hace trabajo** (la consulta y el limitador del aviso). Aun así, **se distingue con una prueba estadística de manual**, y por eso el suelo se queda. El suelo se aplica **después** del trabajo: si fuera antes, solo retrasaría las dos respuestas por igual y no ocultaría nada (un test de arquitectura y uno **funcional** lo vigilan: un decorador retrasa el servicio 600 ms con un suelo de 900 ms y la respuesta debe tardar ~900 ms, no ~1.500).

## Consecuencias

### Positivas

- **Una cuenta solo nace tras demostrar el buzón**, y **quien registra el email de otra persona no consigue nada**: no hay cuenta, y si la víctima abre el enlace, **elige ella la contraseña**.
- **Se cierra el límite 3 del capítulo 6:** el registro ya no dice «ese email ya está registrado», y no abre otros (ni por la respuesta, ni por el tiempo, ni por los límites: los dos de email son silenciosos).
- **Nada de lo sensible se guarda de más:** el token solo como hash, la contraseña solo en memoria hasta hashearla, y los datos pendientes con caducidad y purga.
- **Las condiciones aceptadas quedan registradas** con versión y fecha, sin que el código decida su contenido.
- **Una sola forma de crear una cuenta** (más la invitación): no hay camino que se salte la confirmación.

### Negativas / riesgos aceptados

- **El primer usuario de cada empresa nace sin segundo factor.** Con el registro abierto gana importancia; el remedio de fondo es **obligar el segundo factor a `Owner` y `Admin`** (límite 3b de ADR-014, ya anotado).
- **El aviso de «ya tienes cuenta» se limita en memoria y por instancia**, no en la base de datos (no hay fila que contar). Reiniciar la API lo pone a cero; con varias instancias, el efectivo es la suma. El límite por IP sigue activo.
- **Los límites por IP pueden frenar a una oficina entera** que comparta salida a internet (3 por hora para pedir). Es un riesgo anotado, no resuelto aquí.
- **Quien pida el registro con el email de otra persona le envía hasta 3 correos por hora** (el enlace). Es el coste de no revelar si el email existe; el límite por email lo acota y el correo dice que, si no lo pidió, lo ignore.
- **Se guardan datos personales de alguien que aún no tiene cuenta** (nombre, empresa y email) hasta 24 h de vigencia más 7 días de margen de purga.
- **El suelo de tiempo tiene un límite honesto:** si el trabajo de un email nuevo pasara de 250 ms (una base de datos muy lenta), esa cola volvería a delatarlo. Se puede subir por configuración y está muy por encima de lo medido.
- **«El servidor lo aceptó» no es «llegó a la bandeja».** Sin SPF, DKIM y DMARC (ADR-020) el enlace acabará en el spam justo cuando más se necesita.
- **La cola en memoria pierde lo pendiente si la API se reinicia** (ADR-021): la persona vuelve a pedirlo.
- **Un email con cuenta no se puede añadir a otra empresa.** Una cuenta pertenece a una sola empresa (spec 009); «una cuenta en varias empresas» queda abierto.
- **Sin textos legales no hay enlaces en la casilla.** Mientras `environment.legal` esté vacío en el frontend, la casilla nombra los documentos sin enlazarlos. Los textos los redacta el dueño del proyecto y **hay que tenerlos antes de abrir el registro a clientes reales**.
- **Invitar a un email que ya tiene cuenta sigue respondiendo un error** (`user.email_already_registered`): lo ve **quien invita**, que está autenticado y es de la empresa. Es el «hermano» que esta spec no toca.

### Lo que cambió al implementar

1. **Un hueco de la spec (R7): el límite de «3 por email» no cubría el aviso.** Con un email que ya tiene cuenta no se crea ninguna fila que contar, así que sin otro freno quien conociera un email con cuenta podía llenarle el buzón de avisos desde muchas IP. Se añadió `IRegistrationNoticeThrottle` (3 por hora y por email, **en memoria**, clave = huella del email normalizado y **no** el email, registrado como *singleton*: si fuera *scoped* no limitaría nada).
2. **Los eventos son `1018`–`1022`** y se fijaron al implementar cada bloque.
3. **La versión de las condiciones no se comprueba si no hay ninguna configurada**, al pedir el registro: el registro está cerrado de todos modos, y si el `400` dependiera de la configuración interna la respuesta distinguiría «cerrado» de «abierto». En cambio, **al aceptar una invitación sí es *fail-closed***: no hay camino «cerrado» que dé un `202`, y no debe nacer una cuenta sin condiciones.
4. **Los dos fallos de la base de datos al confirmar se capturan juntos** desde el principio (la lección de la spec 009, que se descubrió con SQL Server real y no con la base en memoria).
5. **Se quitó código muerto** de `ConsumeAsync`: un bucle que anulaba «los demás vigentes» del mismo email, imposible con el índice único filtrado, que costaba una consulta de más por canje.
6. **`user.email_already_registered` se queda.** Lo siguen lanzando `IdentityService` (la guarda de último recurso), invitar a un email con cuenta y aceptar una invitación cuando se registró alguien con ese email. Lo que ya no puede ocurrir es verlo **desde un registro anónimo**.
7. **El ayudante de tests no inicia sesión** (el registro viejo tampoco la abría): una sesión de más rompió cuatro tests que cuentan sesiones. El Id de la empresa se lee de la base de datos.
8. **`RequestRegistrationCommand.ToString()` y `CompleteRegistrationCommand.ToString()` no imprimen sus propiedades.**
9. **En el frontend,** la constante `TERMS_VERSION` es la versión que se envía y **hay que subirla a la vez que `Legal:TermsVersion`** de la API; y el mensaje tras pedir dice «recibirás…» y **no** «te hemos enviado», porque la API responde `202` igual en los cuatro casos.

### Hallazgos de la implementación (útiles para el tribunal)

- **El método de medir encontró lo que el razonamiento no, otra vez.** Con una diferencia de solo 3,4 ms, nadie lo habría defendido a ojo; con 400 muestras alternadas y dos pruebas estadísticas es inequívoca. Y al revés: el segundo camino (el de «ya tiene cuenta») **no era gratis**, y la medición lo mostró.
- **Un test de tiempo que falló una vez en paralelo era el único con cota superior.** Los demás solo exigen «no antes del suelo», que la lentitud de la máquina no puede romper. La primera petición a un host recién creado paga costes de arranque que no son lo que se mide; se arregló **por diseño** (una petición de calentamiento descartada y márgenes más anchos), no repitiendo hasta que pasara.
- **Mailpit en Docker Desktop tardaba 2–10 s en saludar por SMTP** (búsqueda DNS inversa del cliente) y el primer envío se perdía por tiempo agotado. `MP_SMTP_DISABLE_RDNS=true` lo baja a 1–3 ms.
- **`sqlcmd` arranca con `QUOTED_IDENTIFIER` apagado y SQL Server exige tenerlo encendido para escribir en una tabla con un índice filtrado.** Un `UPDATE` de la comprobación en vivo fallaba **en silencio** y el enlace «caducado» seguía sirviendo; se detectó porque la respuesta no era la esperada y se verificó con una consulta antes de culpar a la API.
- **Un ayudante de tests también tiene efectos secundarios que otros tests pueden estar midiendo.** Mi primer ayudante entraba para saber el Id de la empresa y dejaba una sesión de más.
- **Una mutación mal planteada no mide nada.** Una «el registro de rechazo lleva el token» sobrevivió porque registraba la palabra «token» y no el token; tres del bloque 4 quitaban la comprobación del estado pero no la petición. Se rehicieron quitando de verdad lo que dicen quitar.
- **Un hueco real de un test de frontend: la casilla desligada de su control.** Ningún test marcaba la casilla con un clic, solo cambiaban el valor del control a mano. La mutación sobrevivió y se añadió el clic.
- **El validador de email de Angular no recorta.** Un espacio al final (pegar, autocompletar) bloqueaba el botón aunque al enviar sí se recorta; lo cazó un test.
- **Una mutación equivalente que sí interesa:** buscar por el email sin pasarlo a mayúsculas no rompe ningún test porque la *collation* por defecto de SQL Server no distingue mayúsculas. La regla sigue ahí, pero depende de la ordenación de la columna.

### Recomendación de despliegue (no es código)

1. Todo lo de [ADR-020](ADR-020-correo-saliente.md): proveedor SMTP, **dominio propio**, SPF/DKIM/DMARC, secretos por variables de entorno y `Email__LinkBaseUrl`. **Sin correo no se puede crear ninguna cuenta.**
2. **`Legal__TermsVersion` configurada** y, en el frontend, `TERMS_VERSION` con **el mismo valor**, más las direcciones de `environment.legal`. **Los textos de las condiciones y de la política de privacidad los redacta el dueño del proyecto** antes de abrir el registro.
3. **Una creación de cuenta real de punta a punta** con el proveedor elegido, antes de dar nada por hecho: pedir, recibir, abrir el enlace, elegir la contraseña y entrar.
4. **Aplicar la migración `AddPendingRegistrations`.**
5. **Vigilar `1021`, `1019` y `1013`** (ver la tabla de eventos), y `1018` con «el email ya tenía cuenta: True» desde pocas IP.
6. **No bajar `Registration:ResponseFloor` a cero en producción:** lo hace útil el suelo.

### Qué queda cerrado (no se debe reabrir sin una razón nueva y explícita)

- **Ninguna cuenta existe hasta que se abre el enlace del correo.**
- **La contraseña se elige al completar**, nunca al pedir, y nunca se envía por correo.
- **La respuesta de pedir el registro es siempre la misma**, y el tiempo también.
- **El token nunca está en claro en un sitio persistente**, y no se devuelve en ninguna respuesta.
- **Un solo error para un token que no sirve**, con el motivo real solo en el registro.
- **Completar no abre sesión.**
- **Ni la contraseña, ni el token, ni el enlace, ni el email, ni los nombres** en logs, eventos ni errores.
- **No hay un segundo camino para crear una cuenta salvo la invitación.**

### Qué queda abierto para revisar más adelante

- ~~**Obligar el segundo factor a `Owner` y `Admin`** (límite 3b de ADR-014): es el remedio de fondo para la cuenta que nace sin él.~~ **Hecho en la spec 022 ([ADR-024](ADR-024-segundo-factor-obligatorio.md)):** la cuenta que nace de la web ya no puede hacer nada salvo activar el factor, y su ventana de riesgo es mínima porque la primera pantalla que ve es la de activarlo.
- **Que las cuentas ya existentes acepten las condiciones**, y una pantalla de «hemos actualizado las condiciones».
- **Una cuenta en varias empresas.**
- **Una comprobación anti-robots** y **registrarse con Google o Microsoft.**
- **Eliminar la cuenta y los derechos del RGPD** (acceso, borrado) como funciones de la aplicación.
- **Correo en varios idiomas** y que el correo diga el nombre de la empresa o de la persona.
- **Los límites compartidos entre instancias**, si se despliega más de una (incluido el del aviso).
- **Revisar los valores iniciales** (3 por hora y por IP para pedir, 10 para completar, 3 por email, 24 horas, 250 ms, 7 días de purga) con datos reales.
- ~~**Cambiar la contraseña estando dentro** (spec 019).~~ **Hecho en la spec 019 ([ADR-025](ADR-025-cambiar-contrasena-dentro.md)).**

---

## Relación con otros ADR

- Amplía: **ADR-021** (el patrón completo —respuesta uniforme, suelo de tiempo medido, cola en memoria, token de un solo uso con versión de fila—, aplicado por tercera vez), **ADR-020** (tercer uso del puerto de correo), **ADR-013** (el token con solo su hash, ahora para una cuenta que aún no existe), **ADR-009** (eventos `1018`–`1022`; datos personales de los pendientes y su purga; los tres límites; el límite 3 del manual, cerrado), **ADR-007** (el modelo `ApplicationUser` + `User` con el mismo Id se sigue creando en una sola confirmación, ahora desde `CompleteRegistrationCommandHandler`).
- Relacionada con: ADR-019 (la purga: los pendientes caducados), ADR-014 (el segundo factor obligatorio para `Owner` y `Admin`, el remedio abierto), ADR-016 (un operador de la plataforma cuenta como «ya tiene cuenta»), ADR-011 (códigos de error nuevos `registration.invalid` y `registration.terms_not_accepted`), ADR-008 (Mailpit y el servidor SMTP de pruebas), ADR-010 (versiones fijadas).
- Ampliada por: la [ADR-024](ADR-024-segundo-factor-obligatorio.md) (spec 022): el riesgo aceptado «el primer usuario de cada empresa nace sin segundo factor» se resuelve obligando el factor a `Owner` y `Admin`; la cuenta que nace de aquí queda **restringida a activarlo** desde su primera sesión.
- Prepara: la spec 019 (cambiar la contraseña estando dentro).
