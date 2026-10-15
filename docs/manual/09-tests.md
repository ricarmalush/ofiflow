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
| `OfiFlow.Domain.Tests` | Las reglas del negocio | Nada: C# puro | 194 | ~0,1 s | No |
| `OfiFlow.Application.Tests` | Los casos de uso, los validadores y el pipeline | Base en memoria y dobles de prueba | 245 | ~1 s | No |
| `OfiFlow.Infrastructure.Tests` | Persistencia, tokens y aislamiento entre empresas | **SQL Server real** en Docker (y base en memoria para lo ligero) | 96 | ~12 s | **Sí**, en parte |
| `OfiFlow.Api.Tests` | HTTP, seguridad, arquitectura y extremo a extremo | La API entera en memoria; con SQL Server real en los de extremo a extremo | 235 | ~15 s | **Sí**, en parte |

En total, **770 casos**. Un "caso" no es lo mismo que un método de test: hay unos 406 métodos, pero un mismo método con `[Theory]` se ejecuta varias veces con datos distintos (la matriz de permisos es un solo método que se ejecuta 65 veces).

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

Las reglas del negocio (que un trabajo no se pueda completar sin empezarlo, que un email tenga formato válido) se prueban sin nada alrededor: ni base de datos, ni servidor. Por eso son los más rápidos (una décima de segundo para casi 200 casos) y los más fáciles de escribir. Aquí también viven las dos **tablas de seguridad**: la matriz de permisos, con una celda "rol × permiso" por caso (50 celdas, `RolePermissionsTests`), y la de jerarquía de roles, con una celda "quién actúa × a quién gestiona" por caso (25 celdas, `ManageableRolesTests`). Y las reglas de las invitaciones (`InvitationTests`): que caduquen, que se usen una sola vez y que el rol `Owner` no se pueda conceder.

## Parte 2 — Application: casos de uso con dobles de prueba

Un *handler* necesita una base de datos y otras piezas (quién es el usuario, cómo se emiten tokens). En los tests se sustituyen por **dobles de prueba** (*test doubles*): versiones simples y controlables.

| Doble | Qué sustituye | Fichero |
|---|---|---|
| `TestDbContext` | La base de datos, por una **en memoria** (`TestDbContextFactory.Create()` da una nueva y vacía en cada test) | `Common/TestDbContext.cs` |
| `FakeTenantContext` | "¿En qué empresa estoy?" | `Common/FakeTenantContext.cs` |
| `FakeCurrentUser` | "¿Quién soy y qué rol tengo?" Distingue el rol **efectivo** (el de la base de datos) del que diría el token, para probar que decide el primero, y cuenta cuántas veces se verifica la pertenencia | `Common/FakeCurrentUser.cs` |
| `FakeTimeProvider` | "¿Qué hora es?" (para probar que una invitación caduca a los 7 días **sin esperar 7 días**: el test avanza el reloj) | `Common/FakeTimeProvider.cs` |
| `FakeIdentityService`, `FakeTokenService` | Las contraseñas y los tokens | `Common/` |

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

## Parte 4 — Api: tres clases de test en un mismo proyecto

`OfiFlow.Api.Tests` arranca la **API entera en memoria** (con `WebApplicationFactory`) y le hace peticiones HTTP reales. Dentro hay tres familias:

**1. Contrato HTTP, sin base de datos** (`OfiFlowApiFactory`). Comprueban cabeceras de seguridad, formato de errores, límite de peticiones, idioma o la pantalla de Swagger (`SwaggerUiTests`: solo existe en `Development`, su CSP relajada no se escapa a otras rutas y en `Production` ni existe). No necesitan SQL Server porque no llegan a la base de datos.

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

Un escaneo que no encuentra nada también "pasa". Para evitarlo, algunos tests fijan **lo que deberían encontrar**: "las entidades de empresa son exactamente `Customer`, `Job` y `TenantUser`", "se encuentran al menos las 16 operaciones". Si el escaneo se rompiera y no viera nada, el centinela salta.

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

Antes de asustarse con la cifra: **770 casos son unos 406 métodos**. Un mismo método con `[Theory]` se ejecuta una vez por cada dato. La matriz de permisos son 50 celdas y la de jerarquía de roles 25, y cada celda es un caso a propósito: si alguien cambia una, debe fallar *esa* celda y no "algo".

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
