# Capítulo 5 — Multi-tenancy: aislar a cada cliente

## Qué vas a aprender

- Qué amenaza concreta evita el aislamiento entre empresas y por qué es el riesgo más grave del proyecto.
- Las cuatro piezas que lo forman y cómo encajan.
- Cuándo (y solo cuándo) se permite saltárselo.
- Cómo sabemos que funciona de verdad: los tests.
- Dónde están hoy sus puntos débiles.

Este capítulo desarrolla el paso 11 del capítulo 4. Si no lo has leído, léelo antes.

## El problema

Recuerda la imagen del edificio de apartamentos: una sola aplicación y una sola base de datos, donde cada empresa (*tenant*) tiene su "piso". En la base de datos, los clientes de la fontanera Marta y los de la electricista Elena están **en la misma tabla `Customers`**, mezclados fila a fila. Lo único que los distingue es una columna: `TenantId`.

Eso significa que el aislamiento **no lo da la infraestructura**, lo da el código. Y el error posible es muy simple:

```csharp
// ❌ Un handler que olvida filtrar por empresa
var customers = await dbContext.Customers.ToListAsync();   // devuelve los de TODAS las empresas
```

Una sola consulta así, escrita por descuido en cualquier handler, y Marta vería los clientes de Elena: nombres, teléfonos, direcciones.

### Qué amenaza es

Esto tiene nombre: **BOLA** (*Broken Object Level Authorization*, puesto 1 del OWASP API Security Top 10), que en aplicaciones web clásicas se llama **IDOR** (*Insecure Direct Object Reference*). Es acceder a un objeto que no te pertenece cambiando o adivinando su identificador. En términos de CyberOps:

- Es un fallo de **control de acceso** (en el modelo AAA, la parte de *autorización*).
- Rompe la **confidencialidad** de la tríada CIA.
- En STRIDE es *Information disclosure* (revelación de información).

Y tiene consecuencias legales, porque los datos de clientes son datos personales (RGPD). Por eso el ADR-002 lo califica como "el riesgo más peligroso del proyecto".

## La idea de la solución

> **El aislamiento no puede depender de que cada programador se acuerde de filtrar.** Tiene que ser automático, y lo que no sea automático tiene que romper un test.

Hay cuatro piezas. Cada una resuelve una parte.

```
 ① ETIQUETA            ② EL TENANT VIAJA        ③ FILTRO AUTOMÁTICO       ④ LA FIJA EL SERVIDOR
 ITenantOwned          EN EL TOKEN              en toda consulta          al crear datos
 "esta entidad es      "esta sesión es de       "WHERE TenantId = ..."    "el TenantId nuevo sale
  de una empresa"       la empresa X"            añadido por EF Core       del token, no del cliente"
```

---

## Pieza ① — La etiqueta `ITenantOwned`

[`ITenantOwned`](../../src/OfiFlow.Domain/Common/ITenantOwned.cs) es una interfaz con una sola propiedad:

```csharp
public interface ITenantOwned
{
    Guid TenantId { get; }
}
```

Es una **marca**: "esta entidad pertenece a una empresa". Las tres entidades que la llevan hoy:

| Entidad | ¿`ITenantOwned`? | Por qué |
|---|---|---|
| `Customer` | ✅ | Los clientes son de una empresa |
| `Job` | ✅ | Los trabajos son de una empresa |
| `TenantUser` | ✅ | La pertenencia de una persona a **esa** empresa (con su rol) |
| `Tenant` | ❌ | Es la propia empresa; no pertenece a ninguna |
| `User` | ❌ | Una persona puede estar en varias empresas, así que no es de ninguna en concreto |

La marca es lo que **activa** el filtro automático de la pieza ③.

## Pieza ② — El tenant viaja dentro del token

Cuando alguien inicia sesión, el servidor emite un token JWT **firmado** que contiene, entre otras cosas, la empresa en la que trabaja. Está en [`TokenService.cs:93-100`](../../src/OfiFlow.Infrastructure/Identity/TokenService.cs):

```csharp
Claim[] claims =
[
    new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),        // quién eres
    new Claim(OfiFlowClaimTypes.TenantId, tenantId.ToString()),       // en qué empresa
    new Claim(ClaimTypes.Role, role.ToString())                       // con qué rol
];
```

Está firmado con HMAC-SHA256 y el secreto del servidor. Si alguien cambia el `tenant_id` dentro del token, la firma deja de coincidir y el servidor lo rechaza en el paso 7 del capítulo 4.

En cada petición, [`TenantContext.cs:17-20`](../../src/OfiFlow.Infrastructure/Tenancy/TenantContext.cs) lee ese dato del usuario ya autenticado:

```csharp
var claim = httpContextAccessor.HttpContext?.User.FindFirst(OfiFlowClaimTypes.TenantId)
    ?? throw new InvalidOperationException("No hay un tenant activo en el contexto actual.");

return Guid.Parse(claim.Value);
```

La clave: **el tenant sale del token firmado, nunca de lo que envía el cliente**. No hay cabecera `X-Tenant-Id`, ni parámetro en la URL, ni campo en el JSON que lo determine. El ADR-002 comparó las dos opciones y descartó la cabecera porque obligaría a validar en cada llamada que el usuario pertenece realmente a ese tenant, "más superficie de error".

Cualquier clase de Application que necesite saber la empresa actual pide la interfaz [`ITenantContext`](../../src/OfiFlow.Application/Common/Abstractions/ITenantContext.cs) (definida en Application, implementada en Infrastructure: el patrón del capítulo 2).

Si no hay ningún tenant en el token, `TenantContext` lanza una excepción en vez de devolver un valor por defecto: **prefiere fallar a trabajar con la empresa equivocada**.

## Pieza ③ — El filtro global de consultas

Esta es la pieza central. En [`ApplicationDbContext.cs:44-64`](../../src/OfiFlow.Infrastructure/Persistence/ApplicationDbContext.cs):

```csharp
private void ApplyTenantQueryFilters(ModelBuilder modelBuilder)
{
    foreach (var entityType in modelBuilder.Model.GetEntityTypes())
    {
        if (typeof(ITenantOwned).IsAssignableFrom(entityType.ClrType))
        {
            SetTenantQueryFilterMethod.MakeGenericMethod(entityType.ClrType).Invoke(this, [modelBuilder]);
        }
    }
}

private void SetTenantQueryFilter<TEntity>(ModelBuilder modelBuilder) where TEntity : class, ITenantOwned
{
    modelBuilder.Entity<TEntity>().HasQueryFilter(entity => entity.TenantId == tenantContext.TenantId);
}
```

Qué hace, en cristiano:

1. Al construir el modelo, **recorre todas las entidades** del sistema (esto se llama *reflexión*).
2. A las que llevan la marca `ITenantOwned`, les añade un **filtro global de consultas** (*Global Query Filter*): una condición `TenantId == <empresa actual>` que EF Core agrega **automáticamente** a cada consulta sobre ellas.
3. Se escribe **una sola vez**, no entidad por entidad. Una entidad nueva con la marca queda protegida sin hacer nada más.

El efecto sobre una consulta que no menciona la empresa:

```csharp
// Lo que escribe el handler:
dbContext.Customers.ToListAsync()
```
```sql
-- Lo que se ejecuta (aproximadamente):
SELECT ... FROM Customers WHERE TenantId = @tenant
```

El valor se reevalúa **por cada petición**, porque cada una usa su propia instancia del `DbContext` con su propio `ITenantContext`.

El filtro protege lecturas, pero también todo lo que **empieza por una lectura**: cuando un handler hace `FirstOrDefaultAsync(j => j.Id == id)` para modificar o borrar, si el `Id` es de otra empresa no encuentra nada y responde 404. Así, Marta tampoco puede modificar ni borrar un trabajo de Elena aunque conozca su `Id`.

## Pieza ④ — El servidor fija el tenant al crear datos

El filtro cubre las lecturas. Falta lo opuesto: **al crear** un dato, ¿quién decide a qué empresa pertenece? Siempre el servidor:

```csharp
// CreateJobCommandHandler.cs:22
var job = Job.Create(tenantContext.TenantId, request.CustomerId, request.Title, ...);
```

Y para que sea imposible que el cliente lo decida, **ningún Command ni Query tiene una propiedad `TenantId`**. Aunque un atacante añada `"tenantId": "..."` al JSON, no hay dónde guardarlo y se descarta. Es la protección contra *mass assignment* (que el cliente rellene campos que no debería).

No depende de la memoria del autor: un test lo vigila (ver más abajo).

---

## La excepción: `IgnoreQueryFilters()`

A veces hay que saltarse el filtro **a propósito**. EF Core ofrece `IgnoreQueryFilters()`, que desactiva el filtro en una consulta concreta. Hoy se usa en **solo dos sitios**, ambos con motivo:

| Dónde | Por qué es necesario |
|---|---|
| [`LoginCommandHandler.cs:28-30`](../../src/OfiFlow.Application/Identity/Commands/Login/LoginCommandHandler.cs) | En el login **todavía no hay tenant activo**: es justo lo que el login determina. Busca la pertenencia del usuario a una empresa para saber qué poner en el token |
| [`TokenService.cs:72-74`](../../src/OfiFlow.Infrastructure/Identity/TokenService.cs) | Al renovar la sesión, relee el rol del usuario **en su empresa** (el rol puede haber cambiado) |

Es la forma **deliberada** de saltarse el filtro (más abajo verás otra, accidental, que hoy no está vigilada), y por eso esta sí lo está. El test [`IgnoreQueryFilters_IsOnlyUsedInTheAllowList`](../../tests/OfiFlow.Api.Tests/Architecture/SecurityArchitectureTests.cs) escanea el código fuente y falla si `IgnoreQueryFilters(` aparece en cualquier fichero que no esté en una **lista blanca** de esos dos. Para añadir un tercer uso hay que modificar la lista a propósito y justificarlo, es decir, tomar la decisión de forma consciente.

## De dónde sale el tenant la primera vez

Recorramos el ciclo completo, desde que Marta inicia sesión:

```
1. Marta envía email + contraseña                        (sin tenant: aún no existe sesión)
2. IdentityService comprueba la contraseña               → obtiene su UserId
3. LoginCommandHandler busca su TenantUser               (con IgnoreQueryFilters, ver arriba)
4. TokenService emite un token con sub, tenant_id, role  (firmado)
5. A partir de aquí, TODAS las peticiones llevan ese token
   y TenantContext lo lee → el filtro se aplica solo
```

Un detalle de **honestidad**: el ADR-002 habla de "tenant activo elegido tras el login". Hoy esa elección no existe de verdad: el login busca la primera pertenencia del usuario (`FirstOrDefaultAsync` sin orden) y usa esa. Como el único camino para tener cuenta es registrarse (lo que crea una empresa nueva y te hace su `Owner`), **hoy cada usuario pertenece a exactamente una empresa** y no hay ambigüedad. No existen todavía invitaciones, ni un selector de empresa, ni un endpoint para cambiar de empresa. Cuando existan, el login tendrá que dejar de elegir "la primera".

Al renovar la sesión (`refresh`), el servicio comprueba otra vez que el usuario **sigue perteneciendo** a esa empresa y relee su rol antes de emitir el token nuevo.

---

## Cómo sabemos que funciona: los tests

Un control de seguridad que no se prueba es solo una esperanza. Hay tres capas de prueba:

| Test | Qué demuestra | Contra qué |
|---|---|---|
| [`ApplicationDbContextTenantFilterTests`](../../tests/OfiFlow.Infrastructure.Tests/Persistence/ApplicationDbContextTenantFilterTests.cs) | El filtro por reflexión restringe por tenant | Base de datos en memoria (comprobación rápida) |
| [`TenantIsolationIntegrationTests`](../../tests/OfiFlow.Infrastructure.Tests/Persistence/TenantIsolationIntegrationTests.cs) | **Clientes y trabajos de una empresa no se ven desde otra** | **SQL Server real en Docker** |
| [`SecurityArchitectureTests`](../../tests/OfiFlow.Api.Tests/Architecture/SecurityArchitectureTests.cs) | Nadie usa `IgnoreQueryFilters` fuera de la lista blanca; ningún Command/Query acepta `TenantId` | Escaneo del código y reflexión |

### Cómo leer el test más importante

```csharp
// TenantIsolationIntegrationTests.cs:21-37
[Fact]
public async Task Customers_AreNotVisibleAcrossTenants()
{
    var tenantA = Guid.NewGuid();
    var tenantB = Guid.NewGuid();

    await using (var dbAsTenantA = _fixture.CreateDbContext(tenantA))
    {
        dbAsTenantA.Customers.Add(Customer.Create(tenantA, CustomerType.Person, "Cliente A", ...));
        await dbAsTenantA.SaveChangesAsync(CancellationToken.None);       // 1. A guarda un cliente
    }

    await using var dbAsTenantB = _fixture.CreateDbContext(tenantB);
    var visibleToTenantB = await dbAsTenantB.Customers.ToListAsync();     // 2. B pide TODOS los clientes

    Assert.Empty(visibleToTenantB);                                       // 3. B no ve ninguno
}
```

Fíjate en la consulta del paso 2: `Customers.ToListAsync()` **sin ninguna condición**. Si el filtro no funcionara, devolvería el cliente de A. Que devuelva una lista vacía demuestra que el filtro se aplicó solo.

### ¿Por qué SQL Server real y no una base de datos en memoria?

La base en memoria de EF Core **no se comporta igual que SQL Server**: no ejecuta SQL real. Un filtro que funcione en memoria podría traducirse mal a SQL. Como este es el test más importante del proyecto, se ejecuta contra SQL Server de verdad, en un contenedor Docker (Testcontainers) que se crea para el test y se destruye al terminar, con las migraciones reales. Se explica en el capítulo 9 y en el ADR-008.

---

## Dónde están hoy los puntos débiles

Un buen diseño conoce sus límites. Estos son los de este mecanismo, de más a menos importante:

**1. Una entidad nueva que olvide la marca no queda protegida, y ningún test lo detecta.**
El filtro se aplica solo a las entidades con `ITenantOwned`. Si alguien crea una entidad con su propia columna `TenantId` pero olvida añadir la interfaz, esa tabla **no tendrá filtro**, y nada fallará. El comentario del código dice que "ninguna entidad queda exenta por omisión", pero eso solo es cierto para las que llevan la marca. Una solución barata sería un test de arquitectura: "toda entidad con una propiedad `TenantId` debe implementar `ITenantOwned`" (con una excepción documentada para `RefreshToken`). No existe todavía.

**2. `Tenants` y `Users` no tienen filtro, y `IApplicationDbContext` los expone.**
Es lo correcto según el diseño (no pertenecen a una empresa), pero significa que un handler futuro que escribiera `dbContext.Users.ToListAsync()` vería a **todas las personas de todas las empresas**. Hoy ningún handler los lee (solo `Register` los escribe), pero la protección aquí depende de que quien escriba el siguiente handler tenga cuidado.

**3. Los tests cubren lecturas de `Customer` y `Job`, no todo.**
No hay test de aislamiento para `TenantUser`; ni de que un handler no pueda **modificar o borrar** un dato ajeno (funciona por la lectura previa, pero no está probado explícitamente); ni de extremo a extremo por HTTP con dos usuarios reales. El comentario de `ApplicationDbContextTenantFilterTests` sigue diciendo que el test contra SQL Server real está "pendiente", y ya existe: está desactualizado.

**4. La pertenencia al tenant no se vuelve a comprobar en cada petición.**
El ADR-002 prevé verificar en cada Command que el usuario pertenece al tenant del token. Hoy solo se verifica al **renovar** la sesión. Entre renovaciones, el token manda: si a alguien se le quitase de la empresa, su token seguiría valiendo hasta que caduque (30 minutos). Como todavía no existe la operación de quitar usuarios, no es explotable hoy, pero lo será cuando exista.

**5. El login no sabe elegir entre varias empresas** (ver la sección anterior).

**6. La lista blanca de `IgnoreQueryFilters` se compara por nombre de fichero.** Un fichero nuevo llamado `TokenService.cs` en otra carpeta pasaría el test. Es un riesgo menor, pero real.

**7. Todo el aislamiento está en el código.** La base de datos no lo impone por sí misma. SQL Server tiene un mecanismo (*Row-Level Security*) que podría añadir una segunda barrera a nivel de base de datos. No está en ningún ADR y sería una mejora para más adelante, si el riesgo lo justifica: es defensa en profundidad.

Observa lo que **sí** cubre el diseño: el descuido más probable (un handler que olvida filtrar) **no puede ocurrir**, porque el handler no filtra: lo hace el sistema. Los puntos débiles son sobre todo los que aparecerán al **crecer** (nuevas entidades, usuarios en varias empresas).

## El mapa de amenazas del aislamiento

Una tabla para ver de un vistazo qué amenaza cubre qué control (el germen del mapa de amenazas final del manual):

| Amenaza | Control | Dónde | Test |
|---|---|---|---|
| Un handler olvida filtrar por empresa | Filtro global automático | `ApplicationDbContext.cs:63` | `TenantIsolationIntegrationTests` |
| El cliente dice que es de otra empresa | El tenant sale del token firmado | `TenantContext.cs:17-20` | Validación de firma (capítulo 6) |
| El cliente envía `tenantId` en el JSON | Ningún Command/Query tiene `TenantId` | Los `*Command.cs` | `NoCommandOrQuery_AcceptsTenantIdFromTheClient` |
| Alguien se salta el filtro con `IgnoreQueryFilters` | Lista blanca vigilada | `SecurityArchitectureTests.cs:16-20` | `IgnoreQueryFilters_IsOnlyUsedInTheAllowList` |
| Adivinar el `Id` de un recurso ajeno | El filtro no lo encuentra; mismo 404 que si no existiera | `GlobalExceptionHandler.cs:71-72` | `Jobs_AreNotVisibleAcrossTenants` (lectura) |
| Token manipulado para cambiar de empresa | Firma HMAC-SHA256 | `DependencyInjection.cs:57-68` | Capítulo 6 |
| Entidad nueva sin la marca | **Sin control** | — | **Sin test** (punto débil 1) |
| Handler futuro que lea `Users` o `Tenants` | **Sin control** (por diseño) | — | **Sin test** (punto débil 2) |

Las dos últimas filas están en blanco a propósito: son las que habría que cerrar a continuación.

## Para comprobar que lo has entendido

1. ¿De dónde saca el servidor la empresa de Marta en cada petición, y por qué no se puede falsificar?
2. Un handler nuevo escribe `dbContext.Customers.ToListAsync()` sin ninguna condición. ¿Qué devuelve y por qué?
3. ¿Por qué `Tenant` y `User` no llevan `ITenantOwned`?
4. ¿Por qué el login necesita `IgnoreQueryFilters()` y qué impide que se use en cualquier otro sitio?
5. ¿Por qué el test de aislamiento usa SQL Server real en vez de una base en memoria?
6. Nombra dos puntos débiles del mecanismo actual y cómo cerrarías uno.

*(Respuestas: 1 = del claim `tenant_id` del token JWT, que está firmado con el secreto del servidor; si se altera, la firma no coincide y se rechaza. 2 = solo los clientes de la empresa de Marta, porque el filtro global añade `WHERE TenantId = ...` automáticamente. 3 = `Tenant` es la propia empresa y `User` puede pertenecer a varias; ninguno es "de" una empresa concreta. 4 = en el login aún no hay tenant activo, es lo que se está determinando; lo impide un test que escanea el código y falla si aparece en un fichero fuera de la lista blanca. 5 = porque la base en memoria no ejecuta SQL real y no prueba que el filtro se traduzca bien; es el test más importante. 6 = por ejemplo, entidad nueva sin la marca `ITenantOwned` (se cerraría con un test de arquitectura que exija la marca a toda entidad con `TenantId`) y la pertenencia no revalidada en cada petición.)*

## Siguiente capítulo

El capítulo 6 explica **identidad y autenticación**: cómo se registra alguien, cómo se guarda su contraseña, cómo se emite el token que acabamos de ver y cómo funciona la renovación de sesión con detección de robo.
