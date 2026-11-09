# Capítulo 12 — Glosario y decisiones

## Qué vas a aprender

Este capítulo no se lee de corrido: es para **consultar**.

- Un glosario de los términos del manual, en orden alfabético, con el capítulo donde se explican.
- Un índice de las **decisiones de arquitectura** (ADR) con lo que decidieron en una frase.
- Un índice de las **especificaciones** (specs) y de lo que cada una añadió.

## Glosario

Los términos en inglés van entre paréntesis porque son los que verás en el código, en la documentación oficial y en una entrevista.

| Término | Qué significa | Cap. |
|---|---|---|
| **AAA** (*Arrange, Act, Assert*) | La estructura de un test: preparar, ejecutar, comprobar | 9 |
| **ADR** (*Architecture Decision Record*) | Un documento corto que recoge una decisión de arquitectura: el contexto, las opciones, qué se decidió y qué consecuencias tiene | 2, 11 |
| **Agenda** | La vista de los trabajos que tienen fecha dentro de un rango (hoy, una semana). Es una consulta (`GET /jobs/agenda`), no una entidad nueva: sus datos son los de `Job` | 0, 8 |
| **Agregado** (*aggregate*) | Un grupo de objetos que se trata como una unidad y se guarda de una vez (por ejemplo, `Job`, `Invitation`). Solo se entra por su raíz | 3 |
| **Autenticación** (*authentication*) | Saber **quién** eres (el login) | 6 |
| **Autorización** (*authorization*) | Saber **qué puedes hacer** (permisos y roles) | 6, 8 |
| **Behavior** (de MediatR) | Un paso que se ejecuta antes o después de cada operación, para todas: autorización y validación | 4 |
| **Bearer** | Esquema de autenticación en el que el token se envía en la cabecera `Authorization: Bearer <token>` | 6 |
| **Bloqueo de cuenta** (*account lockout*) | Tras 5 contraseñas o códigos erróneos, la cuenta no deja entrar durante 15 minutos, ni con la contraseña correcta. Se cuenta **reservando el intento antes de comprobarlo**, para que las peticiones simultáneas no lo esquiven | 6, 8 |
| **Cambiar la contraseña estando dentro** | `PUT /account/password`: pide la contraseña **actual** y, si hay segundo factor, un **código**; cierra las demás sesiones y conserva esta, y anula los enlaces de recuperación pendientes, todo en una confirmación (spec 019, ADR-025) | 6, 8 |
| **Caso de uso** (*use case*) | Una cosa que se puede hacer con el sistema ("crear un trabajo"). En OfiFlow, un *Command* o una *Query* | 3 |
| **Céntimos enteros** (*cents*) | Cómo se guarda y se calcula el dinero de un presupuesto: un número entero de céntimos (`long`), nunca un decimal de coma flotante, que no representa 0,1 con exactitud. Lo calcula **una sola función** del servidor (spec 023, ADR-026) | 8, 9 |
| **Claim** | Un dato dentro de un token (quién es, de qué empresa). Está firmado: no se puede alterar sin que se note | 6 |
| **Clean Architecture** | Organizar el código en capas concéntricas donde las dependencias apuntan hacia dentro | 2 |
| **CodeQL** | Analizador de GitHub que sigue el flujo de los datos buscando vulnerabilidades | 10 |
| **Código de recuperación** (*recovery code*) | Uno de diez códigos de un solo uso (80 bits, solo se guarda su hash) que permite entrar si se pierde el móvil | 6 |
| **Cola en memoria** (*in-memory queue*) | Una lista de correos pendientes que vive en la memoria del proceso, con un solo consumidor que los envía de uno en uno. No se pierde nada **mientras la API siga viva**, pero se pierde si se reinicia, y no reintenta. Se usa para el correo de recuperar la contraseña porque así el token **no se guarda en claro** en ningún sitio persistente | 6 |
| **Command / Query** | Una operación que cambia algo / una que solo lee | 3 |
| **Concurrencia optimista** (*optimistic concurrency*) | Dos peticiones leen lo mismo; al guardar, la base de datos solo deja confirmar a una (columna `rowversion`). En un presupuesto la versión viaja al cliente y vuelve al modificar, y un contador de cambios (`Revision`) hace que la fila cambie siempre | 6, 8 |
| **CQRS** | Separar las operaciones que cambian datos de las que los leen | 2 |
| **CSP** (*Content-Security-Policy*) | Cabecera que le dice al navegador qué puede cargar o ejecutar una respuesta | 8 |
| **Desafío** (*challenge*) | Lo que recibe quien pone bien la contraseña en una cuenta con segundo factor: un valor opaco (no un JWT) que caduca en 5 minutos, se canjea una vez con un código y muere a los 5 intentos | 6 |
| **Desfase horario** (*offset*) | La diferencia con UTC con la que se escribe una hora (`+01:00`, o `Z` si es cero). Sin él, una hora no es un momento exacto y la API no la acepta | 0 |
| **DAST** | Probar la aplicación **en ejecución**, desde fuera, como un atacante (OWASP ZAP) | 10 |
| **DDD** (*Domain-Driven Design*) | Modelar el código alrededor de las reglas del negocio | 2 |
| **Defensa en profundidad** (*defense in depth*) | Varias protecciones seguidas, para que si una falla la siguiente aún proteja | 8 |
| **Dependabot** | Robot de GitHub que abre PRs cuando salen versiones nuevas de las dependencias | 10 |
| **Diccionario de mensajes** | El fichero `ErrorMessages.resx` con el texto de cada código de error | 7 |
| **Domain** | La capa del centro: las reglas del negocio, sin depender de nada externo | 3 |
| **DTO** (*Data Transfer Object*) | Un objeto que solo transporta datos hacia fuera (la respuesta de la API) | 3 |
| **EF Core** | La herramienta que traduce C# a SQL y guarda los datos | 3 |
| **Encargado del tratamiento** (*data processor*) | Una empresa que trata datos personales por cuenta de otra. Desde el correo (spec 017), el proveedor SMTP lo es: la dirección del invitado sale hacia él. Hay que resolver el contrato y dónde trata los datos | 6 |
| **Envenenamiento por cabecera Host** (*Host header poisoning*) | Construir un enlace con la cabecera `Host` u `Origin` de la petición: quien la envía decide adónde apunta el enlace de un correo que sale de tu sistema y parece de fiar. Por eso el enlace sale solo de `Email:LinkBaseUrl` | 6 |
| **Fail-closed** | Ante la duda, se deniega. Una operación sin permiso declarado no se ejecuta | 8 |
| **FluentValidation** | La librería que valida los datos de entrada con reglas legibles | 7 |
| **Filtro global de consultas** (*Global Query Filter*) | Una condición `TenantId == <empresa actual>` que EF Core añade a toda consulta de una entidad de empresa | 5 |
| **Gitleaks** | Escáner que busca secretos (contraseñas, claves) en el código y en todo el historial | 10 |
| **Guardarraíl** (*guardrail*) | Un control automático (compilador o test) que impide romper una regla sin darse cuenta | 8, 9 |
| **Handler** | El código que ejecuta un *Command* o una *Query* | 3 |
| **Hash** | Resultado de una función de un solo sentido: no se puede volver al original. Así se guardan contraseñas y tokens | 6 |
| **HSTS** | Cabecera que ordena al navegador usar siempre HTTPS con ese sitio | 8 |
| **Infrastructure** | La capa que habla con lo de fuera: base de datos, tokens, contraseñas | 3 |
| **Instante** (*instant*) | Un momento exacto del tiempo, sin ambigüedad de huso (`2026-11-10T08:00:00Z`). La API guarda y devuelve así las fechas de la agenda; la «hora de pared» (las 09:00) solo la ve y la escribe el navegador | 0 |
| **Intervalo semiabierto** | El rango `[from, to)`: incluye el principio y **no** el final. Así un trabajo a las 00:00 exactas del lunes siguiente es de la semana siguiente y no sale en dos agendas | 0, 9 |
| **Invitación** (*invitation*) | La forma de que una persona entre en una empresa que ya existe: un token de un solo uso con caducidad | 6 |
| **IVA por tipo** | El IVA de un presupuesto se redondea **una vez por cada tipo** (0, 4, 10 o 21 %) sobre la suma de las bases, y no línea a línea: es como lo desglosa una factura, así que presupuesto y factura coincidirán al céntimo (ADR-026) | 8, 9 |
| **JWT** (*JSON Web Token*) | Un token firmado que dice quién eres y en qué empresa estás | 6 |
| **Jerarquía de roles** (`ManageableRoles`) | La tabla que dice a qué roles puede conceder, cambiar y quitar cada rol | 8 |
| **Mass assignment** | Que un atacante añada a una petición campos que el programador no esperaba (por ejemplo, `tenantId`) | 4, 8 |
| **MediatR** | La librería que reparte cada operación a su handler y permite pasos comunes (*behaviors*) | 4 |
| **Meta-test** | Un test que comprueba que otro test (o una regla) detecta lo que dice detectar | 9 |
| **Middleware** | Un paso por el que pasa cada petición HTTP, en orden | 4 |
| **Migración** (*migration*) | Un fichero que describe un cambio del esquema de la base de datos | 1, 11 |
| **Monolito modular** (*modular monolith*) | Una sola aplicación, organizada en módulos independientes por dentro | 2 |
| **Mínimo privilegio** (*least privilege*) | Cada rol recibe solo lo que necesita | 8 |
| **Multi-tenancy** | Que muchos clientes (empresas) compartan el mismo sistema sin ver los datos de los demás | 5 |
| **Mutación** (*mutation testing*) | Romper el código a propósito y comprobar que algún test se pone en rojo | 9 |
| **Mutante equivalente** | Una mutación que no cambia lo que hace el código (por ejemplo, quitar una comprobación que otra barrera ya repite): ningún test puede detectarla. Se explica en vez de forzar un test falso | 9 |
| **NIF, NIE y CIF** | Los identificadores fiscales españoles: de una persona (NIF, ocho cifras y una letra), de un extranjero (NIE, empieza por X, Y o Z) y de una empresa (CIF, una letra de entidad, siete cifras y un control). Se validan **por algoritmo** —su letra o dígito de control tiene que cuadrar—, no solo por su forma (spec 024, ADR-027) | 5, 7, 8 |
| **Objeto de valor** (*value object*) | Un valor del dominio que **no existe sin ser válido** y se compara por su contenido, no por un Id: `SpanishTaxId`, `SpanishPostalCode` o `PhoneNumber`. Quien los recibe ya construidos no puede guardar un NIF que no cuadra | 3, 7 |
| **OpenAPI / Swagger** | La descripción de la API en JSON y la pantalla para probarla desde el navegador | 1 |
| **Oráculo** (*oracle*) | Cualquier cosa que, sin querer, responde a una pregunta que el atacante no debería poder hacer («¿existe esta cuenta?»). Puede ser la respuesta, **el tiempo que tarda**, o un límite que se activa. Se evita haciendo que todos los casos se vean igual por fuera | 6, 8 |
| **OWASP** | Organización que publica listas de riesgos y herramientas de seguridad web (como ZAP) | 8, 10 |
| **PBKDF2** | Función de hash lenta y con sal para contraseñas | 6 |
| **Permiso** (*permission*) | Lo que se puede hacer, con el formato `Entidad.Acción` (`Jobs.Assign`) | 8 |
| **Perfil fiscal** (*company profile*) | Los datos de quien emite los documentos: nombre fiscal, NIF/NIE/CIF, dirección, teléfono y email. Uno por empresa, nace al guardarlo; el NIF de un autónomo es su DNI, así que **nunca llega a un registro** (spec 024, ADR-027) | 0, 8 |
| **Presupuesto** (*quote*) | El precio que se ofrece a un cliente, con sus líneas y su total. Nace como borrador; **enviado, es inmutable** (se corrige haciendo otro); después se anota aceptado o rechazado. Aceptar es una anotación interna, no la firma del cliente (spec 023) | 0, 8 |
| **Puerto** (*port*) | Una interfaz en Application que dice qué necesita un caso de uso (`IEmailSender`, `ITokenService`) sin decir cómo se hace; Infrastructure la cumple. Cambiar de proveedor es cambiar la implementación | 3, 6 |
| **ProblemDetails** (RFC 9457) | El formato estándar de las respuestas de error de la API | 7 |
| **PR** (*Pull Request*) | La petición de fusionar una rama en `master`, donde corren las comprobaciones | 10, 11 |
| **Reserva atómica del intento** | Sumar el intento fallido y comprobar el límite en una sola sentencia SQL, antes de comprobar la contraseña. Es lo que impide que 50 peticiones simultáneas den 50 intentos | 6 |
| **Rate limiting** | Limitar cuántas peticiones puede hacer una IP en una ventana de tiempo | 8 |
| **RBAC** (*Role-Based Access Control*) | Autorización basada en roles: cada rol tiene unos permisos | 8 |
| **Refresh token** | Un segundo token, de larga duración, para renovar la sesión; se rota en cada uso | 6 |
| **Revalidación** | Comprobar en cada petición, en la base de datos, que la persona sigue en la empresa y con qué rol | 8 |
| **Revocar** (frente a *borrar*) | Marcar un refresh token como cerrado sin eliminarlo: si alguien lo usa después se sigue detectando como reutilización; es lo que hace cerrar «las demás» sesiones al cambiar la contraseña | 6 |
| **Rol** (*role*) | Un conjunto de permisos con nombre: `Owner`, `Admin`, `Manager`, `Technician`, `Employee` | 8 |
| **Puerta del segundo factor** | La comprobación del `AuthorizationBehavior` que, **por petición y después del permiso**, deniega con `mfa.enrollment_required` toda operación que no esté marcada `[AllowedWithoutSecondFactor]` a quien tiene un rol que lo exige y no lo tiene activo. No hay token «limitado»: se decide con el rol efectivo de la base de datos | 6, 8 |
| **Sesión restringida** | La de un `Owner` o `Admin` que todavía no ha activado el segundo factor: sirve solo para ver quién es, ver el estado del factor, pedir el secreto y confirmarlo. No es un tipo de sesión: es el resultado de la puerta en cada petición | 6, 8 |
| **Segundo factor** (*MFA*, *2FA*) | Una segunda prueba de quién eres además de la contraseña: aquí, el código TOTP de la app del móvil. **Obligatorio para `Owner` y `Admin`** (spec 022); opcional para el resto | 6, 8 |
| **SAST** | Analizar el **código** sin ejecutarlo para buscar vulnerabilidades | 10 |
| **SMTP / STARTTLS** | SMTP es el protocolo con el que se envía el correo y lo hablan todos los proveedores. STARTTLS empieza la conexión en claro y la cifra a continuación; en OfiFlow es obligatorio (si el servidor no lo ofrece, no se envía en claro) | 6 |
| **SPF / DKIM / DMARC** | Tres registros del DNS de un dominio que hacen que su correo llegue a la bandeja y no a spam. SPF: qué servidores pueden enviar en su nombre. DKIM: la firma de cada mensaje. DMARC: qué hacer con lo que falle y a dónde mandar los informes | 6 |
| **Pendiente de registro** (*pending registration*) | Lo que escribió alguien que quiere crear una cuenta (empresa, nombre y email), guardado **hasta que abre el enlace de su correo**. No es una cuenta: no hay empresa, ni usuario, ni contraseña. Caduca a las 24 horas y se purga a los 7 días | 6 |
| **Pre-hijacking** | Un ataque contra el registro: el atacante crea una cuenta con el email de **otra persona** y **su** contraseña; si la víctima confirma el correo, el atacante ya controla la cuenta. OfiFlow lo evita porque **no existe ninguna cuenta hasta que se abre el enlace** y la contraseña la elige quien lo abre | 6 |
| **SCA** | Vigilar las **dependencias** por vulnerabilidades conocidas (NuGetAudit, Dependabot) | 10 |
| **Spec-Driven Development** | Escribir la especificación antes que el código | 11 |
| **STRIDE** | Seis tipos de amenaza (suplantación, manipulación, repudio, revelación, denegación de servicio, elevación de privilegios) | 8 |
| **Suelo de tiempo** (*time floor*) | Una espera mínima que se aplica a una petición **después** de hacer su trabajo, para que no tarde menos de cierto tiempo pase lo que pase. En `forgot-password` oculta que crear un token cuesta ≈ 12 ms más que no crearlo Se usa por segunda vez al pedir crear una cuenta (`registration`, diferencia medida de ≈ 3,4 ms: menor, pero igualmente distinguible) | 6, 8 |
| **Tenant** | Una empresa cliente de OfiFlow | 5 |
| **Testcontainers** | Librería que arranca un SQL Server real en Docker desde el propio test | 9 |
| **TimeProvider** | La forma de pedir la hora en .NET, sustituible en los tests para no esperar | 11 |
| **TOTP** (*Time-based One-Time Password*, RFC 6238) | El código de 6 dígitos que cambia cada 30 segundos y sale de un secreto compartido y de la hora. Cada paso solo se acepta una vez | 6 |
| **Token** | Una credencial de un solo valor secreto (de acceso, de renovación o de invitación) | 6 |
| **Token de registro** | El secreto del enlace del correo de «crear una cuenta»: 256 bits aleatorios, **solo se guarda su hash**, caduca a las 24 horas, vale una sola vez y hay uno vigente por email (un índice único filtrado lo garantiza). Con él se **completa** el registro y se elige la contraseña | 6 |
| **Token de restablecimiento** (*password reset token*) | El secreto del enlace del correo de «he olvidado mi contraseña»: 256 bits aleatorios, **solo se guarda su hash**, caduca a la hora, vale una sola vez y una cuenta tiene uno vigente | 6 |
| **Versión de las condiciones** (*terms version*) | Una etiqueta corta como `2026-11` (`Legal:TermsVersion`). La API **no guarda los textos ni decide qué dicen**: compara la etiqueta que envía quien se registra con la vigente y guarda en el `User` la versión y la fecha aceptadas | 6 |
| **Value object** | Un objeto sin identidad propia, definido por su valor (`Email`) | 3 |
| **ZAP** | Herramienta de OWASP que ataca automáticamente una API en ejecución | 10 |

### Códigos HTTP que aparecen en el manual

| Código | Significa | Ejemplo en OfiFlow |
|---|---|---|
| **200 / 201 / 204** | Correcto / creado / correcto sin cuerpo | Listar clientes / invitar / quitar a un miembro |
| **400** | La petición tiene un problema (datos inválidos o regla de negocio) | Un título vacío; `invitation.invalid` |
| **401** | No estás autenticado | Falta el token o ha caducado |
| **403** | Estás autenticado, pero no puedes | Un `Technician` que intenta borrar un cliente |
| **404** | No existe (o es de otra empresa: se responde igual) | Un cliente de otra empresa |
| **429** | Demasiadas peticiones | Más de 5 logins por minuto desde la misma IP |
| **500** | Un fallo nuestro | Una excepción inesperada |

## Las decisiones (ADR)

Cada ADR está en [`docs/adr/`](../adr/). El manual **explica cómo funciona**; las ADR guardan **por qué** se decidió así y qué alternativas se descartaron.

| ADR | Decisión en una frase | Se explica en |
|---|---|---|
| [001](../adr/ADR-001-motor-base-datos.md) Motor de base de datos | SQL Server en la fase inicial; si el proyecto crece y el coste de licencia pesa, se migraría a PostgreSQL | 3 |
| [002](../adr/ADR-002-multi-tenancy.md) Multi-tenancy | Una sola base de datos; cada entidad de empresa lleva `TenantId`, un filtro global lo aplica solo y la empresa activa se guarda en el JWT, nunca viene del cliente | 5 |
| [003](../adr/ADR-003-cqrs.md) CQRS | CQRS pragmático (una aplicación, una base de datos) con MediatR y *behaviors* para lo común (validación, autorización) | 2, 4 |
| [004](../adr/ADR-004-identity.md) Identity | ASP.NET Core Identity para las credenciales; el rol vive en `TenantUser`, no en la cuenta | 6 |
| [005](../adr/ADR-005-modular-monolith.md) Monolito modular | Una aplicación con módulos por dentro, no microservicios | 2 |
| [006](../adr/ADR-006-persistencia-domain-events.md) Persistencia | `Guid` planos como identificadores; EF Core directamente (`IApplicationDbContext`), sin repositorio genérico; `ITenantOwned` para las entidades de empresa; auditoría automática y borrado físico | 3, 5 |
| [007](../adr/ADR-007-identity-refresh-token.md) Identity y refresh token | `User` (negocio) y `ApplicationUser` (credenciales) comparten Id; el refresh token rota y se detecta su reutilización | 6 |
| [008](../adr/ADR-008-testing-integracion.md) Tests de integración | SQL Server real en Docker con Testcontainers, no una base en memoria | 9 |
| [009](../adr/ADR-009-seguridad-base.md) Línea base de seguridad | Secure by Design: validación, cabeceras, límite de peticiones, secretos fuera del repositorio, eventos de seguridad | 8 |
| [010](../adr/ADR-010-pipeline-devsecops.md) Pipeline DevSecOps | Cada capa de verificación con una herramienta: compilador, CodeQL, gitleaks, ZAP, Dependabot | 10 |
| [011](../adr/ADR-011-errores-codigos-diccionario.md) Errores | Códigos estables y un diccionario de mensajes; ProblemDetails | 7 |
| [012](../adr/ADR-012-autorizacion-rbac.md) Autorización | Permisos `Entidad.Acción` en una tabla única rol → permisos, comprobados antes que la validación | 8 |
| [013](../adr/ADR-013-gestion-de-usuarios.md) Gestión de usuarios | Invitación con token de un solo uso; jerarquía de roles como tabla; el rol se lee de la base de datos en cada petición | 6, 8 |
| [014](../adr/ADR-014-mfa-y-bloqueo-de-cuenta.md) Segundo factor y bloqueo de cuenta | TOTP (obligatorio para `Owner` y `Admin` desde la ADR-024; opcional para el resto) con el secreto cifrado (AES-GCM) y un desafío opaco de un solo uso para el login; bloqueo por cuenta con el intento reservado antes de comprobarlo | 6, 8 |
| [015](../adr/ADR-015-logs-a-fichero-con-serilog.md) Logs a fichero | Serilog como un proveedor más (no sustituye la fábrica de loggers), en JSON para que nadie falsifique entradas con un salto de línea; 14 días de retención | 1, 8 |
| [016](../adr/ADR-016-panel-de-operador-de-la-plataforma.md) Panel de operador | Una identidad aparte para el operador de la plataforma (otra tabla, otra clave, otra audiencia), con segundo factor obligatorio, y la suspensión de empresas | 6, 8 |
| [017](../adr/ADR-017-cors-para-el-frontend-web.md) CORS | Lista cerrada de orígenes en `Program.cs`, sin comodines ni credenciales de navegador; un origen mal escrito impide arrancar | Capítulo 8 |
| [018](../adr/ADR-018-sesion-ligada-al-access-token.md) Sesión ligada al token | El access token lleva el Id de su sesión (`sid`) y la API comprueba en cada petición que siga activa: cerrar una sesión corta también su token | 6 |
| [019](../adr/ADR-019-purga-de-sesiones-y-datos-del-dispositivo.md) Purga y datos del dispositivo | El primer servicio en segundo plano (purga de refresh tokens caducados) y qué se guarda de cada sesión: IP truncada y el navegador como etiqueta de un conjunto cerrado | 6, 8 |
| [020](../adr/ADR-020-correo-saliente.md) Correo saliente | Un puerto `IEmailSender` y un transporte SMTP (MailKit); la invitación se envía por correo y el token deja de devolverse; sin cola de momento porque el correo lleva un secreto; el enlace sale solo de la configuración | 6, 8 |
| [021](../adr/ADR-021-recuperacion-de-contrasena.md) Recuperar la contraseña | Respuesta idéntica y **tiempo medido** (un suelo de 250 ms), correo por una **cola en memoria** (el token no se guarda en claro), token de una hora y un solo uso, y restablecer **cierra todas las sesiones** en una sola confirmación sin tocar el segundo factor | 1, 6, 8 |
| [022](../adr/ADR-022-agenda.md) La agenda | Las fechas son **instantes UTC con desfase obligatorio** (una hora sin desfase se rechaza), el día lo decide el navegador con la medianoche local (no con 168 horas), `Jobs.Schedule` es un permiso aparte, la agenda es un rango obligatorio de hasta 42 días sobre un índice que empieza por la empresa, y se difieren los conflictos de horario, el huso por empresa y la concurrencia |
| [023](../adr/ADR-023-crear-una-cuenta.md) Crear una cuenta | **No existe ninguna cuenta hasta abrir el enlace del correo** (contra el *pre-hijacking*), la contraseña se elige al completar, la respuesta y el tiempo son uniformes (suelo de 250 ms medido), el aviso de «ya tienes cuenta» tiene su propio límite, las condiciones de uso se guardan con versión y fecha, y el registro inmediato se retira | 1, 6, 8 |
| [024](../adr/ADR-024-segundo-factor-obligatorio.md) Segundo factor obligatorio | **Una puerta por petición** en `AuthorizationBehavior` con el rol efectivo (sin token «limitado», así que promover o reiniciar surte efecto en la siguiente petición), una **lista cerrada de cuatro operaciones** permitidas marcadas con un atributo (lo no marcado se deniega), **no se puede desactivar con un rol que lo exige**, el estado del factor viaja en **la misma consulta** que la pertenencia (una subconsulta: ni una ida más), y `me` lleva dos campos nuevos. Riesgo aceptado: la **ventana de enrolamiento** | 6, 8 |
| [025](../adr/ADR-025-cambiar-contrasena-dentro.md) Cambiar la contraseña estando dentro | La **actual** (con la función del segundo factor: cuenta para el bloqueo) y, con factor, un **código**, en ese orden y antes de gastar nada; **todo o nada en una confirmación** (la contraseña, las demás sesiones cerradas **conservando esta**, los enlaces de recuperación anulados y el contador a cero); **revocar y no borrar**; fuera de la lista cerrada del segundo factor; permiso `Account.ChangePassword`, evento `1023` y errores `password.current_invalid` y `password.unchanged`. Riesgo aceptado: sin factor, una sesión robada que conozca la contraseña puede cambiarla | 6, 8 |
| [026](../adr/ADR-026-presupuestos.md) Presupuestos | El dinero en **céntimos enteros** calculado solo por el servidor (una petición nunca lleva un total); el IVA **una vez por tipo** con una lista cerrada; un presupuesto enviado **inmutable** y la concurrencia vigilada con `RowVersion` más `Revision`; el número por empresa con índice único y reintento; tres permisos (`Quotes.Read`, `Write`, `Issue`) y un `Technician` sin precios. Riesgos aceptados: aceptar es una anotación interna y el cálculo está en dos sitios | 0, 8, 9 |
| [027](../adr/ADR-027-perfil-fiscal-empresa.md) Perfil fiscal de la empresa | Una **entidad propia de la empresa** (no columnas de `Tenant`), el NIF/NIE/CIF **validado por algoritmo** y guardado normalizado, `GET` vacío y `PUT` que sustituye entero, dos permisos (`Company.Read` para cuatro roles y `Company.Manage` para `Owner` y `Admin`) y **ningún dato personal en un registro** (el evento `1409` solo dice quién y cuándo). Riesgos aceptados: quien gestiona puede cambiar el NIF sin historial, un NIF bien formado no es uno que exista y gana la última escritura |

## Las especificaciones

Cada spec está en [`docs/specs/`](../specs/) con su `spec.md` y su `tasks.md`.

| Spec | Qué añadió |
|---|---|
| **001** Clientes | Crear, ver, listar, modificar y eliminar clientes |
| **002** Tenancy e Identity | Registro de una empresa y su primer usuario, login y JWT |
| **003** Trabajos | Trabajos de un cliente y sus transiciones de estado (nuevo, en curso, completado, cancelado) |
| **004** Seguridad base | Cabeceras, HSTS, límite de peticiones, analizadores de seguridad y eventos de seguridad |
| **005** Pipeline DevSecOps | Repositorio público, CI, CodeQL, gitleaks y ZAP (quedan por hacer dos pruebas manuales) |
| **006** Códigos de error | Diccionario de mensajes y formato de error estable |
| **007** Aislamiento endurecido | Guardarraíles y tests que garantizan que una empresa nunca ve los datos de otra |
| **008** RBAC | Los cinco roles se comprueban: permisos por operación y `AuthorizationBehavior` |
| **009** Gestión de usuarios | Invitar, aceptar, listar, cambiar el rol y quitar; revalidación del rol en cada petición |
| **010** Segundo factor y bloqueo de cuenta | TOTP opcional, login en dos pasos, códigos de recuperación, reinicio por un gestor y bloqueo de cuenta por intentos fallidos |
| **011** Panel de operador | Listar, suspender y reactivar empresas con una identidad y una sesión aparte, y segundo factor obligatorio |
| **012** Quién soy | `GET /account/me`: la cuenta, la empresa, el rol efectivo y los permisos |
| **013** Cerrar sesión en el servidor | `POST /auth/logout` revoca el refresh token |
| **014** Cerrar todas mis sesiones | `DELETE /account/sessions` revoca todos los refresh tokens de la cuenta |
| **015** El token muere con su sesión | `sid` en el access token, comprobado en cada petición |
| **016** Purga y sesiones | Purga de refresh tokens caducados en segundo plano; ver y cerrar mis sesiones concretas |
| **017** Correo e invitaciones | Un puerto de correo con transporte SMTP; la invitación se envía por correo (sin token para quien invita), con límite de 20 por hora y por persona y eventos `1407`/`1408` |
| **018** Recuperar la contraseña | `POST /auth/forgot-password` (siempre `202`, con un suelo de tiempo medido) y `POST /auth/reset-password` (cierra todas las sesiones, reinicia el bloqueo y avisa por correo), con una cola de correo en memoria; los eventos `1012`–`1017`; y las dos pantallas del frontend |
| **019** Cambiar la contraseña | `PUT /account/password` (la actual y, con factor, un código; cierra las demás sesiones y conserva esta; anula los enlaces de recuperación pendientes; todo o nada), permiso `Account.ChangePassword` (15 permisos, 75 celdas), evento `1023`, errores `password.current_invalid` y `password.unchanged` (50 códigos), límite de 10 por hora y por IP, y una tarjeta en la pantalla de seguridad |
| **020** La agenda mínima | Programar, reprogramar y quitar de la agenda un trabajo (`PUT` y `DELETE /jobs/{id}/schedule`, permiso `Jobs.Schedule`), `Start` también desde `Scheduled`, y `GET /jobs/agenda` (un rango semiabierto de hasta 42 días); en el frontend, la pantalla de la agenda y la sección «Programación» del trabajo |
| **021** Crear una cuenta | `POST /auth/registration` (siempre `202`, no crea nada) y `POST /auth/registration/complete` (la cuenta nace al abrir el enlace, con la contraseña elegida entonces; no abre sesión), condiciones de uso aceptadas con versión y fecha (también al aceptar una invitación), `POST /auth/register` **retirado**, los eventos `1018`–`1022` y las pantallas de registro y de completar del frontend |
| **022** Segundo factor obligatorio | Para `Owner` y `Admin`: la puerta por petición (`403 mfa.enrollment_required`), la lista cerrada de cuatro operaciones permitidas, no poder desactivarlo (`400 mfa.required_for_role`), `secondFactorEnrollmentRequired` y `secondFactorRequired` en `me`, los eventos `1304` y `1508`, y en el frontend el guardián, el menú reducido, el interceptor y la pantalla de seguridad con aviso |
| **023** Presupuestos | Fase 3: crear, ver, listar (paginado y con filtros), modificar y eliminar un **borrador** y **enviar, aceptar y rechazar** (`/api/v1/quotes`), con el total calculado por el servidor, un número por empresa, un enviado inmutable, control de concurrencia, tres permisos, 64 códigos de error en total y 18 permisos; y en el frontend el listado y una pantalla con vista previa del total (los mismos 15 casos fijos que el backend) |
| **024** Perfil fiscal de la empresa | Fase 3: el perfil fiscal (nombre fiscal, NIF/NIE/CIF validado, dirección, teléfono y email; uno por empresa) con `GET` y `PUT /api/v1/company/profile`, dos permisos (`Company.Read` y `Company.Manage`), el evento `1409` sin datos y, en el frontend, la pantalla «Datos de la empresa»; es el prerrequisito del PDF (crea ADR-027) |

## Lo que viene

Lo que **aún no existe** está explicado con honestidad en cada capítulo (sección "Dónde están hoy los límites") y resumido en el [capítulo 0](./00-que-es-ofiflow.md). Lo más relevante: **los textos legales** que se aceptan al crear una cuenta (los redacta el dueño del proyecto), las *passkeys* (el segundo factor, ya obligatorio para `Owner` y `Admin`, no resiste al *phishing*), recuperar a un `Owner` que lo pierde todo, que el aviso de un cambio de contraseña permita **deshacerlo**, de la agenda los conflictos de horario y el huso por empresa, que un técnico solo vea sus trabajos, el PDF de los presupuestos y mandarlos por correo, y las facturas (fase 4).

## Siguiente capítulo

El **Anexo** reúne las preguntas más probables de un tribunal o de una entrevista de seguridad, con respuestas cortas y el capítulo donde profundizar.
