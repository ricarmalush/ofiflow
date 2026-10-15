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
| **Agregado** (*aggregate*) | Un grupo de objetos que se trata como una unidad y se guarda de una vez (por ejemplo, `Job`, `Invitation`). Solo se entra por su raíz | 3 |
| **Autenticación** (*authentication*) | Saber **quién** eres (el login) | 6 |
| **Autorización** (*authorization*) | Saber **qué puedes hacer** (permisos y roles) | 6, 8 |
| **Behavior** (de MediatR) | Un paso que se ejecuta antes o después de cada operación, para todas: autorización y validación | 4 |
| **Bearer** | Esquema de autenticación en el que el token se envía en la cabecera `Authorization: Bearer <token>` | 6 |
| **Caso de uso** (*use case*) | Una cosa que se puede hacer con el sistema ("crear un trabajo"). En OfiFlow, un *Command* o una *Query* | 3 |
| **Claim** | Un dato dentro de un token (quién es, de qué empresa). Está firmado: no se puede alterar sin que se note | 6 |
| **Clean Architecture** | Organizar el código en capas concéntricas donde las dependencias apuntan hacia dentro | 2 |
| **CodeQL** | Analizador de GitHub que sigue el flujo de los datos buscando vulnerabilidades | 10 |
| **Command / Query** | Una operación que cambia algo / una que solo lee | 3 |
| **Concurrencia optimista** (*optimistic concurrency*) | Dos peticiones leen lo mismo; al guardar, la base de datos solo deja confirmar a una (columna `rowversion`) | 6 |
| **CQRS** | Separar las operaciones que cambian datos de las que los leen | 2 |
| **CSP** (*Content-Security-Policy*) | Cabecera que le dice al navegador qué puede cargar o ejecutar una respuesta | 8 |
| **DAST** | Probar la aplicación **en ejecución**, desde fuera, como un atacante (OWASP ZAP) | 10 |
| **DDD** (*Domain-Driven Design*) | Modelar el código alrededor de las reglas del negocio | 2 |
| **Defensa en profundidad** (*defense in depth*) | Varias protecciones seguidas, para que si una falla la siguiente aún proteja | 8 |
| **Dependabot** | Robot de GitHub que abre PRs cuando salen versiones nuevas de las dependencias | 10 |
| **Diccionario de mensajes** | El fichero `ErrorMessages.resx` con el texto de cada código de error | 7 |
| **Domain** | La capa del centro: las reglas del negocio, sin depender de nada externo | 3 |
| **DTO** (*Data Transfer Object*) | Un objeto que solo transporta datos hacia fuera (la respuesta de la API) | 3 |
| **EF Core** | La herramienta que traduce C# a SQL y guarda los datos | 3 |
| **Fail-closed** | Ante la duda, se deniega. Una operación sin permiso declarado no se ejecuta | 8 |
| **FluentValidation** | La librería que valida los datos de entrada con reglas legibles | 7 |
| **Filtro global de consultas** (*Global Query Filter*) | Una condición `TenantId == <empresa actual>` que EF Core añade a toda consulta de una entidad de empresa | 5 |
| **Gitleaks** | Escáner que busca secretos (contraseñas, claves) en el código y en todo el historial | 10 |
| **Guardarraíl** (*guardrail*) | Un control automático (compilador o test) que impide romper una regla sin darse cuenta | 8, 9 |
| **Handler** | El código que ejecuta un *Command* o una *Query* | 3 |
| **Hash** | Resultado de una función de un solo sentido: no se puede volver al original. Así se guardan contraseñas y tokens | 6 |
| **HSTS** | Cabecera que ordena al navegador usar siempre HTTPS con ese sitio | 8 |
| **Infrastructure** | La capa que habla con lo de fuera: base de datos, tokens, contraseñas | 3 |
| **Invitación** (*invitation*) | La forma de que una persona entre en una empresa que ya existe: un token de un solo uso con caducidad | 6 |
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
| **OpenAPI / Swagger** | La descripción de la API en JSON y la pantalla para probarla desde el navegador | 1 |
| **OWASP** | Organización que publica listas de riesgos y herramientas de seguridad web (como ZAP) | 8, 10 |
| **PBKDF2** | Función de hash lenta y con sal para contraseñas | 6 |
| **Permiso** (*permission*) | Lo que se puede hacer, con el formato `Entidad.Acción` (`Jobs.Assign`) | 8 |
| **ProblemDetails** (RFC 9457) | El formato estándar de las respuestas de error de la API | 7 |
| **PR** (*Pull Request*) | La petición de fusionar una rama en `master`, donde corren las comprobaciones | 10, 11 |
| **Rate limiting** | Limitar cuántas peticiones puede hacer una IP en una ventana de tiempo | 8 |
| **RBAC** (*Role-Based Access Control*) | Autorización basada en roles: cada rol tiene unos permisos | 8 |
| **Refresh token** | Un segundo token, de larga duración, para renovar la sesión; se rota en cada uso | 6 |
| **Revalidación** | Comprobar en cada petición, en la base de datos, que la persona sigue en la empresa y con qué rol | 8 |
| **Rol** (*role*) | Un conjunto de permisos con nombre: `Owner`, `Admin`, `Manager`, `Technician`, `Employee` | 8 |
| **SAST** | Analizar el **código** sin ejecutarlo para buscar vulnerabilidades | 10 |
| **SCA** | Vigilar las **dependencias** por vulnerabilidades conocidas (NuGetAudit, Dependabot) | 10 |
| **Spec-Driven Development** | Escribir la especificación antes que el código | 11 |
| **STRIDE** | Seis tipos de amenaza (suplantación, manipulación, repudio, revelación, denegación de servicio, elevación de privilegios) | 8 |
| **Tenant** | Una empresa cliente de OfiFlow | 5 |
| **Testcontainers** | Librería que arranca un SQL Server real en Docker desde el propio test | 9 |
| **TimeProvider** | La forma de pedir la hora en .NET, sustituible en los tests para no esperar | 11 |
| **Token** | Una credencial de un solo valor secreto (de acceso, de renovación o de invitación) | 6 |
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

## Lo que viene

Lo que **aún no existe** está explicado con honestidad en cada capítulo (sección "Dónde están hoy los límites") y resumido en el [capítulo 0](./00-que-es-ofiflow.md). Lo más relevante: el panel de operador de la plataforma, la agenda y la asignación de técnicos (Fase 2), el envío de invitaciones por email, el doble factor de autenticación y el bloqueo de cuentas, y que un técnico solo vea sus trabajos.

## Siguiente capítulo

El **Anexo** reúne las preguntas más probables de un tribunal o de una entrevista de seguridad, con respuestas cortas y el capítulo donde profundizar.
