# Capítulo 9 — Tests

## Qué vas a aprender

- Qué es un test automático, cómo se escribe y cómo se lee.
- Por qué hay **cuatro proyectos de test** y qué prueba cada uno.
- Por qué los tests más importantes usan un **SQL Server de verdad** (en Docker) y no una base de datos "de mentira".
- Las técnicas que hacen fiable un test de seguridad: controles positivos, meta-tests, centinelas y **pruebas de mutación**.
- Cómo ejecutarlos y qué hacen en el pipeline.
- **Cuántos tests escribir**: por qué la batería es grande y el criterio para que sea proporcional al riesgo.
- Qué errores reales cometimos (y cazamos) por el camino, y qué límites tiene hoy la batería.

## Para qué sirven

Un **test automático** es un programa corto que ejecuta una parte del sistema y comprueba que el resultado es el esperado. Se ejecutan en segundos y repetidamente, así que sirven para dos cosas:

1. **Demostrar que algo funciona** ahora.
2. **Avisar si deja de funcionar** mañana, cuando alguien cambie código sin darse cuenta.

En un proyecto de seguridad, la segunda es la que más importa: un control que funciona hoy pero que nadie vigila es un control que alguien puede romper por accidente sin enterarse.

## Los cuatro proyectos

Los proyectos de `tests/` reflejan las capas del capítulo 3. Cuanto más hacia fuera, más realista y más lento es el test:

| Proyecto | Prueba | Con qué | Casos | Tarda | ¿Docker? |
|---|---|---|---|---|---|
| `OfiFlow.Domain.Tests` | Las reglas del negocio | Nada: C# puro | 229 | ~0,1 s | No |
| `OfiFlow.Application.Tests` | Los casos de uso, los validadores y el pipeline | Base en memoria y dobles de prueba | 575 | ~1 s | No |
| `OfiFlow.Infrastructure.Tests` | Persistencia, tokens y aislamiento entre empresas | **SQL Server real** en Docker (y base en memoria para lo ligero) | 315 | ~55 s | **Sí**, en parte |
| `OfiFlow.Api.Tests` | HTTP, seguridad, arquitectura y extremo a extremo | La API entera en memoria; con SQL Server real en los de extremo a extremo | 461 | ~2 min | **Sí**, en parte |

En total, **1580 casos**. Un "caso" no es lo mismo que un método de test: hay unos 980 métodos, pero un mismo método con `[Theory]` se ejecuta varias veces con datos distintos (la matriz de permisos es un solo método que se ejecuta 60 veces).

---

## Cómo es un test

Todos siguen el mismo patrón de tres pasos, **AAA**: *Arrange* (preparar), *Act* (actuar), *Assert* (comprobar). Un test real de `Domain`:

```csharp
// tests/OfiFlow.Domain.Tests/Jobs/JobTests.cs
[Fact]
public void Start_FromNew_TransitionsToInProgress()
{
    var job = CreateJob();                      // Arrange: un trabajo nuevo

    job.Start();                                // Act: lo empezamos

    Assert.Equal(JobStatus.InProgress, job.Status);   // Assert: ahora está en curso
}
```

- **`[Fact]`** es un test con un único caso. **`[Theory]`** es un test que se repite con varios datos (`[InlineData(...)]`):

```csharp
[Theory]
[InlineData("")]
[InlineData("   ")]
public void Create_WithoutTitle_Throws(string title)
{
    var exception = Assert.Throws<DomainException>(() => Job.Create(TenantId, CustomerId, title, null, JobPriority.Normal));

    Assert.Equal(JobErrors.TitleRequired, exception.Code);
}
```

- **El nombre cuenta una frase:** `Método_Condición_Resultado`. `Create_WithoutTitle_Throws` se lee "crear sin título lanza un error". Así, cuando un test falla, su nombre ya dice qué se rompió.
- Un test **no debe depender de otro** ni del orden: cada uno prepara sus propios datos.

---

## Parte 1 — Domain: tests puros

Las reglas del negocio (que un trabajo no se pueda completar sin empezarlo, que un email tenga formato válido) se prueban sin nada alrededor: ni base de datos, ni servidor. Por eso son los más rápidos (una décima de segundo para casi 200 casos) y los más fáciles de escribir. Aquí también viven las dos **tablas de seguridad**: la matriz de permisos, con una celda "rol × permiso" por caso (55 celdas, `RolePermissionsTests`), y la de jerarquía de roles, con una celda "quién actúa × a quién gestiona" por caso (25 celdas, `ManageableRolesTests`). Y las reglas de las invitaciones (`InvitationTests`): que caduquen, que se usen una sola vez y que el rol `Owner` no se pueda conceder.

## Parte 2 — Application: casos de uso con dobles de prueba

Un *handler* necesita una base de datos y otras piezas (quién es el usuario, cómo se emiten tokens). En los tests se sustituyen por **dobles de prueba** (*test doubles*): versiones simples y controlables.

| Doble | Qué sustituye | Fichero |
|---|---|---|
| `TestDbContext` | La base de datos, por una **en memoria** (`TestDbContextFactory.Create()` da una nueva y vacía en cada test) | `Common/TestDbContext.cs` |
| `FakeTenantContext` | "¿En qué empresa estoy?" | `Common/FakeTenantContext.cs` |
| `FakeCurrentUser` | "¿Quién soy y qué rol tengo?" Distingue el rol **efectivo** (el de la base de datos) del que diría el token, para probar que decide el primero, y cuenta cuántas veces se verifica la pertenencia | `Common/FakeCurrentUser.cs` |
| `FakeTimeProvider` | "¿Qué hora es?" (para probar que una invitación caduca a los 7 días **sin esperar 7 días**: el test avanza el reloj) | `Common/FakeTimeProvider.cs` |
| `FakeIdentityService`, `FakeTokenService` | Las contraseñas (`AuthenticateAsync`, sin bloqueo real) y los tokens | `Common/` |

**Pruebas de oro: los vectores de las RFC (spec 010).** El código de 6 dígitos del segundo factor (`Totp`) no se prueba comparándolo con lo que calcula el propio código, que sería darle la razón a lo que se prueba, sino con los **valores que publican las propias RFC**: los diez de HOTP (RFC 4226, Apéndice D) y los seis de TOTP (RFC 6238, Apéndice B). Si alguien cambia una máscara, un desplazamiento o el orden de los bytes, falla un caso con el código esperado concreto. Igual con Base32 (RFC 4648) y con el hash de los códigos de recuperación, cuyo valor de referencia se calculó con OpenSSL y no con este proyecto. Son funciones puras (el reloj entra por parámetro), por eso no necesitan ningún doble.

Un test típico de un handler:

```csharp
// CreateJobCommandHandlerTests.cs
await using var db = TestDbContextFactory.Create();                      // base vacía
var tenantContext = new FakeTenantContext(Guid.NewGuid());               // una empresa cualquiera
db.Customers.Add(Customer.Create(tenantContext.TenantId, ...));          // un cliente existente
await db.SaveChangesAsync();

var jobId = await new CreateJobCommandHandler(db, tenantContext)         // Act
    .Handle(new CreateJobCommand(customer.Id, "Reparar fuga", ...), CancellationToken.None);

var job = await db.Jobs.FirstAsync(j => j.Id == jobId);                  // Assert
Assert.Equal(JobStatus.New, job.Status);
```

También hay tests del **pipeline real de MediatR** (`AuthorizationPipelineTests`): construyen el contenedor con `AddApplication()` y envían comandos de verdad, para comprobar el orden de los behaviors (la autorización va antes que la validación).

### Un límite que conviene entender

> **Los tests de Application NO prueban el aislamiento entre empresas.** El `TestDbContext` es una base en memoria *sin el filtro por empresa*: si un handler olvidara filtrar, estos tests no lo notarían. Es a propósito: ese aislamiento lo prueba la capa siguiente, con la base de datos real. Por eso existe `OfiFlow.Infrastructure.Tests`.

---

## Parte 3 — Infrastructure: la base de datos de verdad

### Por qué no basta una base en memoria

La base en memoria de EF Core **no es SQL Server**: no ejecuta SQL real ni traduce las consultas igual. Un filtro por empresa que funcione en memoria podría traducirse mal al hablar con SQL Server de verdad, y el test pasaría igualmente. Para lo más importante del proyecto, el aislamiento entre empresas, eso no vale.

### Testcontainers: un SQL Server desechable

**Testcontainers** es una librería que arranca un contenedor de Docker desde el propio test. El [`SqlServerContainerFixture`](../../tests/OfiFlow.Infrastructure.Tests/Common/SqlServerContainerFixture.cs):

1. **Arranca un SQL Server 2022** en un contenedor.
2. **Aplica las migraciones reales** (`MigrateAsync`), así que el esquema es el de producción.
3. Entrega contextos de base de datos para cada test.
4. **Destruye el contenedor** al terminar: no deja nada.

Detalles de diseño:

- **La imagen está fijada** (`mssql/server:2022-CU14-ubuntu-22.04`): los tests usan siempre la misma versión, en tu máquina y en el pipeline, en vez de la que traiga por defecto cada versión de la librería.
- **Un contenedor por clase de test**, no por test (`IClassFixture`): arrancar uno tarda unos segundos, y repetirlo en cada test multiplicaría el tiempo por veinte. Para que los tests de una misma clase no se pisen, **cada test usa empresas nuevas** (`Guid.NewGuid()`).
- Requiere **Docker arrancado** en tu máquina. El pipeline de GitHub ya lo trae.

Aquí viven el test obligatorio de aislamiento (`TenantIsolationIntegrationTests`) y los de escritura entre empresas (`CrossTenantWriteIsolationTests`), que ejecutan los handlers reales contra SQL Server real. Con la spec 009 se añade `InvitationPersistenceTests`, que comprueba lo que una base en memoria **no puede** comprobar:

- **Los índices únicos:** dos invitaciones no pueden compartir el hash del token, ni siquiera de empresas distintas.
- **La concurrencia:** dos peticiones que aceptan la misma invitación a la vez leen las dos "pendiente"; al guardar, la base de datos obliga a que **solo una gane** (`DbUpdateConcurrencyException`). Un `if` en el código no lo garantiza, porque comprobar y escribir serían dos pasos.
- **Que las migraciones coinciden con el modelo** (`HasPendingModelChanges`): si alguien cambia una entidad y se olvida de generar la migración, falla este test y no la base de producción.
- **Que una baja es todo o nada:** quitar a un miembro borra su cuenta, su ficha, su pertenencia y sus sesiones en una transacción; si una parte falla, **nada** se borra, y si todo va bien no queda ninguna fila huérfana.

Cada uno va con su control positivo (un caso que sí funciona, para que el "falla" no se deba a un error de preparación), y se verificaron rompiendo la **migración** a propósito, que es como se rompería de verdad el esquema.

Y `CurrentUserMembershipTests`, para la **revalidación en cada petición** (también contra SQL Server real, porque lo que se prueba es una consulta de verdad):

- **Decide la base de datos, no el token:** un token que dice `Owner` de alguien que es `Technician` en la base de datos da como rol efectivo `Technician`. Y un claim ausente o raro no cambia nada.
- **Quitar o degradar se nota en la siguiente petición:** se cambia el rol en la base de datos sin renovar el token, y la petición siguiente ya ve el rol nuevo.
- **Solo cuenta la pertenencia a esa empresa y a esa persona:** ni la de otra empresa ni la de otro usuario de la misma.
- **Fail-closed:** hasta verificar no hay rol, y un token sin usuario o sin empresa se deniega **sin tocar la base de datos**.
- **El coste:** un interceptor cuenta las consultas y comprueba que verificar cuesta **exactamente una**, aunque se pregunte varias veces. Es la forma de que "una consulta por petición" no sea solo una promesa del manual.

Y `AccountLockoutTests`, para el **bloqueo de cuenta** (spec 010, bloque 3), con la técnica que más conviene entender del capítulo: probar una **carrera**.

- **El esquema ingenuo falla justo aquí.** «Leer el contador, comprobar la contraseña y sumar si falla» parece correcto, y todos los tests secuenciales pasan. Pero con 50 peticiones simultáneas las 50 leen «0 fallos». El test lanza **50 peticiones a la vez**, cada una con su propio contexto como en la API real, contra SQL Server de verdad, y exige que **solo 5 lleguen a comprobar la contraseña** y que la cuenta quede bloqueada. Existe otra versión por HTTP, con la API entera.
- **Contar sin cronómetros.** El test no mide milisegundos (serían intermitentes): el verificador de contraseñas es una pieza sustituible (`IPasswordChecker`) y el test usa una versión que **cuenta las llamadas** y si fueron contra un hash real o contra el ficticio. Así se comprueba que «email desconocido», «cuenta bloqueada» y «contraseña errónea» hacen cada uno **exactamente una verificación completa**: igual coste, sin medir tiempo.
- **El reloj lo controla el test** (`FakeTimeProvider`): el bloqueo de 15 minutos se prueba avanzando el reloj 14 y luego 1 minuto más, incluido el límite exacto, sin esperar nada.
- **Invariante, no solo ejemplos.** Cinco rondas de 20 peticiones simultáneas comprueban que el contador **nunca pasa de 5** y que la hora de bloqueo existe exactamente cuando llega a 5, sea cual sea el reparto.
- **Los controles positivos de siempre:** antes de los fallos la contraseña correcta entra; con 4 fallos y un quinto intento correcto, entra; tras 15 minutos, entra; y los fallos de una cuenta no tocan a otra.

Y `MfaServiceTests`, para **lo que los handlers del segundo factor dan por supuesto** (spec 010, bloque 4), siempre contra SQL Server real porque son garantías de base de datos:

- **El mismo código, a la vez.** Cuatro peticiones simultáneas con el mismo código TOTP, y otras cuatro con el mismo código de recuperación: **exactamente una** es válida. Sin la anotación atómica del paso (o del `UsedAt`), las cuatro lo darían por bueno. Con 20 a la vez, el bloqueo puede dejar fuera incluso a la correcta; el test solo exige lo que no puede pasar nunca: que entren **dos**.
- **Los pasos del tiempo.** Un código de un paso posterior invalida los anteriores aunque estén en la ventana de ±30 s; el paso siguiente trae un código nuevo; el mismo código dos veces falla la segunda. El reloj lo controla el test.
- **Los fallos cuentan, los aciertos no reinician.** Un código erróneo suma uno; cinco bloquean la cuenta y entonces ni el correcto entra; un acierto devuelve su intento **sin** poner el contador a cero (hay un test con dos fallos previos que exige que siga en dos).
- **Falla cerrado.** Un secreto cifrado copiado a la fila de otra cuenta no se descifra: la comprobación lanza un error criptográfico, nunca "código válido".
- **Ningún secreto en el registro:** tras varios aciertos, repetidos y fallos, ningún evento contiene el código, el secreto (en Base64 ni Base32) ni los códigos de recuperación.

`PasswordReconfirmationTests` repite para la contraseña que se pide de nuevo antes de una operación sensible lo que `AccountLockoutTests` hace con el login: los tres casos hacen una sola verificación completa, 50 peticiones simultáneas dan 5 comprobaciones reales, y un acierto no reinicia el contador.

Los handlers se prueban en Application con un **doble del servicio** (`FakeMfaService`): ahí solo importa el **orden** y los **datos** (la contraseña antes que el estado, el código antes de borrar, las sesiones cerradas en el mismo guardado, y que los hashes guardados sean exactamente los de los códigos que se muestran). Los 25 casos de la **jerarquía del reinicio** se escriben aparte de la tabla de producción, copiados literales, igual que las matrices de las specs 008 y 009.

Y los del **login en dos pasos** (spec 010, bloque 5), que repiten el reparto de siempre:

- **Application, con dobles.** `LoginCommandHandlerTests` comprueba la promesa central con un contador en el doble del servicio de tokens: con el segundo factor activo y la contraseña correcta, **`IssuedSessions == 0`**. No se emitió ni un token. También que el desafío guardado es el *hash* del entregado, que caduca a los 5 minutos y que cada login recibe uno distinto. `VerifyMfaChallengeCommandHandlerTests` comprueba que un canje rechazado, una forma mal escrita o una cuenta sin empresa no dan sesión, y que la sesión sale para la empresa **de la pertenencia** (la petición no tiene campo para elegirla: un test lo fija mirando las propiedades del comando).
- **SQL Server real, el canje.** El desafío se canjea **una sola vez** (cuatro canjes simultáneos, una sesión; y dos códigos *distintos* a la vez, también una), muere a los 5 códigos erróneos **aunque se levante a mano el bloqueo de la cuenta** (para aislar los dos topes), caduca justo a los 5 minutos (con un control positivo a los 4:59), un desafío desconocido o caducado **no** suma contra la cuenta, y el desafío de una cuenta no se canjea con el código de otra.
- **SQL Server real, el contador.** `SecondFactorLoginTests` prueba el punto más fino: con MFA, la contraseña correcta **no reinicia** el contador (sin MFA, sí: control), y un ataque simulado que intercala la contraseña correcta entre los fallos sigue acabando en bloqueo al quinto.
- **Arquitectura.** Un test fija que solo **dos ficheros** llaman a `IssueTokensAsync` (el login y el canje), y otro que, dentro del login, la rama «falta el segundo factor» termina con un `return` **antes** de la línea que emite la sesión. Es el equivalente, para el segundo factor, de la lista blanca de `IgnoreQueryFilters()`.

Y los **tests HTTP del segundo factor** (spec 010, bloque 6), en la clase de API con SQL Server real:

- **Las rutas existen y exigen sesión.** Una teoría recorre las seis (cinco de la cuenta y el reinicio) sin token y exige 401; y los cinco roles ven el estado de su cuenta. La ruta de canjear el desafío, en cambio, es **anónima**: con tres formas distintas de fallo (desafío inventado, desafío mal escrito, código mal escrito) la respuesta es **el mismo cuerpo**, y un texto de 5000 caracteres es un 400 de validación (no se hashea).
- **Los errores llevan su código.** `mfa.invalid_password`, `mfa.setup_required`, `mfa.invalid_code` y `mfa.not_enabled` salen con 400 y su código estable; un código que no son 6 dígitos ni siquiera llega al handler. Y el contrato de errores gana las cinco filas `mfa.*` (cada código, su estado y su mensaje del diccionario).
- **El secreto no se cachea.** Pedir el secreto devuelve `Cache-Control: no-store`, el estado nunca lo repite, y un error no lleva esa cabecera.
- **El reinicio por un gestor, con su jerarquía:** el `Owner` puede (y repetirlo no falla), `Manager`/`Employee`/`Technician` reciben 403, un `Admin` no puede con otro `Admin` pero sí con un `Technician` (control positivo), nadie se reinicia a sí mismo, y un miembro de **otra empresa** da el mismo 404 y el mismo mensaje que uno inexistente.
- **Los límites de peticiones.** Con el limitador **antes** de la autenticación, se prueba sin sesión: diez peticiones a cada ruta de gestión dan 401 y la undécima, **429**; igual con canjear el desafío (10 por minuto). Cada ruta usa su propia IP de prueba, porque el límite es por IP y por política.

Estos tests no calculan códigos TOTP reales (por eso no pueden confirmar, desactivar ni regenerar con códigos válidos): eso lo hace el recorrido completo que se describe más abajo, con un reloj controlado.

Y **el recorrido entero** (spec 010, bloque 7): `MfaEndToEndTests`, con la API real, SQL Server real y **códigos TOTP de verdad**. Aquí está la técnica nueva de este bloque: el **reloj controlado**. El TOTP cambia cada 30 segundos y cada paso solo se acepta una vez, así que probar «login con MFA, otra vez, otra vez» con el reloj real obligaría a esperar. La fábrica de pruebas sustituye el `TimeProvider` de la API por uno que el test avanza a voluntad: 30 s para tener un código nuevo, 5 minutos para caducar un desafío, 15 para terminar un bloqueo. El test hace de app del móvil: decodifica el secreto Base32 que recibe y calcula el código con la misma clase `Totp` que ya se validó contra las RFC.

- **El ciclo completo, en un solo test:** registrar → pedir el secreto → confirmar → comprobar que la sesión anterior ya no renueva → login (que da un desafío y **ni un token**) → canjearlo con un código → usar un código de recuperación → desactivar → comprobar que el login vuelve a ser de un paso.
- **Cada ataque, con su control positivo:** fuerza bruta del código (acaba en bloqueo, y ni el código bueno entra después), código ya usado en un desafío nuevo, desafío ya canjeado, desafío caducado, desafío de una cuenta con el código de otra, y el desafío presentado como token de acceso en **cinco rutas** (con el token de verdad, las cinco dan 200).
- **Los seis fallos del segundo paso son indistinguibles.** Desconocido, ya usado, caducado, código erróneo, forma equivocada del desafío y código mal escrito: se comparan la respuesta completa (estado, cabeceras y cuerpo) y las seis cadenas tienen que ser **la misma**.
- **Carreras por HTTP:** cuatro canjes simultáneos del mismo desafío con el mismo código dan **una** sesión, y tres confirmaciones simultáneas activan el MFA **una** vez (las otras reciben un 400, nunca un 500).
- **Que no quede nada en ningún sitio.** Tras un ciclo, se leen las tres tablas **tal cual** (con SQL directo) y no contienen el secreto, ni los códigos de recuperación, ni el desafío en claro; y los eventos de seguridad (`1501`–`1507`, `1005`, `1006`) se registran **sin** ningún secreto, código, desafío, contraseña ni email (se busca cada valor conocido y, además, cualquier cosa con forma de email).
- **El error que solo cazó este nivel.** Un test que hacía 4 fallos y luego un login legítimo con MFA falló: la cuenta quedaba bloqueada por un intento que no fue un fallo (capítulo 6). Los tests de cada pieza por separado pasaban. Es la razón de ser de los tests de extremo a extremo: lo que se rompe en la **unión** de las piezas.

Y `MfaPersistenceTests`, para el **almacén del segundo factor** (spec 010, bloque 2), con la misma idea: lo que solo una base de datos de verdad puede garantizar.

- **Los índices únicos:** una cuenta no puede tener dos secretos TOTP (su clave primaria es la propia cuenta), y dos códigos de recuperación o dos desafíos no pueden compartir hash, ni siquiera de cuentas distintas.
- **La concurrencia, tres veces:** dos usos simultáneos del mismo código de recuperación, dos canjes del mismo desafío y dos actualizaciones del último paso aceptado de un secreto. En las tres solo una gana (`DbUpdateConcurrencyException`).
- **Que el secreto no queda en claro:** se lee la columna tal cual la guarda SQL Server, sin pasar por el modelo, y se comprueba que no contiene el secreto ni en Base64 ni en hexadecimal, y que con la clave se recupera.
- **Que borrar una cuenta no deja huérfanos:** desaparecen su secreto, sus códigos y sus desafíos, y los de otra persona siguen intactos; y si no se confirma la baja entera, no se borra nada.

Aparte, sin base de datos, `SecretProtectorTests` prueba el cifrado como lo que es: una función que debe **negarse**. Cualquier byte cambiado (nonce, etiqueta o texto), otra clave, otra cuenta o una versión desconocida acaban en `CryptographicException`, nunca en datos corruptos; y `MfaOptionsTests` junto con `MfaStartupTests` comprueban que la API entera **no arranca** sin la clave o con una de tamaño incorrecto (en entorno de producción, para que los user-secrets de la máquina no tapen el fallo).

Y los de la **spec 011** (el panel del operador de la plataforma, bloques 1 a 7), de riesgo alto porque una cuenta con acceso a todas las empresas es lo más delicado del sistema:

- **La suspensión de una empresa, en Domain y Application.** `Tenant.Suspend` y `Reactivate` rechazan suspender dos veces o reactivar a quien no está suspendido. `TenantStatusTests` comprueba la lectura en sí (suspendida, activa, inexistente, reactivada y la consulta componible). Que la suspensión se lea **en un solo sitio** (`TenantStatus`) lo fija el test de arquitectura de la lista blanca de lecturas de `Tenants`, y los cinco puntos que la aplican (cada petición, login, canje del segundo factor, renovación y aceptar invitación) se prueban de punta a punta en el siguiente punto.
- **La suspensión de punta a punta** (`TenantSuspensionEndToEndTests`, API con SQL Server real): una petición con un token aún válido recibe 403 `tenant.suspended` incluso del `Owner`; el login con la contraseña buena también, **antes** de crear el desafío del segundo factor y sin contar como intento fallido (siete seguidos no bloquean la cuenta); con la contraseña mala, el 401 de siempre, para que nadie sepa que la empresa existe sin credenciales; la renovación no revoca el token (vuelve a servir al reactivar) y la invitación no se consume.
- **El operador, con SQL Server real** (`PlatformOperatorPersistenceTests`): una cuenta no puede tener dos filas de operador (la clave primaria es la propia cuenta); una cuenta con pertenencia a una empresa **no** puede ser operador (regla R1); un operador dado de baja no se reactiva por la puerta de atrás; la fila de **auditoría** se puede insertar pero no modificar ni borrar desde la aplicación; y la auditoría y la acción **confirman juntas o no confirman**.
- **Que el desafío no cruce de mundo** (`MfaServiceTests`): un desafío del segundo factor creado para una empresa no se canjea como sesión de plataforma, ni al revés (R9).
- **Arquitectura.** Una entidad global sin clasificar rompe el test centinela; las dos tablas nuevas solo se leen desde sus ficheros; y `TenantMemberships_AreOnlyCreatedForBrandNewAccounts` fija que solo el registro y aceptar una invitación crean una pertenencia, que es lo que hace cierta la regla R1.
- **El alta y la baja del operador** (`PlatformOperatorAdministrationTests`, en Application y en Infrastructure, y `PlatformOperatorToolTests`; bloque 4). Con dobles se vigila el **orden** y el «todo o nada»: un primer código erróneo (de otra ventana, mal formado o vacío) no crea nada —ni cuenta, ni segundo factor, ni operador, ni auditoría, ni evento—, un email ocupado tampoco, y el éxito confirma **un solo** `SaveChanges`. Se prueba también el reloj desfasado del móvil: un código del paso anterior vale y se anota **su** paso. Con SQL Server real se prueba lo que un doble no puede: que la cuenta nace con el segundo factor **confirmado** y con el primer código **ya gastado** (ese mismo código no vale al iniciar sesión; el del paso siguiente y un código de recuperación, sí), que dos altas simultáneas con el mismo email dejan **un** operador y ninguna fila suelta de la que pierde, y que el servicio se niega a pisar un segundo factor existente. Los de arquitectura fijan que la API no puede llegar al alta (ni referencia el proyecto ni nombra el servicio), que no es una petición de MediatR, que la contraseña no entra por argumento y que la consola no recibe `Jwt:Secret`. Se hicieron 25 mutaciones (el alta sin exigir el código, el alta que guarda antes de terminar, la baja sin auditoría, el paso del primer código sin anotar, el segundo factor sin confirmar, la API que nombra el servicio, la consola con la clave JWT o con la contraseña por argumento...), todas detectadas. **Un error del propio test, que enseña algo:** el test que comprueba que no se pisa un segundo factor existente se escribió mal (pedía el secreto antes de guardar la cuenta) y fallaba siempre, así que la mutación que quitaba esa barrera salió «detectada» por el motivo equivocado. Se vio porque el test también fallaba **sin** mutación al pasar la suite entera. Una mutación solo cuenta si el test pasa sin ella; por eso las mutaciones siempre se acompañan de una pasada limpia.
- **La sesión de plataforma** (bloque 5; `PlatformLoginCommandHandlerTests`, `VerifyPlatformChallengeCommandHandlerTests`, `PlatformAuthorizationBehaviorTests`, `CurrentUserPlatformTests`, `PlatformAuthenticationTests`, `PlatformStartupTests` y `PlatformSessionEndToEndTests`). Con dobles se prueba cada rama del login y del canje y el **orden** (canjear, volver a comprobar al operador, firmar, auditar y confirmar). Con SQL Server real, que el operador se **revalida en la base de datos en cada petición con una sola consulta**, que un token con otro valor del claim de mundo (`tenant`, `Platform`, vacío) no es nunca una sesión de plataforma y ni siquiera consulta. Con la API entera: el ciclo completo; un token de empresa en una ruta de plataforma y al revés; tokens fabricados con la clave de empresa y la audiencia de plataforma (y a la inversa), caducados, con otra clave o sin el claim del mundo (`403`); un desafío de empresa canjeado en plataforma y al revés **sin gastarse**; la baja que corta la sesión en la siguiente petición y deja el `1608`; los seis fallos del primer paso con **huella idéntica y exactamente una verificación de contraseña cada uno** (contada con `CountingPasswordChecker`, sin cronómetros); y el límite de peticiones. Como aún no hay rutas de plataforma con efecto, el fixture (`PlatformFlowFixture`) añade **una ruta y una operación de prueba** que pasan por el pipeline real; un `IStartupFilter` las monta solo en los tests. Otro detalle de fixture: la firma de tokens de plataforma sigue el reloj real (como la de empresa) mientras el reloj controlado solo gobierna el segundo factor; si no, los tokens nacerían con `nbf` en el futuro. `PlatformStartupTests` comprueba, con la API entera en entorno de producción, que **no arranca** sin la clave, con una clave en blanco, que no sea Base64, de 16/31/33/64 bytes, igual que la de empresa (incluso escrita con otro espacio) o con la misma audiencia, y que ningún mensaje reproduce una clave. Los de arquitectura fijan que solo el canje emite sesiones de plataforma, que el login no recibe un servicio de tokens y el orden del canje, y que dentro de `OfiFlow.Application.Platform` solo hay operaciones de plataforma o anónimas.
- **El panel del operador** (bloque 6; `ListTenantsQueryHandlerTests`, `SuspendAndReactivateTenantCommandHandlerTests`, `PlatformTenantPanelPersistenceTests` y `PlatformTenantPanelEndToEndTests`). Con dobles se prueba la forma: el listado devuelve **exactamente cinco campos** (un test sobre el registro falla si alguien le añade una propiedad), el orden es total (alta descendente y, a igualdad, el Id: las dos empresas del mismo día se insertan al revés para que un orden que dependa de la inserción falle), dos páginas seguidas no repiten ni se saltan ninguna empresa, el filtro por suspendida, el tope de 100 (un `400`, no una página recortada), y que suspender y reactivar escriben **una** fila de auditoría y confirman **una** vez, que un error de negocio no deja ni fila ni evento y que el evento lleva solo Ids. Con SQL Server real lo que un doble no puede: que el recuento de miembros **cruza el filtro de empresa** (el contexto se abre para otra empresa y los recuentos siguen siendo los verdaderos), que el listado son **dos consultas** (el recuento y la página) y no una por empresa, y que la sentencia ordena por alta descendente **y por Id** (SQL Server devuelve los empates casi siempre en orden de Id, así que quitar el desempate no cambiaba ningún resultado: la mutación lo dejó claro y el test mira ahora el `ORDER BY` enviado), y que suspender es **todo o nada** de verdad: un interceptor hace fallar la sentencia de la auditoría (y, en otro test, la de la empresa) y, en los dos sentidos, no queda ni una suspensión sin rastro ni un rastro sin suspensión. La versión de fila se prueba de forma **determinista** (dos peticiones leen la empresa activa antes de que ninguna confirme y solo gana la primera) y, además, con ocho suspensiones simultáneas: una gana, las otras reciben `tenant.already_suspended` y queda **una** fila. Con la API entera: el cuerpo **entero** de la lista no contiene el email, el nombre de la persona, el cliente ni su teléfono o dirección; suspender corta a los miembros en su siguiente petición (login incluido) y reactivar les devuelve su sesión y sus datos; suspender una empresa no afecta a otra; los errores de negocio (`400`), la empresa inexistente (`404`) y que una sesión de empresa, sin sesión, con basura o de un operador dado de baja no entra en ninguna de las tres rutas; y el límite de 30 peticiones por minuto. Un detalle de un test que falló solo a veces: se ordenaban las filas de auditoría por hora y el reloj del fixture está parado (las dos tenían la misma), así que el orden no era estable; el test ya no depende de él.
- **Los ataques de punta a punta** (bloque 7; `PlatformPanelAttacksEndToEndTests`, con la API entera, SQL Server real y el reloj controlado). El recorrido completo en un solo test: el operador se da de alta, entra en dos pasos con un TOTP de verdad, lista, suspende, su miembro se queda fuera (con su token y con una sesión nueva), la otra empresa no se entera, reactiva y todo vuelve; y la auditoría y los eventos cuentan lo mismo (la sesión, `1601`, antes de la suspensión, `1604`). Después, cada ataque de la spec: **cincuenta inicios de sesión de plataforma simultáneos con la contraseña mala hacen exactamente cinco comprobaciones reales** y la cuenta queda bloqueada (la misma respuesta con la contraseña buena); cinco códigos erróneos matan el desafío aunque el sexto sea el bueno; un `Owner` no entra en ninguna ruta de plataforma ni la usa contra su propia empresa; el operador no ve ni un cliente, ni un trabajo, ni un miembro, ni una invitación; la sesión de plataforma no se renueva (no existe la ruta y el token no vale como *refresh token*); un `tenantId` en el cuerpo o en la consulta se ignora y solo se suspende el de la ruta; el email de un operador no se puede registrar como empresa ni invitar; suspender A deja a B trabajando en todo (invitaciones, renovación y registro de empresas nuevas) y, reactivada A, su *refresh token* vuelve a valer; y un operador dado de baja lo pierde todo a la vez (panel, sesiones nuevas y el desafío que tenía a medias). Dos peculiaridades de fixture: la firma de los tokens de plataforma sigue el reloj real y el segundo factor, el controlado (si no, el token nacería en el futuro), y se pidió el alta con el mismo servicio que usa el programa del servidor.
- **Mutaciones del bloque 7.** 12 sobre el código compartido, cada una probada **solo** contra esta clase para ver qué detecta por sí sola: el bloqueo por cuenta ignorado o con umbral 500, el pipeline sin revalidar al operador, la pertenencia, el login, el refresh y la invitación que ignoran la suspensión, el canje que no vuelve a comprobar al operador, la sesión sin auditar o sin evento, el alta sin auditar y la baja sin evento. Las 12 detectadas.
- **La verificación contra la API publicada** (no es un test del repositorio: un script, como el del segundo factor). Arranca la API de verdad (Kestrel), con un SQL Server desechable en Docker y un TOTP calculado **fuera** de la aplicación (`openssl`), da de alta al operador con el mismo servicio que `OfiFlow.Admin` (ese programa exige una terminal interactiva) y recorre el ciclo: 77 comprobaciones, entre ellas que la API **no arranca** sin `Jwt:PlatformSecret` y que su mensaje nombra la clave sin reproducir ninguna, los tres 401 de login con el **mismo cuerpo**, que el cuerpo de la lista no contiene ni el email, ni el nombre, ni el cliente, ni el teléfono de la empresa, la suspensión con el 403 `tenant.suspended` del miembro, la auditoría **consultada en la base de datos** (cuatro filas del operador, una por suspensión) y que el registro de la API no contiene contraseñas, emails ni claves. En el primer intento fallaban siete comprobaciones, **todas del propio script** (Git Bash reescribía la ruta de `sqlcmd` dentro de `docker exec`), no de la API.
- **Mutaciones.** 24 en el bloque 6 (la lista con una columna de más, el `pageSize` y la página sin tope, suspender o reactivar sin auditoría, la auditoría confirmada aparte, el recuento que respeta el filtro de empresa, la entrada de la lista blanca quitada, el orden sin desempate o ascendente, el filtro de suspendidas al revés, suspender sin buscar la empresa pedida, la empresa sin versión de fila, el conflicto de concurrencia ignorado, sin evento `1604`, las rutas sin límite o con cualquier esquema, sin `[RequiresPlatformOperator]`, un comando de empresa con `TenantId`, sin identidad del operador, reactivar que audita como suspender y el total que ignora el filtro). Todas detectadas; **una (N9, el desempate por Id) sobrevivió la primera vez** porque en memoria y en SQL Server los empates salen en el mismo orden por casualidad: se añadió el test del `ORDER BY` y se repitió. El script se guardó la copia de seguridad entera y se comprobó al final que `src` y `tests` quedaron idénticos. Antes: 28 en el bloque 5 (la misma clave o audiencia para los dos mundos, la política sin esquema o sin el claim, la revalidación omitida, cualquier valor del claim como plataforma, el desafío que cruza de mundo en el login o en el canje, el login sin exigir ser operador o segundo factor, el canje sin volver a comprobar ni auditar, 60 minutos en vez de 15, el token sin claim, con la clave o la audiencia de empresa, el login sin límite de peticiones, las respuestas cacheables, cada comprobación de arranque, un segundo sitio que emite sesiones y las rutas sin mapear), todas detectadas; una (la 27) hubo que repetirla porque el script usaba `/` como delimitador del texto que contenía `//`. Antes: 17 en el bloque 2 y 15 en el 3 (alta sin comprobar la pertenencia, la baja que no mira si sigue activo, la auditoría que deja de ser de solo inserción, la audiencia ignorada, lecturas colgadas de otro fichero, una entidad sin clasificar, el modelo cambiado sin migración y la clave primaria quitada de la migración). Todas detectadas. Una salió «no cambió nada» la primera vez por un fallo del propio script de mutaciones, no del código: por eso el script comprueba siempre que la mutación cambió el fichero.

Y los de la **spec 012** (`GET /account/me`, «quién soy»), una funcionalidad pequeña pero que habla de la seguridad de cada persona, así que con el reparto de siempre:

- **Domain** (`PermissionNamesTests` y la matriz de `RolePermissionsTests`): la celda nueva de la matriz aprobada (los cinco roles tienen `Account.Read`), los **doce nombres exactos** de los permisos escritos a mano y aparte del código (son un contrato con el frontend: si `Customers.Read` pasara a llamarse otra cosa, la interfaz dejaría de reconocerlo sin que nada fallara), que cada nombre tenga el formato `Entidad.Acción` y no se repita, que un valor fuera del enum **no tenga nombre** (lanza, no lo inventa) y que los permisos de un rol salgan ordenados y coincidan siempre con la tabla única.
- **Application** (`GetCurrentAccountQueryHandlerTests`, con dobles): los datos de quien pregunta, **el rol efectivo y no el del token** (el doble lleva un rol distinto en cada sitio), los permisos de cada uno de los cinco roles, que con varias personas y empresas sea siempre quien pregunta, que una pertenencia de otra empresa no devuelva nada (la consulta repite el Id de empresa a propósito, porque esta base en memoria no tiene el filtro), la denegación sin identidad, sin empresa o sin rol verificado, que la forma de la respuesta sean **exactamente ocho campos** (nada sensible se cuela por descuido) y que la consulta **no tenga parámetros**.
- **Infrastructure** (`GetCurrentAccountTests`, SQL Server real): la consulta cruza tablas con filtro de empresa (pertenencias) y sin él (personas y empresas), y esa mezcla es lo que un doble no puede probar. Dos empresas y un equipo; una pertenencia de otra empresa es **invisible aunque el token la reclame** (el filtro de la base de datos actúa solo); y que sea **una sola sentencia**, medida con un interceptor, no una por tabla.
- **Api** (`AccountMeEndToEndTests`, la API entera): los cinco roles con sus permisos exactos; **el mismo token después de un cambio de rol** (el token sigue diciendo `Technician`, `/me` ya dice `Manager` y `Jobs.Assign`); un técnico —que no puede listar el equipo— averigua cuál es su `membershipId` y coincide con el `assignedTenantUserId` de un trabajo que se le asignó; dos empresas; un `userId`, `tenantId` o email en la consulta **se ignora**; sin token, con basura o con una sesión de **plataforma** (`401`); empresa suspendida (`403 tenant.suspended`, sin sus datos); miembro quitado con el token aún válido; y que la respuesta no se cachea, tiene los ocho campos y no contiene ni `password`, `hash`, `secret`, `recovery`, `token` ni `cipher` **con el segundo factor activado** (hay secretos en la base de datos: ninguno debe salir).
- **Mutaciones.** 15 sobre el código real, todas detectadas: el nombre del permiso sin punto o sin ordenar, el técnico sin `Account.Read`, el rol del token en lugar del de la base de datos, todos los permisos en lugar de los del rol, la consulta sin filtrar por la persona o por la empresa del token, la consulta saltándose el filtro de empresa (la caza Infrastructure, no los dobles), sin denegar cuando no hay pertenencia, la operación sin su declaración de permiso, un campo de más en la respuesta, la respuesta cacheable, la ruta sin exigir sesión o sin mapear y el handler quitado de la lista blanca de lecturas de `Users`. Cada capa caza lo que le toca; las del filtro de empresa y la forma HTTP no las puede cazar nadie más.
- **Verificación contra la API publicada** (script, no test del repositorio): Kestrel real y una base de datos real, 26 comprobaciones entre las que está el desfase del token a la vista (el rol del token sigue siendo `Technician` y `/me` dice `Manager` tras el cambio), el `membershipId` de un técnico igual al de su trabajo asignado, la empresa B sin ver nada de la A, los parámetros ajenos ignorados, `Cache-Control: no-store`, ningún secreto en el cuerpo y el `403` de un miembro quitado.

## Parte 4 — Api: tres clases de test en un mismo proyecto

`OfiFlow.Api.Tests` arranca la **API entera en memoria** (con `WebApplicationFactory`) y le hace peticiones HTTP reales. Dentro hay tres familias:

**1. Contrato HTTP, sin base de datos** (`OfiFlowApiFactory`). Comprueban cabeceras de seguridad, formato de errores, límite de peticiones, idioma o la pantalla de Swagger (`SwaggerUiTests`: solo existe en `Development`, su CSP relajada no se escapa a otras rutas y en `Production` ni existe). No necesitan SQL Server porque no llegan a la base de datos.

**Y el CORS** (`CorsTests`, ADR-017), en esta misma familia pero **arrancando en Production** para que no entre el origen de desarrollo: hacen lo que hace un navegador, con la petición previa `OPTIONS` y la real con `Origin`. Comprueban que el origen permitido puede leer y uno ajeno no, que nunca se pide `AllowCredentials`, que un verbo o una cabecera que la API no usa no se autorizan, que un `429` y un `500` conservan las cabeceras CORS (el manejador de excepciones borra las de la respuesta), y que un origen mal escrito (`*`, con barra final, `http` fuera de `localhost`, con usuario en la URL) **impide arrancar la API**, con un control positivo para los válidos. Su primera ejecución cazó un hueco real (el origen con usuario se aceptaba) y dos suposiciones erróneas sobre cómo responde ASP.NET (la cabecera `Vary` y qué devuelve una petición previa no permitida): cuando el test falla, a veces el que estaba equivocado es el test, y hay que comprobar cuál antes de tocar.

**2. Extremo a extremo, con SQL Server real** (`SqlServerApiFactory` con `TwoTenantsFixture` o `RolesFixture`). Registran empresas y usuarios **por HTTP**, hacen login de verdad y comprueban el comportamiento completo: aislamiento entre dos empresas (`TenantIsolationEndToEndTests`) y la matriz de permisos con un usuario de cada rol (`RbacEndToEndTests`), incluido lo que ocurre con un token todavía válido cuando se quita o se degrada al miembro en la base de datos (se deniega o cambia de poderes en la siguiente petición). Dos cosas curiosas:

- El **registro** admite 3 por hora y el **login** 5 por minuto (los límites de seguridad). Los fixtures lo respetan a propósito: `RolesFixture` hace **exactamente 5 logins** (el del Owner y uno por cada rol que nace por invitación) y fabrica el resto de tokens con el secreto de la factoría. Si alguien añadiera un sexto login, los tests fallarían por el límite.
- La factoría comprueba que la API usa el **servidor del contenedor** y no tu base de datos local: sin esa salvaguarda, un error de configuración haría que los tests fueran contra tus datos de desarrollo sin avisar.

**Y las rutas de invitaciones y miembros** (`UserManagementEndpointsTests`, con su propia `MembersFixture`): comprueban por HTTP que cada ruta exige sesión (salvo aceptar), qué estado devuelve, que quien no puede recibe un 403 **sin que nada cambie**, que la jerarquía se aplica, que un miembro quitado deja de poder usar su token, que la respuesta con el token lleva `Cache-Control: no-store` y que un token ya usado y uno inventado reciben exactamente la misma respuesta. Necesitan una fixture propia porque la de roles gasta los 5 logins por minuto que permite la API: los miembros de esta se siembran en la base de datos y llevan un token firmado (el rol del token no autoriza nada).

**Y los criterios de aceptación de la spec 009** (`UserManagementAcceptanceTests`), uno por garantía y siempre con su control positivo:

- **Un token inventado, caducado, revocado o ya usado reciben la respuesta idéntica** (estado, título, texto y código), y no se crea nada.
- **Dos aceptaciones simultáneas del mismo token** (dos peticiones HTTP a la vez): exactamente una crea la cuenta y la otra recibe un 400, no un 500.
- **El token en claro no está en la base de datos ni en ningún log**: se concatenan todas las columnas de la invitación y se buscan, y se revisan todos los registros de la API. Y los eventos de seguridad llevan Ids, pero ni el email ni el nombre.
- **Un miembro quitado** no puede renovar la sesión, ni entrar, ni usar su token aún vivo; su email queda libre y se le puede volver a invitar; y sus trabajos sin terminar quedan sin asignar mientras los terminados conservan la referencia.
- **El aislamiento entre empresas:** otra empresa no ve a mis miembros ni mis invitaciones, y tocarlos da el **mismo** 404 que un Id inexistente.

Un detalle de preparación: el límite de aceptar es de 10 por hora y por IP, y todos los clientes de prueba comparten una IP. En vez de relajar el límite de producción, la factoría de pruebas lleva un filtro **solo de tests** (`TestClientIpStartupFilter`) que deja a cada petición declarar su IP con una cabecera. La API real no lo tiene, así que ignora esa cabecera. El límite de verdad se prueba aparte, sin el filtro.

**3. Arquitectura: tests que leen el código.** No prueban una funcionalidad, sino que **la estructura del código respeta una regla**: nada de SQL a mano, toda operación declara su permiso, toda entidad está clasificada... (capítulo 8). Funcionan escaneando los ficheros fuente o inspeccionando los tipos por reflexión.

---

## Parte 5 — Cómo hacer fiable un test de seguridad

Aquí está lo que más distingue estos tests de unos corrientes. Un test de seguridad tiene un peligro especial: **puede pasar sin comprobar nada**. Si el test dice "esta persona no puede ver aquello" y la preparación estaba mal, la persona "no ve nada" por el motivo equivocado, y el test pasa en verde con el control roto. Estas son las técnicas para evitarlo.

### 1. Controles positivos

Junto a cada "este rol **no puede**", va un "este rol **sí puede**, y la operación tuvo efecto". Si el test de "no puede" pasa porque la API está rota, el de "sí puede" falla y lo delata. Por eso cada caso de la matriz de permisos comprueba, además del 403, que el dato **no cambió**, y cuando se permite, que **sí cambió**.

### 2. Meta-tests: probar al guardián

Los tests de arquitectura aplican una regla al código real. Pero ¿cómo sabes que la regla detecta de verdad una infracción? Con **meta-tests**: la regla se escribe como una función pura (recibe ficheros o tipos, devuelve incumplimientos) y se prueba con **entradas inventadas, mal hechas a propósito**: un fichero que lee la tabla prohibida, una operación sin declarar, una entidad sin clasificar. Si la regla no los detecta, el meta-test falla.

### 3. Centinelas: que no pase en vacío

Un escaneo que no encuentra nada también "pasa". Para evitarlo, algunos tests fijan **lo que deberían encontrar**: "las entidades de empresa son exactamente `Customer`, `Invitation`, `Job` y `TenantUser`", "se encuentran al menos las 23 operaciones". Si el escaneo se rompiera y no viera nada, el centinela salta.

### 4. Pruebas de mutación: romper a propósito

La técnica más contundente, y la que más seguridad da. Cada guardarraíl se verifica así:

1. **Rompes a propósito el código real** (desactivas el filtro por empresa, quitas la comprobación de la firma, cambias un permiso).
2. **Ejecutas los tests** y compruebas que **se ponen en rojo**.
3. **Restauras** el código.

Con el filtro por empresa desactivado, 20 de los 22 tests de extremo a extremo fallan; con el filtro saltado solo en un handler, falla **únicamente** el test de esa ruta. Eso demuestra a la vez que el test vigila lo que dice y que **señala el sitio concreto**. Es la mejor respuesta a "¿cómo sabes que tus tests de seguridad funcionan?". Su nombre técnico es *mutation testing* (pruebas de mutación).

### 5. Más hábitos

- **Datos aleatorios y sin secretos.** Los usuarios de prueba y sus contraseñas se generan en cada ejecución, sin credenciales fijas. Un día se nos coló una contraseña escrita a mano en un test y el escáner de secretos la marcó.
- **Los contratos se fijan con literales.** Los códigos de error (`"job.cannot_complete"`) se escriben tal cual en los tests, porque son contrato público: si alguien cambia el valor de la constante, el test falla en vez de acompañar el cambio.
- **Copias independientes.** La matriz de permisos está escrita **tres veces** (la tabla de producción, el test de Domain y el test de extremo a extremo), sin que una se derive de otra. Tienen que coincidir.

---

## Cuántos tests escribir: proporcionales al riesgo

La batería de este proyecto es grande (más de 600 casos) y un lector atento se preguntará si **toda** funcionalidad necesita tantos. La respuesta es **no**: la cantidad de tests de este proyecto responde al **riesgo** de lo que protegen, no a una regla fija. Esta sección deja por escrito el criterio para no sobreprobar sin querer (ni quedarse corto donde importa).

### Un caso no es un método

Antes de asustarse con la cifra: **1580 casos son unos 980 métodos**. Un mismo método con `[Theory]` se ejecuta una vez por cada dato. La matriz de permisos son 60 celdas (12 permisos por 5 roles) y la de jerarquía de roles 25, y cada celda es un caso a propósito: si alguien cambia una, debe fallar *esa* celda y no "algo".

### Tres niveles de riesgo

| Nivel | Qué es | Qué tests lleva |
|---|---|---|
| **Alto** | Seguridad, dinero y datos personales: autorización, aislamiento entre empresas, tokens, invitaciones, y más adelante facturas y cobros | La batería completa de este capítulo: reglas de Domain, casos de uso con dobles, **SQL real** para lo que una base en memoria no ve, controles positivos, **mutaciones sobre el código real** y que nada sensible aparezca en los logs |
| **Medio** | Reglas de negocio sin consecuencias de seguridad: las transiciones de estado de un trabajo, validaciones de campos | Domain y Application: camino feliz, cada regla que puede fallar y sus errores. Integración solo si la regla depende de la base de datos |
| **Bajo** | Código corriente: otra pantalla de listado, un campo nuevo de texto, un CRUD más | **Lo mínimo**: el camino feliz, los errores de validación y **el aislamiento entre empresas** (que sigue siendo obligatorio aunque el resto sea poco). Sin mutaciones ni familias de tests propias |

Una pregunta rápida para decidir el nivel: *si esto falla en silencio, ¿alguien ve datos que no debe, pierde dinero o puede hacer algo que no le toca?* Si la respuesta es sí, es nivel alto.

### Qué NO hacer

- **No probar el framework.** Que EF Core guarda una fila o que ASP.NET devuelve un 404 ya lo prueban sus autores. Se prueba **nuestra** regla.
- **No añadir una tercera copia de una tabla** salvo que cubra un hueco que las otras no cubren. La matriz de permisos ya está escrita aparte en tres sitios (Domain, Application y extremo a extremo) y es el punto de mayor duplicación del proyecto: se acepta porque es una tabla de seguridad, no como patrón general.
- **No comprobar el texto exacto de un log.** Lo que importa es lo que **no** puede aparecer (un email, un token, una contraseña): se comprueba la ausencia de ese dato, no la frase completa. Así cambiar la redacción del mensaje no rompe el test sin que nada esté roto.
- **No dejar un test que nunca ha fallado sin comprobarlo** si es de nivel alto: se rompe el código a propósito y se ve el rojo (apartado anterior).

### El coste, sin maquillar

Más tests no son más calidad por sí solos. Tienen un precio: la suite tarda alrededor de un minuto y **necesita Docker** (ya nos falló por no estar arrancado); un cambio de diseño obliga a tocar varios tests; y un test mal pensado da **falsa confianza**, que es peor que no tenerlo. Por eso el criterio es proporcional: mucho donde el fallo sería grave, poco donde no lo sería, y nunca tests por rellenar.

### Para una funcionalidad nueva

1. **Decide el nivel** con la pregunta de arriba, y anótalo en el `tasks.md` de la spec.
2. Si es **alto**, escribe la batería completa y verifica cada guardarraíl con una mutación.
3. Si es **bajo**, escribe lo mínimo y comprueba que no rompe el aislamiento entre empresas.
4. Al terminar, revisa si algún test **duplica** lo que ya cubre otro sin aportar nada, y quítalo.

## Cómo ejecutarlos

```bash
dotnet test                                         # todos (necesita Docker arrancado)
dotnet test tests/OfiFlow.Domain.Tests              # solo un proyecto
dotnet test --filter "FullyQualifiedName~Rbac"      # solo los tests cuyo nombre contiene "Rbac"
```

Sin Docker, los proyectos `Domain` y `Application` funcionan siempre; en `Infrastructure` y `Api` fallan solo las clases que necesitan SQL Server.

En el **pipeline**, el job `build-and-test` ejecuta `dotnet test` sobre toda la solución en un runner de Ubuntu, que ya trae Docker. Tarda alrededor de **un minuto y cuarto**, con las dos suites de SQL Server incluidas. Es uno de los cuatro comprobantes obligatorios para poder fusionar cualquier cambio.

## Errores reales que cazamos (y por qué importan)

Esta lista es la mejor lección del capítulo, porque son fallos que **nos pasaron de verdad**:

| Qué pasó | Qué enseña |
|---|---|
| Un test de seguridad pasaba a la primera | Un test que nunca ha fallado no demuestra nada: hay que romper el código y verlo en rojo |
| Mi primer test del claim del rol validaba con un manejador de JWT suelto y daba por bueno algo que fallaba en la API real | Probar con la **configuración real de producción**, no con una réplica simplificada |
| Una salvaguarda dio falsa alarma comparando dos cadenas de conexión que SQL Server escribe distinto | Comparar el significado (servidor y puerto), no el texto |
| El `Owner` recibía un 403 en todo por un renombrado automático de claims | El test de extremo a extremo con un token **real** cazó lo que ningún test unitario veía |
| Una mutación mal formada rompía también los tokens válidos | Un test "en rojo" por la razón equivocada tampoco demuestra nada |
| Una contraseña escrita a mano en un test saltó en el escáner de secretos | Nada de credenciales fijas, ni siquiera de prueba |
| Una restauración con `git checkout` borró cambios aún sin commitear | Restaurar tras una mutación con copias de seguridad, o commitear antes |
| Dos aceptaciones simultáneas de la misma invitación: la perdedora acababa en un **500** y no en el error esperado | Los tests con **SQL Server real** ven lo que una base en memoria no puede (índices únicos, versiones de fila). Los de en memoria pasaban en verde |
| Una mutación **sobrevivió**: el test de "email duplicado sin distinguir mayúsculas" usaba un segundo email ya en mayúsculas, y coincidía por casualidad | Un test puede pasar por una coincidencia. Una mutación que no se detecta es una pista de que el test es débil: se reforzó con varias variantes de mayúsculas |
| El límite de aceptar invitaciones (10 por hora y por IP) cortaba mis propios tests de extremo a extremo, que hacen muchas aceptaciones desde la misma IP de prueba | No se relaja el límite de producción para que pasen los tests: se separa la IP de cada petición de prueba con un filtro solo de tests |
| Una mutación (quitar el borrado de las sesiones) **no** la detectaba el grupo de tests HTTP | No siempre falla el mismo grupo: aquí el HTTP seguía protegido por otra defensa independiente (el refresh comprueba la pertenencia), y la detectaron los tests de SQL real y los de Application. Conviene saber qué grupo vigila cada cosa |
| Seis tests HTTP fallaban juntos y pasaban por separado: la matriz creaba una invitación por caso y **agotaba el máximo de 25 pendientes por empresa** | Que un test pase solo y falle en grupo apunta a **estado compartido**. Aquí el límite funcionaba y el que gastaba de más era el test: la matriz solo crea la invitación que necesita |
| Mis cuatro tests de extremo a extremo de la revalidación fallaron a la primera: la ayuda de test consultaba con el filtro de empresa sin que hubiera empresa activa, no la revalidación | Antes de culpar al código, confirmar la causa con el mensaje de error: era un fallo de la **preparación** del test, y se corrigió ahí |
| Un test **intermitente** (pasaba y fallaba con el mismo código) destapó que `Base64Url.IsValid` de .NET ignora los espacios en blanco, y mi comprobación del formato del token los aceptaba según el azar del token | Un test intermitente casi nunca es mala suerte: es el azar mostrando un defecto. No se repite hasta que pase: se investiga, y el arreglo se prueba con **muchas muestras**, no con una |

## Dónde están hoy los límites

**1. No se mide la cobertura.** `coverlet` (la herramienta que la mide) está instalado en los cuatro proyectos, pero **ningún workflow la ejecuta** ni se publica ningún informe. No sabemos qué porcentaje del código recorren los tests. Los guardarraíles de seguridad se verifican por mutación, que es más exigente que la cobertura, pero la cobertura daría una visión de conjunto.

**2. Las pruebas de mutación se hacen a mano.** No hay una herramienta que las automatice (existe una para .NET, *Stryker.NET*) ni nada que las repita en el pipeline. Funcionan, pero dependen de que alguien las haga al escribir cada guardarraíl.

**3. Cuatro controles no tienen test propio** (capítulos 6 y 8): que la aplicación no arranque sin el secreto, que el token caduque, que el hash no sea la contraseña en claro y que se envíe HSTS.

**4. No hay tests de rendimiento ni de carga.** Nadie ha medido cuántas peticiones por segundo aguanta.

**5. Los tests de extremo a extremo son frágiles ante los límites de peticiones.** Un sexto login en un mismo fixture los rompería. Está documentado en el propio código, pero es un acoplamiento a recordar.

**6. Dependen de Docker en local.** Si Docker no arranca, las clases con SQL Server no se pueden ejecutar en tu máquina (en el pipeline sí).

**7. La imagen de SQL Server está escrita en dos ficheros** (con un comentario cruzado), y Dependabot no la vigila: si hay que cambiar de versión, hay que acordarse de las dos.

**8. No hay tests del frontend** (todavía no existe).

**9. Un fallo intermitente, ya explicado y corregido (a medias).** `TwoSimultaneousAcceptancesOfTheSameToken_CreateExactlyOneAccount` falló una vez en el CI de la spec 011 (en la versión por HTTP, que hasta entonces «no había fallado nunca»): la perdedora recibía `user.email_already_registered` en vez de `invitation.invalid`. **No era un test frágil sino una ventana real** del handler de aceptar invitaciones (capítulo 6, «una tercera vuelta de la misma carrera»), ya corregida y fijada con un test determinista (un doble que consume la invitación justo antes de crear la cuenta; falla sin la corrección). Lo que sigue **sin explicar** es el fallo, una sola vez, de la versión de Infrastructure de ese mismo escenario: probablemente la misma ventana, pero no se guardó la salida y es una hipótesis. Además, al lanzar la suite completa de golpe fallaron una vez 25 tests de `MfaEndToEndTests` porque un contenedor de SQL Server salió con código 191 al arrancar (varios SQL Server a la vez en un Docker con unos 12 GB): es contención de recursos, no del código, y en local la suite completa va mejor **proyecto a proyecto**.

## Para comprobar que lo has entendido

1. ¿Qué significa AAA y cómo lo ves en un test de `Domain`?
2. ¿Por qué hay cuatro proyectos de test y no uno?
3. ¿Por qué los tests de Application no demuestran que una empresa no ve los datos de otra?
4. ¿Por qué se usa un SQL Server real en Docker y no una base en memoria para el aislamiento entre empresas?
5. ¿Qué es un control positivo y por qué un test de "esto no se puede" lo necesita?
6. Explica con tus palabras qué es una prueba de mutación y qué demuestra.
7. ¿Qué es un meta-test y qué problema resuelve?
8. Nombra dos límites de la batería de tests y di cuál cerrarías primero.
9. Vas a añadir una pantalla de listado de proveedores. ¿Qué nivel de riesgo tiene y qué tests escribirías? ¿Y si fuera la emisión de facturas?

*(Respuestas: 1 = *Arrange, Act, Assert*: preparar, actuar y comprobar; en `Start_FromNew_...` se crea el trabajo, se llama a `Start()` y se comprueba que el estado es `InProgress`. 2 = porque cada capa se prueba con lo que le corresponde: reglas puras sin nada, casos de uso con dobles, persistencia con base real y HTTP con la API entera; cuanto más hacia fuera, más lento y realista. 3 = porque usan una base en memoria sin el filtro por empresa; si un handler olvidara filtrar, no lo notarían; eso lo prueba la capa de Infrastructure con la base real. 4 = porque la base en memoria no ejecuta SQL real y un filtro podría traducirse mal sin que el test lo notara; el aislamiento es lo más importante del proyecto. 5 = junto a "este rol no puede" va "este rol sí puede y la operación tuvo efecto"; sin él, el test de "no puede" podría pasar porque la preparación estaba mal y no por el control. 6 = romper a propósito el código real y comprobar que el test se pone en rojo; demuestra que el test vigila lo que dice y señala el sitio concreto. 7 = una prueba de la regla que vigila el código, con entradas inventadas mal hechas, para demostrar que la regla detecta de verdad una infracción. 8 = por ejemplo, no se mide la cobertura y las mutaciones se hacen a mano; yo cerraría primero los cuatro controles sin test propio, porque son baratos y son seguridad. 9 = el listado es de riesgo bajo: el camino feliz, los errores de validación y el aislamiento entre empresas, sin mutaciones ni familias propias; la emisión de facturas es de riesgo alto (dinero y datos personales): batería completa, SQL real, controles positivos y mutaciones sobre el código real.)*

## Siguiente capítulo

El capítulo 10 explica el **pipeline de CI/CD**: qué ejecuta GitHub en cada cambio, los cuatro comprobantes obligatorios, los escáneres de seguridad, Dependabot y por qué `master` está protegida.
