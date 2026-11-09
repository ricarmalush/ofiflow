# Anexo — Preguntas probables del tribunal

Este anexo reúne las preguntas más probables de un tribunal o de una entrevista de seguridad de aplicaciones, con una respuesta corta y el capítulo donde profundizar. **No es para memorizar**: es para comprobar que puedes explicar cada cosa con tus palabras. Si una respuesta no te sale sin mirar, vuelve al capítulo indicado.

## Cómo contestar bien

Las respuestas que mejor funcionan siguen casi siempre la misma forma, en cuatro partes:

1. **Qué se decidió** (una frase).
2. **Por qué** (el problema que resuelve).
3. **Qué se descartó o qué cuesta** (toda decisión tiene un precio).
4. **Cómo se comprueba** (un test, un guardarraíl, un evento de seguridad).

Y una regla de oro: **di siempre lo que no está hecho.** Un tribunal desconfía de quien no tiene límites y respeta a quien los conoce y sabe cuál cerraría primero. Cada capítulo tiene su sección "Dónde están hoy los límites" y el final de este anexo los resume.

---

## 1. Visión y arquitectura

**¿Qué es OfiFlow y para quién es?**
Un SaaS para autónomos y pequeñas empresas de servicios (fontanería, electricidad, climatización...). Hoy es solo una API: gestiona clientes, trabajos y el equipo de cada empresa. El frontend, la agenda, los presupuestos y las facturas son fases posteriores. *(Cap. 0)*

**¿Qué arquitectura usa y por qué?**
Clean Architecture con DDD, CQRS pragmático (una aplicación, una base de datos) y monolito modular, sobre .NET 10, EF Core, SQL Server y MediatR. Lo condiciona un hecho: **lo construye una sola persona**, así que se prefiere lo simple y mantenible a lo sofisticado. *(Cap. 2, ADR-003 y 005)*

**¿Qué depende de qué?**
Las dependencias apuntan **hacia dentro**: Domain (las reglas) no depende de nada; Application depende de Domain; Infrastructure y Api dependen de Application. Un test de arquitectura lo vigila. Así las reglas se prueban sin base de datos ni servidor. *(Cap. 2 y 3)*

**¿Por qué un monolito modular y no microservicios?**
Para una persona con unas 25 horas a la semana, los microservicios añadirían despliegue, red y consistencia distribuida sin beneficio. Los módulos son carpetas por contexto dentro de cada capa; si algún día hiciera falta separar uno, la frontera ya existe. *(Cap. 2, ADR-005)*

**¿Para qué MediatR y CQRS?**
Cada operación es un mensaje con su handler, y los pasos comunes (autorización y validación) son *behaviors* que se aplican a todas **sin poder olvidarlos**. Es "pragmático": sin bases separadas de lectura y escritura ni *event sourcing*. *(Cap. 4, ADR-003)*

**¿Por qué no hay un repositorio genérico?**
EF Core ya hace de repositorio y de unidad de trabajo; envolverlo duplicaría código sin aportar. Application depende de `IApplicationDbContext`. *(ADR-006)*

## 2. Multi-tenancy (que una empresa no vea los datos de otra)

**¿Cómo se aísla a las empresas?**
Una sola base de datos con un `TenantId` en cada entidad de empresa, y un **filtro global de EF Core** que añade `TenantId == <empresa actual>` a toda consulta, automáticamente. La empresa sale del **token firmado**, nunca de lo que mande el cliente. *(Cap. 5, ADR-002)*

**¿Y si un programador olvida el filtro?**
No se puede olvidar: se configura **una sola vez, por reflexión**, sobre toda entidad con la marca `ITenantOwned`. Y un test falla si una entidad tiene `TenantId` sin la marca, o si no está clasificada como "de empresa" o "global". *(Cap. 5)*

**¿Qué pasa si alguien añade `"tenantId": "otra empresa"` al JSON?**
Nada: ningún comando tiene ese campo, así que no hay dónde guardarlo. Y un test de arquitectura impide que un comando lo acepte. *(Cap. 4, 5)*

**Una empresa pide un cliente de otra. ¿Qué recibe?**
El mismo **404** que si no existiera: no se revela que existe en otra parte. Está comprobado por HTTP contra SQL Server real. *(Cap. 5)*

**¿En qué sitios se salta el filtro?**
En **tres**, todos con motivo y en una lista blanca que vigila un test: el login y el refresh (todavía no hay empresa activa) y aceptar una invitación (la persona aún no tiene cuenta). *(Cap. 5)*

## 3. Autenticación

**¿Cómo se guardan las contraseñas?**
Con el `PasswordHasher` de ASP.NET Core: **PBKDF2**, lento a propósito y con sal. Nunca en claro ni cifradas (un hash no se puede deshacer). Mínimo 8 caracteres y máximo 128, para no ejecutar el hash sobre entradas enormes. *(Cap. 6)*

**¿Qué lleva el token de acceso y cuánto dura?**
Es un JWT firmado (HMAC-SHA256) con el usuario, la empresa, el rol y el **Id de su sesión** (`sid`); dura **30 minutos**. El secreto con el que se firma no está en el repositorio: en local vive en *user-secrets* y en producción vendría de una variable de entorno o un gestor de secretos. *(Cap. 6, ADR-009)*

**¿Y el refresh token?**
64 bytes aleatorios; en la base de datos solo se guarda su **hash**. Cada uso lo **rota** (emite uno nuevo y revoca el anterior). Si alguien presenta uno ya revocado, se interpreta como robo y se revoca toda la cadena del usuario. *(Cap. 6, ADR-007)*

**Si roban un token de acceso, ¿qué pasa?**
Depende de si su dueño ya cerró la sesión. **Si la cerró** (cerrar sesión, «cerrar todas mis sesiones», activar o desactivar el segundo factor, o quitar a esa persona de la empresa), el token **deja de valer en la petición siguiente**: lleva el Id de su sesión y la API comprueba en cada petición que esa sesión siga activa (`401`). Hasta la spec 015 no era así: un JWT no se puede anular y valía hasta 30 minutos. **Si no la cerró**, sigue valiendo hasta que caduque (esto no detecta que lo use otra persona), **pero** el rol y la pertenencia a la empresa se revalidan en la base de datos en cada petición, así que un token de alguien al que se quitó de la empresa no autoriza nada. El coste de poder anularlo es una consulta más por petición (medida: ~1,5 ms de mediana en local, por un índice que la cubre entera). *(Cap. 6 y 8, ADR-018)*

**¿Cómo se frena la fuerza bruta?**
Con un límite de peticiones por IP: 5 logins por minuto, 3 pedidos de registro por hora, 10 registros completados por hora, 10 aceptaciones de invitación por hora. Al pasarse, un 429 y un evento de seguridad. **Además, desde la spec 010, **cada cuenta se bloquea 15 minutos tras 5 fallos** aunque los intentos vengan de IP distintas, y el intento se reserva antes de comprobar la contraseña para que 50 peticiones simultáneas no den 50 intentos. **Límite honesto:** quien conozca un email puede mantener esa cuenta bloqueada fallando a propósito. *(Cap. 6 y 8)*

**¿Se puede saber si un email está registrado?**
El login responde un único 401 para "email desconocido", "contraseña incorrecta" y "cuenta bloqueada", y tarda lo mismo en los tres (se hace siempre una verificación completa, contra un hash ficticio si no hay cuenta). El registro, que antes sí decía «ya está registrado», se cerró con la spec 021: pedir crear una cuenta responde siempre lo mismo y tarda siempre lo mismo (ver «Crear una cuenta»). *(Cap. 6, 8)*

**¿Por qué TOTP y no SMS?**
El SMS es el factor más débil (se secuestra la línea con un duplicado de SIM) y exige un proveedor de pago con un dato personal más. El TOTP es gratis, no depende de nadie y lo entiende cualquier app. **Límite honesto:** no resiste al *phishing*; las *passkeys* sí, y son el siguiente paso. *(ADR-014; cap. 6)*

**¿Por qué el secreto TOTP se cifra y no se hashea?**
Porque el servidor necesita el secreto **en claro** para calcular el código de este instante: un hash no se puede deshacer. Se cifra con AES-256-GCM (cifrado autenticado), con la clave **fuera** de la base de datos y atado al Id de la cuenta, de modo que un secreto copiado a la fila de otra cuenta no se descifra. *(Cap. 6)*

**¿Qué pasa si llegan 50 intentos de login a la vez contra la misma cuenta?**
Que solo 5 llegan a comprobar la contraseña. El intento se **reserva con una sola sentencia SQL atómica antes de comprobarlo**; con «leer el contador, comprobar y sumar si falla», las 50 leerían «0 fallos» y el atacante tendría 50 intentos. Hay un test con 50 peticiones simultáneas contra SQL Server real, y otro por HTTP. *(Cap. 6, 9)*

**¿Por qué la contraseña correcta no reinicia el contador del bloqueo en una cuenta con segundo factor?**
Porque si lo reiniciara, quien ya conoce la contraseña probaría 4 códigos, pondría la contraseña para volver a cero, probaría 4 más… y adivinaría un código de 6 dígitos sin límite. Solo un acceso completo (contraseña y código) lo reinicia. *(Cap. 6)*

**¿Por qué SHA-256 basta para los códigos de recuperación y no para una contraseña?**
Una contraseña la elige una persona, así que existe un diccionario de candidatos y hace falta un hash lento. Un código de recuperación es aleatorio de 80 bits: no hay diccionario, y probar 2^80 candidatos es inviable aunque se robe la base de datos. *(Cap. 6)*

**¿Qué error real encontrasteis con el segundo factor?**
Uno que solo cazó el test de extremo a extremo: con 4 fallos previos, el quinto intento reserva, llega a 5 y bloquea la cuenta *antes de saber si la contraseña es buena*; con segundo factor, devolver la reserva no levantaba el bloqueo y una persona legítima quedaba fuera 15 minutos. Los tests de cada pieza pasaban; lo cazó el recorrido entero. Se corrigió y hay un test en tres niveles. *(Cap. 6, 9)*

## 4. Autorización y gestión de usuarios

**¿Cómo funciona el control de roles?**
Cinco roles (`Owner`, `Admin`, `Manager`, `Technician`, `Employee`) y ocho permisos más dos de usuarios, con el formato `Entidad.Acción`. Una **tabla única** dice qué permisos tiene cada rol; cada operación **declara** el suyo. *(Cap. 8, ADR-012)*

**¿Por qué se comprueba en Application y no en los endpoints?**
Para que valga igual venga la petición de HTTP, de la IA o de otro cliente, y para que **no se pueda olvidar** en un endpoint. Además es *fail-closed*: una operación sin permiso declarado se deniega, y un test de arquitectura impide que exista. *(Cap. 8)*

**¿Qué diferencia hay entre `Owner` y `Admin`?**
Tienen los mismos permisos, pero el `Owner` es único por empresa e **intocable** (nadie lo cambia ni lo quita) y es el único que puede nombrar o quitar `Admin`. Un `Admin` gestiona a `Manager`, `Technician` y `Employee`. *(Cap. 8)*

**¿Cómo evitas la escalada de privilegios al gestionar usuarios?**
Una segunda tabla (`ManageableRoles`) dice a quién puede gestionar cada rol. Nadie se gestiona a sí mismo, nadie toca al `Owner`, y **cambiar un rol exige poder gestionar el rol actual y el nuevo**: un `Admin` no asciende a nadie a `Admin`. Cada celda tiene su test. *(Cap. 8)*

**Despiden a un empleado a las 10:00 y su token caduca a las 10:30. ¿Qué puede hacer a las 10:15?**
Nada: en cada petición se comprueba en la base de datos que sigue en la empresa, así que recibe un 403 y queda un evento `1302`. Antes de la spec 009 el rol viajaba en el token y habría conservado el acceso. El coste es **una consulta indexada más** por petición, medida con un test. *(Cap. 8, ADR-013)*

**Un token firmado dice "Owner" pero su dueño es `Technician`. ¿Qué rol tiene?**
`Technician`: el rol sale de la base de datos y el del token es solo informativo. *(Cap. 8)*

**¿Cómo funciona una invitación?**
El `Owner` o un `Admin` invita con un email y un rol. Se genera un token de **256 bits** del que **solo se guarda el hash**; caduca a los 7 días y se usa una vez. La persona lo acepta (ruta anónima, 10 por hora y por IP) y **elige ella su contraseña**. Un token inventado, caducado, revocado o usado reciben **exactamente la misma respuesta**. Todo se confirma en una transacción y, con dos aceptaciones a la vez, gana una. *(Cap. 6)*

**¿Qué riesgo tiene devolver el token al invitante?**
Que el invitante lo ve y podría aceptarlo él mismo. Fue una decisión consciente mientras no había correo; lo que **nunca** pasó es que conociera la contraseña de la otra persona. **Desde la spec 017 el riesgo desaparece cuando hay correo configurado**: la API envía el enlace a la persona invitada y responde `"token": null`, así que quien invita nunca ve el token. Sin correo configurado (desarrollo, tests, demos) sigue como antes, y en `Production` un aviso al arrancar lo recuerda. *(ADR-013, ADR-020)*

**¿De dónde sale el enlace del correo, y por qué no de la petición?**
Solo de `Email:LinkBaseUrl`, un valor que fija el despliegue y que se valida al arrancar (`https`, sin ruta). Si saliera de la cabecera `Host` u `Origin`, quien envía la petición decidiría adónde apunta el correo que recibe la víctima (*envenenamiento por cabecera Host*): el correo, al salir de OfiFlow, sería de fiar y llevaría a otra web. Hay un test que envía una invitación con `Host`, `Origin`, `Referer` y `X-Forwarded-Host` de un tercero y comprueba que el enlace no cambia. El token va en el **fragmento** (`#token=…`), que el navegador no envía al servidor. *(Cap. 6, ADR-020)*

**¿Qué pasa si el correo no se puede enviar? ¿Y por qué no hay una cola?**
La respuesta es un **502** (`invitation.email_not_sent`, sin el motivo) y **no se crea ninguna invitación**: se envía *antes* de guardar, así que no hay nada que deshacer ni una invitación pendiente que nadie recibió. No hay cola porque una cola persistida guardaría el correo, y ese correo **contiene el token en claro**, justo lo que se evita guardando solo el hash. Se hará con el primer correo no interactivo (un recordatorio), y entonces habrá que decidir cómo no guardar secretos en ella. La spec 018 añadió una cola **en memoria**, sin persistencia, para el correo de recuperar la contraseña, justo por esa razón. *(ADR-020, ADR-021)*

**¿Qué datos nuevos salen de OfiFlow con el correo?**
La dirección del invitado y el nombre de la empresa (en el cuerpo) salen hacia el **proveedor de correo**, que pasa a ser un *encargado del tratamiento*: hay que resolver el contrato y dónde trata los datos al elegirlo. Ni la dirección, ni el token, ni el enlace aparecen en ningún log; los eventos `1407`/`1408` llevan solo Ids y la categoría del fallo, y hay un test con la API real que lo comprueba. *(Cap. 8, ADR-020)*

**¿Cómo evitas que OfiFlow sirva para llenar de correo a un tercero?**
El asunto es fijo y **nada de lo que escribe quien invita entra en el cuerpo** (solo el nombre de la empresa, el rol y la caducidad), así que no sirve para escribir un texto cualquiera; hay un máximo de 25 invitaciones pendientes por empresa, una pendiente por email y **20 invitaciones por hora y por persona**. Ese último límite lo consulta el endpoint y no el limitador global, porque este se evalúa antes de autenticar y solo conoce la IP (y una oficina entera comparte IP). *(Cap. 6 y 8, ADR-020)*

### Recuperar la contraseña (spec 018)

**¿Cómo se recupera una contraseña olvidada sin regalar la cuenta?**
En tres pasos y negándole algo al atacante en cada uno. Pedir el enlace (`forgot-password`) responde **siempre `202` sin cuerpo**, exista la cuenta o no, esté bloqueada, suspendida, sea de un operador o el correo esté apagado, y **tarda siempre lo mismo** (un suelo de 250 ms); el correo no se envía en la petición sino que se **encola** y lo envía otro servicio. El token es de 256 bits, **solo se guarda su hash**, caduca a la hora y vale una vez. Al restablecer (`reset-password`) cambian a la vez, en **una sola confirmación**, la contraseña, el token (usado), **todas las sesiones** (cerradas) y el bloqueo (a cero); no abre sesión ni toca el segundo factor. *(Cap. 6, ADR-021)*

**¿Por qué no basta con devolver siempre lo mismo?**
Porque el tiempo también habla. Sin más, crear el token cuesta ≈ 12 ms más que no crearlo, y con cientos de muestras la diferencia se distingue con una prueba estadística (t de Welch ≈ 54). **Se midió, no se supuso**, y se añadió un suelo de 250 ms aplicado *después* del trabajo; con él, dos tandas dieron t = 1,47 y 0,96 (indistinguibles). **Límite honesto:** si el trabajo de una cuenta que existe se pasara del suelo, volvería la diferencia. *(Cap. 6 y 8, ADR-021)*

**¿Por qué el correo de recuperación va a una cola en memoria?**
Enviarlo en la petición tardaría segundos solo cuando la cuenta existe (otro oráculo de tiempo); guardarlo en una cola de la base de datos obligaría a guardar el token **en claro**, que es lo que el diseño evita. En memoria vive unos segundos. El coste: si la API se reinicia, lo pendiente se pierde y no hay reintentos; es aceptable porque pedirlo otra vez es gratis. *(ADR-021)*

**Si roban la contraseña de alguien, ¿qué impide que el atacante se quede dentro tras recuperarla?**
Que restablecer **cierra todas las sesiones** de la cuenta, en todas sus empresas, en la misma confirmación que cambia la contraseña: el atacante que ya tenía sesión la pierde y sus tokens de acceso mueren en la petición siguiente. Y si había segundo factor, ni siquiera con el buzón puede entrar: el restablecimiento no lo toca. *(Cap. 6, ADR-021)*

**¿Y si dos personas usan el mismo enlace a la vez?**
Gana una. El token lleva una columna de versión de fila; las dos preparan sus cambios, pero solo un `UPDATE` encuentra la fila como la leyó, y la perdedora falla al confirmar —sin cambiar la contraseña ni cerrar sesiones— y recibe el mismo `password_reset.invalid`. Se probó con una carrera de verdad contra SQL Server. *(Cap. 6)*

### Crear una cuenta (spec 021)

**¿Cuándo existe la cuenta de alguien que se registra?**
**Solo cuando abre el enlace que le llega por correo.** Pedir el registro (`POST /auth/registration`) guarda lo escrito en una tabla de pendientes y envía un enlace, pero no crea ninguna cuenta; al abrirlo, la persona **elige su contraseña** y se crean de una vez la empresa, el usuario y su pertenencia de `Owner`, en una sola confirmación. *(Cap. 6, Parte 10; ADR-023)*

**¿Qué ataque evita eso, y por qué no se crea la cuenta «sin confirmar»?**
El *pre-hijacking*. Si la cuenta se creara al registrarse y se bloqueara hasta confirmar, quien registrara el email de **otra persona** dejaría una cuenta con **su** contraseña, y si la víctima pulsara el enlace de confirmación, el atacante ya la controlaría. Con «sin cuenta hasta abrir el enlace» no hay nada que secuestrar, y si la víctima abre un enlace que no pidió, **elige ella la contraseña**. De paso desaparecen el caso «login bloqueado hasta confirmar», la purga de empresas sin confirmar y la migración de `EmailConfirmed`. *(ADR-023)*

**¿Se puede saber si un email ya tiene cuenta al registrarse?**
No: responde **siempre `202` sin cuerpo y con las mismas cabeceras**, y **tarda siempre lo mismo**. Con un email que ya tiene cuenta no se crea nada y se envía un **aviso fijo** («Ya tienes una cuenta»), sin token, para que quien tecleó bien su email no se quede esperando. El tiempo **se midió** (200 + 200 peticiones alternadas): sin suelo la diferencia era de ≈ 3,4 ms y se distinguía sin dudar (t de Welch = −9,40; Mann-Whitney z = −10,62); con un suelo de 250 ms aplicado **después** del trabajo, no (t = 1,03; z = 0,75). **Límite honesto:** si el trabajo de un email nuevo pasara de 250 ms, esa cola volvería a delatarlo. *(Cap. 6, ADR-023)*

**¿Cómo se evita que OfiFlow sirva para llenar de correo el buzón de otra persona?**
Con tres límites: 3 pedidos por hora y por IP, **3 pendientes por hora y por email en silencio** y **3 avisos por hora y por email en silencio**. El tercero **no estaba en la spec**: el límite «por email» de los pendientes cuenta filas, y con un email que ya tiene cuenta no se crea ninguna, así que se podía llenar ese buzón de avisos desde muchas IP; lo encontró escribir el código y se cerró con un limitador propio, en memoria. **Límite honesto:** el del aviso se reinicia con la API y no se comparte entre instancias. *(ADR-023)*

**¿Qué pasa si dos personas abren el mismo enlace a la vez?**
Gana una. El pendiente lleva una columna de versión de fila; la perdedora falla al confirmar —con su empresa y su usuario— y recibe el mismo `registration.invalid`, **sin dejar nada**. Y el código captura **los dos** fallos posibles de la base de datos (la versión de fila y el índice único del email) juntos, porque capturar solo uno dejaría escapar el otro como un `500`. Se probó con una **barrera** que obliga a que las dos hayan leído el pendiente antes de que ninguna confirme. *(Cap. 6, ADR-023)*

**¿Por qué se retiró `POST /auth/register`?**
Porque dos formas de crear una cuenta serían dos formas de saltarse la confirmación del buzón. Era un cambio de contrato **sin clientes que lo sufrieran** (solo lo usaban los tests, la colección de peticiones y la documentación; el frontend nunca lo usó). Los tests crean ahora sus empresas por el flujo real, con un ayudante que lee el enlace del correo capturado, y un test de arquitectura fija que solo el caso de uso de completar crea una empresa. *(ADR-023)*

**¿Qué se guarda de alguien que todavía no tiene cuenta, y durante cuánto tiempo?**
Su nombre, el de la empresa y su email, **hasta 24 horas de vigencia más 7 días de margen de purga**; el token solo como hash y la contraseña, nunca. Son datos personales, y por eso caducan, se purgan por lotes con un evento y no aparecen en ningún registro (ni emails, ni nombres, ni tokens). *(Cap. 6, 8)*

**¿Quién redacta las condiciones de uso, y qué guarda la API?**
Las redacta el dueño del proyecto (hoy **aún no están escritas**, y hay que tenerlas antes de abrir el registro a clientes reales). La API **solo compara una etiqueta** de versión (por ejemplo `2026-11`) con la vigente y guarda en el `User` la versión y la fecha aceptadas; quien tenga abierta una pantalla antigua recibe un `400` en vez de aceptar sin saber qué. Se piden también al aceptar una invitación. *(ADR-023)*

### El segundo factor obligatorio (spec 022)

**¿Cómo se impone el segundo factor a `Owner` y `Admin`, y por qué en cada petición y no con un token «limitado»?**
Con una comprobación en el `AuthorizationBehavior`, **después** del permiso, con el rol **efectivo** de la base de datos: si el rol lo exige y la cuenta no tiene el factor activo, solo pasan las operaciones marcadas `[AllowedWithoutSecondFactor]`; el resto da `403 mfa.enrollment_required`. La alternativa —emitir al iniciar sesión un token que solo sirva para activarlo— añade un tipo de token y un estado que viaja con él, y **no cubre la promoción**: quien pasa a `Admin` con la sesión abierta conservaría acceso completo hasta renovarla. Por petición, promover o reiniciar surte efecto en la siguiente, con el mismo token, por el mismo mecanismo que ya revalida el rol. *(Cap. 6, Parte 11; ADR-024)*

**¿Qué puede hacer alguien que debe activar el factor y todavía no lo ha hecho, y por qué es una lista cerrada?**
Exactamente cuatro cosas: ver quién es, ver el estado del factor, pedir el secreto y confirmarlo. Nada más (ni las sesiones). Cada una lleva un atributo explícito, y **lo que no lo lleva se deniega**: una operación nueva queda cerrada hasta que alguien decida lo contrario, y un test de arquitectura fija la lista exacta. Una lista de operaciones *denegadas* olvidaría la próxima que se añada. *(Cap. 8)*

**¿Se puede desactivar una vez activado?**
No con un rol que lo exige: `400 mfa.required_for_role`, y se decide **antes** de pedir la contraseña o el código, así que un intento no gasta intentos del bloqueo de cuenta ni da pistas (un test repite ocho intentos erróneos y la cuenta no se bloquea). Sin esto, activarlo y desactivarlo en el acto anularía toda la obligación. Quien baja de rol sí puede quitarlo. *(Cap. 6, Parte 11)*

**¿Cuánto cuesta comprobarlo en cada petición?**
**Ninguna consulta más.** El estado del factor viaja en la misma consulta que la pertenencia y el rol (una subconsulta `EXISTS` sobre la clave de la propia cuenta). Un test contra SQL Server real cuenta las consultas y exige una, y la comprobación en vivo leyó el SQL que ejecuta la API. *(Cap. 6, Parte 11)*

**¿Qué riesgo NO cierra?**
**La ventana de enrolamiento:** una cuenta que todavía no ha activado el factor puede ser tomada por quien conozca su contraseña, entrando con la sesión restringida y activándolo él. Es la confianza en el primer uso de cualquier enrolamiento. Se mitiga (activarlo pide la contraseña y cierra todas las sesiones; en la cuenta que nace de la web la ventana es mínima: la primera pantalla que ve es la de activarlo), pero no se cierra sin un canal aparte. Tampoco el *phishing* (el TOTP no lo resiste; las *passkeys* sí) ni la pérdida del móvil y los códigos de un `Owner`. *(Cap. 8, límite 3l)*

**¿Cómo se prueba una regla que los cientos de tests existentes tienen que poder ignorar?**
La regla pasa por una interfaz con **una sola implementación registrada en `src`** (la real); los tests de la API la sustituyen por una que no exige a nadie, y solo las clases que prueban la puerta usan la real. Lo que impide que eso la debilite son escaneos de arquitectura: un solo fichero la registra, ninguno lee una configuración para apagarla (no existe un interruptor), y un test arranca la API real y comprueba que resuelve la regla real y que exige a `Owner` y `Admin` y a nadie más. *(Cap. 8 y 9)*

**¿Qué error real encontrasteis?**
No un fallo del código sino **huecos de los tests**, destapados por las mutaciones: dos eventos con el mismo número no rompían nada porque ningún test fijaba que fueran únicos, y un caso del interceptor del frontend («navegar aunque la cuenta no se haya podido cargar») sobrevivía porque el test no distinguía el caso. Ambos se cerraron con un test nuevo. *(Cap. 9)*

### Cambiar la contraseña estando dentro (spec 019)

**¿Qué hace falta para cambiar la contraseña estando dentro, y por qué no basta con tener sesión?**
La contraseña **actual** y, si la cuenta tiene el segundo factor, también un **código**. Estar dentro no prueba nada contra la amenaza que esta operación cubre: **una sesión robada** (un token, un navegador desatendido) que quiere quedarse con la cuenta cambiando la contraseña. Pedir la actual (y el móvil) es pedir algo que esa sesión no tiene. Es otra amenaza que la del olvido, donde quien puede estar al otro lado es quien controla el buzón. *(Cap. 6, Parte 12; ADR-025)*

**¿Por qué importa el orden de las comprobaciones?**
Primero la actual (con la función del segundo factor: el intento se reserva antes y **cuenta para el bloqueo**), después el código y después «la nueva es distinta». Un error de la actual **no gasta** una comprobación del código; un código ausente no gasta ninguna; y «igual» solo se dice a quien ya demostró la actual, así que no revela nada. Las longitudes se rechazan **antes** de gastar nada. *(Cap. 6, Parte 12)*

**¿Qué se cierra y qué se conserva al cambiarla?**
Se cierran **todas las demás sesiones** de la cuenta y se conserva la actual (si no hay Id de sesión en el token, se cierran todas: *fail-closed*). Se **revoca** y no se borra, para que un refresh token cerrado que alguien use después se siga detectando como reutilización en vez de dar un «no existe» silencioso. Y se anulan los **enlaces de recuperación pendientes**: un atacante con acceso al buzón que había pedido un restablecimiento no puede usarlo después. *(Cap. 6, Parte 12)*

**¿Por qué todo en una sola confirmación?**
Porque si se hiciera por partes, un fallo entre medias dejaría la contraseña cambiada con las sesiones ajenas vivas, justo lo que necesita quien tenía la sesión robada. Tres piezas no confirman por su cuenta y las confirma el caso de uso, una vez; un escaneo de arquitectura lo fija, y un test contra SQL Server real provoca una **carrera real** de dos cambios (gana uno, el otro recibe `400 password.current_invalid`, nunca un `500`). *(Cap. 6, Parte 12; cap. 9)*

**¿Un `Owner` sin segundo factor puede cambiar su contraseña?**
No: la operación no está en la lista cerrada de la puerta (spec 022) y recibe `403 mfa.enrollment_required`. Primero activa el factor. Mantiene la lista exacta de cuatro operaciones y la regla «lo no marcado se deniega». *(Cap. 8)*

**¿Qué riesgo NO cierra?**
Que quien tenga **la contraseña actual y una sesión** de una cuenta **sin** segundo factor (`Manager`, `Technician`, `Employee`) puede cambiarla y expulsar al dueño: la contraseña actual es justo lo que demuestra «ser quien debe». Con factor, y siempre para `Owner` y `Admin`, el código lo impide. Tampoco hay un enlace para deshacer en el aviso por correo, ni se puede elegir cerrar también la sesión actual, ni se comprueba contra una lista de contraseñas filtradas. *(Cap. 8, límite 3o)*

**¿Qué error real encontrasteis?**
Uno que no era del producto: un test de extremo a extremo **mataba su propia sesión**, porque renovaba con el refresh token de una sesión ya cerrada y la detección de reutilización cerraba también la actual. Era la protección funcionando; el test se reordenó, y es el mejor argumento a favor de revocar y no borrar. Otro: el test de «nada sensible en `me`» falló porque el nombre del permiso (`Account.ChangePassword`) contiene `password`; no se relajó, se cambió para comprobar la forma de los permisos. *(Cap. 9)*

### Presupuestos (spec 023)

**¿Por qué el dinero va en céntimos enteros y lo calcula siempre el servidor?**
Porque un decimal de coma flotante no representa 0,1 con exactitud y cada operación volvería a decidir el redondeo; y porque un total que llega en la petición es un campo que alguien manipulará. Una sola función del servidor (`QuoteCalculator`) calcula en céntimos enteros, y la petición nunca lleva un total: uno de más se ignora. *(Cap. 8; ADR-026)*

**¿Cómo se calcula el IVA, y por qué «una vez por tipo»?**
Cada línea da su neto (cantidad × precio, al céntimo, **alejándose de cero**); se suman los netos por tipo de IVA (0, 4, 10 o 21 %) y el IVA se calcula **una vez por tipo** sobre esa suma. Es como lo desglosa una factura, así que el presupuesto y la factura futura coincidirán al céntimo; redondear línea a línea se desvía (cinco líneas de 0,02 € al 21 %: 0,00 € por línea, 0,02 € por tipo). Una tabla de **15 casos fijos** lo protege. *(Cap. 9)*

**¿Cómo se sabe que la vista previa del frontend da lo mismo que el servidor?**
Los dos pasan los **mismos 15 casos fijos** (el JSON del backend, copiado como datos en el frontend) y, en el navegador, el total de la vista previa coincidió al céntimo con el que devolvió la API al guardar. El frontend calcula con `BigInt` porque el redondeo de un número de coma flotante no coincide. Riesgo honesto: **la copia se mantiene a mano**; no hay un test que compare los dos ficheros entre repositorios. *(Cap. 9; ADR-026)*

**¿Por qué un presupuesto enviado no se puede modificar, y cómo se garantiza si dos personas actúan a la vez?**
Porque un precio ofrecido a un cliente es un compromiso: se corrige haciendo otro. Solo un borrador se modifica o se elimina, y la fila lleva una versión (`RowVersion`) y un contador de cambios (`Revision`): una edición y un envío simultáneos nunca dejan un enviado con las líneas cambiadas, y quien edita con una versión vieja recibe `quote.changed_meanwhile` en vez de pisar nada. Un test contra SQL Server provoca la carrera de verdad, en los dos órdenes. *(Cap. 8)*

**¿Por qué hace falta un contador de cambios además de la versión de fila?**
Porque una edición que deja las mismas líneas y la misma cabecera no cambia ninguna columna de negocio: no habría `UPDATE` sobre el presupuesto y el control de concurrencia no la vigilaría. Con `Revision` la fila cambia siempre. *(Cap. 8)*

**¿Cómo se numeran los presupuestos sin SQL a mano y sin repetir números?**
`máximo + 1` **de esa empresa** (el filtro global limita la consulta), un índice único `(TenantId, Número)` y hasta tres reintentos que sacan el intento fallido del seguimiento. Límite aceptado y medido: con **diez creaciones simultáneas** de una misma empresa, ocho se crearon y dos dieron `quote.number_unavailable`, sin ningún `500` ni número repetido. *(Cap. 8, límite 3p)*

**¿Quién puede hacer qué con un presupuesto?**
Tres permisos: `Quotes.Read`, `Quotes.Write` (preparar: crear, modificar y eliminar borradores) y `Quotes.Issue` (**comprometer**: enviar, aceptar y rechazar). `Owner`, `Admin` y `Manager` los tres; `Employee` prepara pero no compromete; `Technician` no ve presupuestos (los precios son información comercial). Se prueba la matriz entera, 8 operaciones por 5 roles, con el efecto comprobado en cada celda. *(Cap. 8)*

**¿Qué riesgo NO cierra?**
Que quien tenga `Quotes.Issue` puede **aceptar «en nombre» del cliente**: aceptar es una anotación interna, no una firma. Hasta que el cliente pueda aceptar él mismo (una identidad nueva y no confiable), el sistema registra pero no prueba nada ante un tercero. Tampoco hay PDF ni correo, ni caducidad automática, y el cálculo vive en dos sitios. *(Cap. 8, límite 3p)*

**¿Qué error real encontrasteis?**
Seis mutaciones sobrevivieron y cada una era un hueco de un test, no del código: el endpoint que registra con `Console.WriteLine` (el escaneo solo buscaba `ILogger`), un DTO que no ordenaba las líneas (SQL Server ya las devolvía en orden por su índice), y en el frontend un `Employee` que veía «Aceptar», una vista previa con una línea inválida (mi test usaba un precio vacío, que suma 0) y que nada distinguía si el total venía del servidor o del cálculo local. Y un fallo de EF Core: una línea nueva con su `Guid` ya puesto se marcaba «modificada» y el `UPDATE` fallaba al sustituir las líneas (`ValueGeneratedNever`). *(Cap. 9)*

### La agenda (spec 020)

**¿Por qué la fecha de un trabajo es un instante en UTC con desfase, y no la hora que escribe la persona?**
Porque «09:00» no es un momento: el mismo texto significa instantes distintos en Madrid y en Bogotá, y no existe (o existe dos veces) la noche en que cambia la hora. La fecha entra con su desfase (`2026-11-10T09:00:00+01:00` o terminada en `Z`), la API la guarda en UTC y la devuelve **siempre en UTC con la `Z`**; solo el navegador convierte a la hora de quien mira. Comparar, ordenar y pedir rangos son entonces operaciones sobre instantes y no dependen de ningún huso. *(ADR-022, opción A.)*

**¿Y si llega una hora sin desfase?**
Se rechaza con un `400` de campo. El motivo es concreto: `DateTimeOffset.TryParse` daría por buena esa hora con el **huso del servidor**, y la cita quedaría a la hora del servidor, no a la de quien la escribió, sin que nada falle. Por eso la fecha viaja como texto y la lee `IsoInstant` (forma exacta, longitud acotada, cultura invariante, `[0-9]` y no `\d`, que en .NET acepta dígitos de otros alfabetos).

**¿Quién decide qué es «hoy» y «esta semana»? ¿Por qué no se suman 24 horas por día?**
El navegador, con su huso: la agenda pide el intervalo de la semana como la medianoche local del lunes y la del lunes siguiente. Se construye con los **componentes de la fecha** (año, mes, día), nunca sumando 168 horas: la semana en que cambia la hora dura 167 o 169, y sumar 168 deja el final a las 23:00 o a las 01:00 con un día descuadrado. Hay un test que recorre todas las semanas de un año. El límite honesto: una empresa con personas en husos distintos ve el mismo instante a horas distintas (correcto) pero puede repartirlo en días distintos; un huso por empresa queda para más adelante.

**¿Cómo se evita que la agenda sirva para pedir toda la historia de una empresa?**
El rango es **obligatorio**, con desfase, el final es posterior al principio y no pasa de **42 días**; fuera de eso, `400`. Es semiabierto (`[from, to)`): un trabajo a las 00:00 exactas del lunes siguiente es de la semana siguiente y no sale en dos agendas. La consulta pasa por el filtro de empresa y la sirve un índice `(TenantId, ScheduledDate)`, con la empresa primero.

**¿Cómo sabes que ese índice se usa de verdad?**
Se mide, y la primera medición estaba mal. Mi test usaba `ToQueryString()`, que genera variables locales que SQL Server no inspecciona, sobre una tabla pequeña, y salía un recorrido de tabla. Se rehízo ejecutando la consulta **real** de EF con la caché de planes vacía, sobre 8.000 trabajos y un rango de un día: el plan en caché es un *Index Seek*. Con pocas filas, recorrer la tabla sería correcto (el índice no cubre la consulta); lo que se prueba es que, con datos suficientes, el índice **puede** usarse.

**¿Por qué programar tiene su propio permiso y no se reutiliza «asignar»?**
Programar compromete el tiempo de una persona y la cita con un cliente; hoy lo tienen los mismos roles que asignar (`Owner`, `Admin` y `Manager`), pero son dos acciones que pueden separarse mañana, y cambiarlo después es editar una tabla. Leer la agenda es `Jobs.Read`, de los cinco roles: un técnico ve la agenda de **toda** la empresa, como ya veía la lista de trabajos; «solo mis trabajos» es un filtro de la pantalla, no una restricción.

**¿Qué no hace la agenda y qué riesgos aceptas?**
No detecta conflictos de horario (dos trabajos del mismo técnico a la vez se aceptan; la vista de semana los deja ver), no tiene control de concurrencia (si dos personas programan el mismo trabajo a la vez, gana la última escritura), admite fechas pasadas (se marcan «Vencido») y un trabajo es un único intervalo de hasta 24 horas. Está todo en la ADR-022 como riesgos aceptados, con lo que habría que hacer para cerrarlos.

**¿Qué error del propio proceso te enseñó algo en esta spec?**
Una mutación sobrevivió **por azar**: ordenar la agenda por `Id` en vez de por fecha pasaba los tests porque los `Id` son aleatorios y, con tres trabajos, coinciden una vez de cada seis. Un test cuya detección depende de la suerte no demuestra nada; con doce trabajos en desorden, acertar por casualidad es 1 entre 479 millones y la mutación se detecta siempre.

### El operador de la plataforma (spec 011)

**¿Cómo puede un operador ver todas las empresas si el aislamiento lo impide?**
Es la **única excepción deliberada** al aislamiento (ADR-002), y por eso se hizo lo más estrecha posible: ve **cinco campos** de cada empresa (Id, nombre, alta, número de miembros y estado), nunca una persona, un cliente ni un trabajo; es una cuenta **sin ninguna empresa**; entra con **segundo factor obligatorio**; y todo lo que cambia deja una fila de auditoría que no caduca. El recuento de miembros es el único sitio que se salta el filtro de empresa, y está en una lista blanca vigilada por un test. *(Cap. 5, 6, 8)*

**¿Por qué no es un rol más?**
Un rol vive **dentro** de una empresa y el operador no tiene ninguna; además, un solo bit mal cambiado en una cuenta daría acceso global. Es una cuenta normal más una fila en una tabla aparte, que se puede auditar y proteger por separado. Y una cuenta es de empresa o de plataforma, **nunca de las dos**. *(Cap. 6)*

**¿Cómo se evita que un token de empresa valga en la plataforma, o al revés?**
Son **dos mundos**: otro esquema de autenticación, **otra clave y otra audiencia**, y la API no arranca si se parecen. Un token de empresa falla en la firma y uno de plataforma, en la audiencia, aunque sean correctos en su propio mundo. Se prueba con tokens fabricados con la clave de uno y la audiencia del otro. *(Cap. 6, 8)*

**¿Cómo se crea el primer operador, si no hay una ruta para hacerlo?**
Con un **programa del servidor** (`OfiFlow.Admin`) que la API ni referencia: la cuenta más poderosa no se crea por una API. Alta todo o nada: el primer código TOTP se comprueba en memoria **antes** de guardar nada, así que nunca existe un operador sin segundo factor. **Límite honesto:** quien controle el servidor o la base de datos puede crear uno. *(Cap. 1, 8)*

**Das de baja a un operador y su token sigue vivo 15 minutos, ¿no?**
No: cada petición vuelve a comprobar **en la base de datos** que la cuenta sigue siendo un operador activo. La baja surte efecto en su **siguiente petición**, que recibe un 403 y deja el evento `1608`. La sesión dura 15 minutos y no tiene *refresh token*. *(Cap. 6, 8)*

**¿Qué pasa si roban la sesión de un operador?**
Es el riesgo aceptado: con ella se puede suspender cualquier empresa durante 15 minutos o hasta que se dé de baja. Se mitiga (segundo factor, bloqueo de cuenta, sesión corta, baja inmediata, 30 peticiones por minuto, auditoría que no caduca) pero no se elimina; con clientes de pago, convendría que suspender exigiera la aprobación de dos operadores. *(Cap. 8, ADR-016)*

**¿Cómo sabes que suspender es todo o nada?**
La suspensión y su fila de auditoría se confirman en el mismo guardado. Un test hace que la base de datos **rechace** una de las dos sentencias y comprueba que la otra se deshace, en los dos sentidos. Y una versión de fila en la empresa impide que dos suspensiones simultáneas dejen dos filas de auditoría. *(Cap. 9)*

## 5. Seguridad general

**¿Cómo piensas la seguridad del proyecto?**
En tres ideas: **STRIDE** para listar amenazas, **defensa en profundidad** (varias capas, para que si una falla otra proteja) y **prevenir, detectar y responder**. El capítulo 8 cierra con un mapa de amenazas: para cada una, el control, dónde está y qué test lo vigila. *(Cap. 8)*

**¿Cómo evitas la inyección SQL?**
EF Core parametriza las consultas; los analizadores de seguridad del compilador están como **error**; y un test de arquitectura prohíbe las APIs de SQL crudo. *(Cap. 8)*

**¿Qué cabeceras de seguridad se envían?**
`X-Content-Type-Options`, `X-Frame-Options`, `Referrer-Policy`, una `Content-Security-Policy` muy restrictiva y `Cross-Origin-Resource-Policy`; y HSTS fuera de desarrollo. La única excepción es la pantalla de Swagger, solo en desarrollo y solo en su ruta, con tests que impiden que se extienda. *(Cap. 8)*

**¿Qué se registra y qué no?**
Hay 53 eventos de seguridad con identificador estable (`1001`...`1608`; `1304` y `1508` son del segundo factor obligatorio y `1023`, de cambiar la contraseña desde dentro). Llevan Ids, rol y operación; **nunca** emails, nombres, contraseñas ni tokens, y hay tests que buscan esos valores en todos los registros. **Límite honesto:** se registra, pero nadie recibe una alerta. *(Cap. 8)*

**¿Cómo se manejan los secretos?**
Fuera del repositorio. gitleaks escanea **todo el historial** en cada PR, GitHub bloquea el `push` si detecta uno conocido, y la aplicación **no arranca** si falta el secreto del token. *(Cap. 6, 10)*

**¿Qué es un *mass assignment* y cómo se evita?**
Que un atacante añada campos que no se esperaban (como `tenantId` o `role`). Los comandos solo tienen los campos que deben aceptar, y el rol o la empresa nunca vienen del cliente. *(Cap. 4, 8)*

## 6. Errores y validación

**¿Cómo responde la API cuando algo falla?**
Con **ProblemDetails** (un formato estándar) más un `code` **estable** (`invitation.invalid`), la lista de errores por campo y un `traceId`. El texto sale de un diccionario por idioma, y un test impide que un código se quede sin mensaje. Los errores inesperados devuelven un 500 **genérico**, sin detalles internos. *(Cap. 7)*

**¿Por qué códigos y no texto?**
Porque el texto cambia y se traduce; el código es un **contrato** con quien consume la API. *(Cap. 7, ADR-011)*

## 7. Tests

**¿Qué tests hay y por qué contra un SQL Server real?**
Cuatro proyectos (Domain, Application, Infrastructure y Api) y **770 casos**. Lo importante se prueba contra un SQL Server real en Docker (Testcontainers), porque una base en memoria **no aplica** índices únicos, versiones de fila ni el filtro de empresa. *(Cap. 9, ADR-008)*

**¿Cómo sabes que un test de seguridad de verdad funciona?**
Con cuatro técnicas: **controles positivos** (un caso que sí debe funcionar), **meta-tests** (probar al guardián), **centinelas** (que no pase en vacío) y **pruebas de mutación**: romper el código a propósito y ver el test en rojo. *(Cap. 9)*

**Cuéntame un fallo real que cazara un test.**
Hay varios y están en el capítulo 9. Uno: dos personas aceptando la misma invitación a la vez; con una base en memoria todo pasaba, y el test contra SQL real mostró que la perdedora acababa en un **error 500** por el índice único del email. Otro: un test intermitente destapó que una función de .NET ignora los espacios en blanco. *(Cap. 6, 9)*

**¿Mides la cobertura?**
No: `coverlet` está instalado pero ningún workflow la ejecuta. Es un límite conocido. La batería es proporcional al riesgo, no a la cobertura. *(Cap. 9)*

## 8. CI/CD

**¿Qué pasa cuando abres un Pull Request?**
Corren tres workflows en paralelo y hacen falta **cuatro comprobantes en verde** para poder fusionar: `build-and-test`, `gitleaks`, `analyze` (CodeQL) y `zap-scan`. `master` está protegida **también para el dueño del repositorio** (`enforce_admins`). *(Cap. 10)*

**¿Qué diferencia hay entre CodeQL y ZAP?**
CodeQL es **SAST**: analiza el código sin ejecutarlo, siguiendo el flujo de los datos. ZAP es **DAST**: ataca la aplicación en ejecución, desde fuera, como un atacante. Se complementan. *(Cap. 10, ADR-010)*

**¿Cómo proteges la cadena de suministro?**
Las acciones de GitHub se fijan por el **hash del commit**, no por una etiqueta que se pueda mover; las dependencias vulnerables rompen el build (`NuGetAudit`); y Dependabot propone las actualizaciones. Un caso real: un aviso de severidad alta sobre una dependencia indirecta rompió todos los PRs, y se arregló fijando la versión corregida, **sin silenciar el control**. *(Cap. 10)*

**¿Hay despliegue automático?**
No: hay integración continua, pero **no entrega continua**. Todavía no hay nada que desplegar. *(Cap. 10)*

## 9. Método y honestidad

**¿Por qué especificas antes de programar?**
Porque obliga a pensar antes (las decisiones difíciles se descubren al escribir la spec, no tras una semana de código), deja constancia del porqué y se puede defender. Y porque el manual y el código avanzan a la vez, así que ninguno enseña algo que ya no es verdad. *(Cap. 11)*

**¿Qué NO está hecho? (la pregunta más importante)**
Con honestidad y de lo más a lo menos preocupante:
- **El TOTP no resiste al *phishing*, queda la ventana de enrolamiento y un `Owner` que lo pierde todo no se recupera solo:** el segundo factor es **obligatorio para `Owner` y `Admin`** (spec 022), pero una cuenta que aún no lo ha activado puede ser tomada por quien conozca su contraseña activándolo él, las *passkeys* son el siguiente paso, y un `Owner` que pierde el móvil y los códigos de recuperación queda fuera hasta que intervenga el operador de la plataforma. La contraseña se puede recuperar (spec 018) y cambiar estando dentro (spec 019), aunque sin segundo factor una sesión robada que conozca la contraseña actual puede cambiarla.
- **El correo existe pero no está probado contra un proveedor real, y no hay cola persistida (la de recuperar la contraseña es en memoria), ni reintentos, ni gestión de rebotes**: la invitación se envía por correo (spec 017) y se ha probado contra un servidor SMTP de pruebas. Falta elegir proveedor y dominio, y configurar SPF, DKIM y DMARC para que llegue a la bandeja. Sin correo configurado, el token sigue devolviéndose a quien invita.
- **Una cuenta pertenece a una sola empresa**; el login toma la primera pertenencia.
- **Crear una cuenta (spec 021) deja abiertos los textos legales** (los redacta el dueño del proyecto), que las cuentas anteriores **no han aceptado ninguna condición**, la comprobación **anti-robots**, que el límite de 3 pedidos por hora y por IP **puede frenar a una oficina entera**, y que el límite de avisos vive **en memoria**.
- **Los presupuestos (spec 023) no tienen PDF ni correo** y aceptar es una **anotación interna**, no la firma del cliente; el cálculo del dinero está en dos sitios (los casos fijos lo protegen, pero la copia del frontend se mantiene a mano); y muchas creaciones simultáneas de una empresa pueden agotar los tres intentos del número.
- **Un técnico puede ver y ejecutar cualquier trabajo de su empresa**, no solo los asignados a él.
- **No hay alertas ni un registro de auditoría persistente**: los eventos se escriben, pero nadie los vigila.
- **ZAP escanea solo la superficie anónima** y solo en los PRs.
- **No se mide la cobertura** y **no hay entrega continua**.
- La política de CodeQL del ADR-010 **no está hecha cumplir**: el análisis se ejecuta, pero una alerta alta no bloquea por sí sola.
- Cuatro controles existen pero **ningún test los comprueba directamente** (arranque sin secreto, caducidad del token, hash distinto de la contraseña, HSTS).

*(Secciones "Dónde están hoy los límites" de los capítulos 6, 8, 9 y 10)*

**¿Qué cerrarías primero?**
Una respuesta defendible: **la recuperación de un `Owner` que lo pierde todo (la herramienta de plataforma)**, porque el segundo factor ya es obligatorio para `Owner` y `Admin` (spec 022) y, justo por eso, quien lo pierde todo ya no puede «quitarlo y entrar solo con la contraseña». Lo siguiente, cerrar la ventana de enrolamiento con un canal aparte y que el técnico solo vea sus trabajos. **Lo importante es poder explicar por qué eliges ese**, no cuál eliges.

**¿Qué no harías igual hoy?**
Hay decisiones que se tomaron y conviene reconocer: no existe todavía una ADR que explique por qué se usa EF Core y no SQL a mano; la imagen de SQL Server de los tests está escrita en dos ficheros; y algunas listas blancas se mantienen a mano. Reconocerlo, con su plan, suma.

**¿Has usado herramientas de IA para construirlo?**
Prepara tu propia respuesta, **honesta y con tus palabras**: qué decidiste tú (la arquitectura, las reglas de negocio, qué se acepta como riesgo), qué revisaste, y cómo compruebas que entiendes cada pieza (por eso existe este manual y las preguntas de cada capítulo). Un tribunal valora más que sepas **explicar y defender** cada decisión que quién escribió cada línea.

---

## Antes de entrar

- [ ] Puedes dibujar las capas y las flechas de dependencia sin mirar (capítulo 2).
- [ ] Puedes recorrer **una petición** de principio a fin (capítulo 4).
- [ ] Puedes explicar el aislamiento entre empresas en tres frases (capítulo 5).
- [ ] Sabes por qué el rol sale de la base de datos y no del token (capítulo 8).
- [ ] Sabes qué hace cada una de las cuatro comprobaciones de un PR (capítulo 10).
- [ ] Puedes enumerar **cinco cosas que no están hechas** y decir cuál cerrarías primero.
- [ ] Has ejecutado la API y probado una invitación completa en Swagger (capítulo 1 y sección 6 del `.http`).

Si alguna casilla te cuesta, vuelve al capítulo indicado y responde a sus preguntas finales.
