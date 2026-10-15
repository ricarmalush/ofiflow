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
Con un límite de peticiones por IP: 5 logins por minuto, 3 registros por hora, 10 aceptaciones de invitación por hora. Al pasarse, un 429 y un evento de seguridad. **Límite honesto:** no hay bloqueo por cuenta (un ataque repartido entre muchas IP no lo ve). *(Cap. 8)*

**¿Se puede saber si un email está registrado?**
El login responde un único 401 para "email desconocido" y "contraseña incorrecta". **Pero** el registro dice "ya está registrado" y el tiempo de respuesta del login delata si el email existe: son dos límites conocidos y anotados. *(Cap. 6, 8)*

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

## 5. Seguridad general

**¿Cómo piensas la seguridad del proyecto?**
En tres ideas: **STRIDE** para listar amenazas, **defensa en profundidad** (varias capas, para que si una falla otra proteja) y **prevenir, detectar y responder**. El capítulo 8 cierra con un mapa de amenazas: para cada una, el control, dónde está y qué test lo vigila. *(Cap. 8)*

**¿Cómo evitas la inyección SQL?**
EF Core parametriza las consultas; los analizadores de seguridad del compilador están como **error**; y un test de arquitectura prohíbe las APIs de SQL crudo. *(Cap. 8)*

**¿Qué cabeceras de seguridad se envían?**
`X-Content-Type-Options`, `X-Frame-Options`, `Referrer-Policy`, una `Content-Security-Policy` muy restrictiva y `Cross-Origin-Resource-Policy`; y HSTS fuera de desarrollo. La única excepción es la pantalla de Swagger, solo en desarrollo y solo en su ruta, con tests que impiden que se extienda. *(Cap. 8)*

**¿Qué se registra y qué no?**
Hay 14 eventos de seguridad con identificador estable (`1001`...`1406`). Llevan Ids, rol y operación; **nunca** emails, nombres, contraseñas ni tokens, y hay tests que buscan esos valores en todos los registros. **Límite honesto:** se registra, pero nadie recibe una alerta. *(Cap. 8)*

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
- **No hay doble factor de autenticación ni bloqueo de cuenta** tras intentos fallidos.
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
Una respuesta defendible: el **doble factor y el bloqueo de cuenta**, porque la cuenta del dueño de una empresa (y más adelante la del operador de la plataforma) es el objetivo más valioso y hoy solo la protege una contraseña y un límite por IP. Lo siguiente, que el técnico solo vea sus trabajos. **Lo importante es poder explicar por qué eliges ese**, no cuál eliges.

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
