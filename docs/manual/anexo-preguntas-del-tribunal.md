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
Es un JWT firmado (HMAC-SHA256) con el usuario, la empresa y el rol; dura **30 minutos**. El secreto con el que se firma no está en el repositorio: en local vive en *user-secrets* y en producción vendría de una variable de entorno o un gestor de secretos. *(Cap. 6, ADR-009)*

**¿Y el refresh token?**
64 bytes aleatorios; en la base de datos solo se guarda su **hash**. Cada uso lo **rota** (emite uno nuevo y revoca el anterior). Si alguien presenta uno ya revocado, se interpreta como robo y se revoca toda la cadena del usuario. *(Cap. 6, ADR-007)*

**Si roban un token de acceso, ¿qué pasa?**
Vale hasta que caduque (un JWT no se puede anular), **pero** el rol y la pertenencia a la empresa se revalidan en la base de datos en cada petición, así que un token de alguien al que se quitó ya no autoriza nada. *(Cap. 6 y 8)*

**¿Cómo se frena la fuerza bruta?**
Con un límite de peticiones por IP: 5 logins por minuto, 3 registros por hora, 10 aceptaciones de invitación por hora. Al pasarse, un 429 y un evento de seguridad. **Además, desde la spec 010, **cada cuenta se bloquea 15 minutos tras 5 fallos** aunque los intentos vengan de IP distintas, y el intento se reserva antes de comprobar la contraseña para que 50 peticiones simultáneas no den 50 intentos. **Límite honesto:** quien conozca un email puede mantener esa cuenta bloqueada fallando a propósito. *(Cap. 6 y 8)*

**¿Se puede saber si un email está registrado?**
El login responde un único 401 para "email desconocido", "contraseña incorrecta" y "cuenta bloqueada", y tarda lo mismo en los tres (se hace siempre una verificación completa, contra un hash ficticio si no hay cuenta). **Pero** el registro sí dice "ya está registrado": un límite conocido y anotado. *(Cap. 6, 8)*

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
Que el invitante lo ve y podría aceptarlo él mismo. Es una decisión consciente (sin servicio de email todavía); lo que **nunca** pasa es que conozca la contraseña de la otra persona. Cuando haya email, el token dejará de devolverse. *(ADR-013)*

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
Hay 32 eventos de seguridad con identificador estable (`1001`...`1608`). Llevan Ids, rol y operación; **nunca** emails, nombres, contraseñas ni tokens, y hay tests que buscan esos valores en todos los registros. **Límite honesto:** se registra, pero nadie recibe una alerta. *(Cap. 8)*

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
- **El segundo factor es opcional y el TOTP no resiste al *phishing*:** obligarlo para `Owner` y `Admin` exige pantallas, y las *passkeys* son el siguiente paso. Tampoco se puede cambiar ni recuperar la contraseña, y un `Owner` que pierde el móvil y los códigos de recuperación queda fuera hasta que intervenga el operador de la plataforma.
- **El token de invitación no se envía por email**: lo recibe quien invita.
- **Una cuenta pertenece a una sola empresa**; el login toma la primera pertenencia.
- **Un técnico puede ver y ejecutar cualquier trabajo de su empresa**, no solo los asignados a él.
- **No hay alertas ni un registro de auditoría persistente**: los eventos se escriben, pero nadie los vigila.
- **ZAP escanea solo la superficie anónima** y solo en los PRs.
- **No se mide la cobertura** y **no hay entrega continua**.
- La política de CodeQL del ADR-010 **no está hecha cumplir**: el análisis se ejecuta, pero una alerta alta no bloquea por sí sola.
- Cuatro controles existen pero **ningún test los comprueba directamente** (arranque sin secreto, caducidad del token, hash distinto de la contraseña, HSTS).

*(Secciones "Dónde están hoy los límites" de los capítulos 6, 8, 9 y 10)*

**¿Qué cerrarías primero?**
Una respuesta defendible: **recuperar y cambiar la contraseña, y hacer obligatorio el segundo factor para `Owner` y `Admin`** en cuanto haya pantallas que lo guíen, porque el segundo factor ya existe (spec 010) pero es opcional, y es lo único que separa una contraseña robada de una cuenta tomada. Lo siguiente, el panel de operador de la plataforma (que usará este mismo segundo factor) y que el técnico solo vea sus trabajos. **Lo importante es poder explicar por qué eliges ese**, no cuál eliges.

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
