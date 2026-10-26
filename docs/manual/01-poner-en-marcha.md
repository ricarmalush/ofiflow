# Capítulo 1 — Poner el proyecto en marcha

## Qué vas a aprender

- Qué necesitas instalar y para qué sirve cada cosa.
- Los tres pasos para arrancar la API en tu máquina, y **por qué** cada uno es como es.
- Dónde vive el secreto JWT y por qué no está en el repositorio.
- Cómo comprobar que funciona haciendo tu primera petición.
- Cómo ejecutar los tests (y por qué necesitas Docker para ellos y no para arrancar la API).
- Qué errores te vas a encontrar con más probabilidad y qué significan.

> Este capítulo está **comprobado ejecutando los pasos**, no copiado: se hicieron contra un SQL Server desechable en Docker, sin tocar la LocalDB del desarrollador. Lo que difiere de tu máquina (la cadena de conexión) se indica donde ocurre.

## Qué necesitas

| Herramienta | Para qué | ¿Imprescindible? |
|---|---|---|
| **.NET 10 SDK** | Compilar y ejecutar la API y los tests | Sí |
| **SQL Server** | La base de datos. En Windows basta **LocalDB**, que se instala con Visual Studio | Sí, para arrancar la API |
| **`dotnet-ef`** | La herramienta que crea y actualiza las tablas a partir de las migraciones | Sí, para el paso 2 |
| **Docker** | Los tests de integración levantan un SQL Server real en un contenedor | Solo para los tests |
| **Visual Studio** | Abrir la solución y ejecutar el fichero `.http` de pruebas manuales | No: sirve cualquier editor y `curl` |

Para instalar `dotnet-ef` (una vez por máquina; el repositorio no lo trae en un manifiesto):

```bash
dotnet tool install --global dotnet-ef
```

Comprueba que lo tienes todo:

```bash
dotnet --version        # debe empezar por 10.
dotnet ef --version
sqllocaldb info         # debe listar MSSQLLocalDB (solo Windows)
docker version          # solo si vas a ejecutar los tests
```

## Los tres pasos

Desde la carpeta raíz del repositorio:

### Paso 1 — Los secretos (JWT y clave del segundo factor)

```bash
dotnet user-secrets set "Jwt:Secret" "$(openssl rand -base64 32)" --project src/OfiFlow.Api
```

El **secreto JWT** es la clave con la que la API firma los tokens de acceso (capítulo 6). Quien la conozca puede fabricar tokens de cualquier usuario y empresa, así que **nunca se guarda en el repositorio**: se genera una vez por máquina y se guarda en tus *user-secrets*, un fichero fuera del proyecto:

```
%APPDATA%\Microsoft\UserSecrets\950a2b04-890b-4264-b9a2-806caef42f16\secrets.json
```

El identificador de esa carpeta (`UserSecretsId`) está en [`OfiFlow.Api.csproj`](../../src/OfiFlow.Api/OfiFlow.Api.csproj). ASP.NET Core carga ese fichero solo en el entorno `Development`, y es la fuente de configuración con **menos prioridad**: una variable de entorno o la línea de comandos la superan.

Sin `openssl` (por ejemplo, en PowerShell):

```powershell
$b = New-Object byte[] 32; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b)
dotnet user-secrets set "Jwt:Secret" ([Convert]::ToBase64String($b)) --project src/OfiFlow.Api
```

**El segundo secreto (spec 010): `Mfa:EncryptionKey`.** El segundo factor guarda un secreto por cuenta y lo guarda **cifrado**; esta es la clave con la que se cifra (32 bytes en Base64, como la del JWT). La API **no arranca sin ella**, igual que sin `Jwt:Secret`:

```bash
dotnet user-secrets set "Mfa:EncryptionKey" "$(openssl rand -base64 32)" --project src/OfiFlow.Api
```

En PowerShell, el mismo fragmento de arriba cambiando el nombre de la clave. Esta clave **no se puede perder**: sin ella, los secretos ya guardados no se pueden descifrar y esas cuentas tendrían que reiniciar su segundo factor. Desde la spec 010 hay rutas para activar el segundo factor, así que la clave de producción habrá que guardarla con copia de seguridad (y rotarla cuando exista el procedimiento).

**El tercer secreto (spec 011): `Jwt:PlatformSecret`.** Las sesiones de los operadores de la plataforma (capítulo 6) se firman con **otra clave**, distinta de `Jwt:Secret`, y con otra audiencia (`Jwt:PlatformAudience`, por defecto `OfiFlow.Platform`). Es a propósito: la plataforma y las empresas son dos mundos, y con claves distintas un fallo de validación en uno no deja pasar un token del otro. La API **no arranca sin ella**, ni si no mide 32 bytes, ni si es igual que `Jwt:Secret`, ni si la audiencia coincide con la de las empresas:

```bash
dotnet user-secrets set "Jwt:PlatformSecret" "$(openssl rand -base64 32)" --project src/OfiFlow.Api
```

> **Si ya tenías la API funcionando antes de la spec 011, ahora no arrancará hasta que hagas esto.** Es lo esperado (arrancar sin la clave sería arrancar sin poder firmar ni validar las sesiones de plataforma). El mensaje de error te dice el comando. En PowerShell, igual que los otros dos: genera 32 bytes aleatorios y cámbiale el nombre de la clave.

En producción no existirá ese fichero: el secreto llegará por la variable de entorno `Jwt__Secret` (el doble guion bajo equivale a los dos puntos) o desde un gestor de secretos. Esa decisión es la R8 del ADR-009.

### Paso 2 — La base de datos

```bash
dotnet ef database update --project src/OfiFlow.Infrastructure
```

EF Core tiene un historial de **migraciones**: ficheros que describen cada cambio del esquema (crear una tabla, añadir una columna). Este comando mira qué migraciones faltan en tu base de datos y las aplica en orden. Hoy son tres, y al ejecutarlo en una base vacía verás:

```
Applying migration '20260921204751_InitialCreate'.
Applying migration '20260921214423_AddTenancyAndIdentity'.
Applying migration '20260922201359_AddJobs'.
```

¿A qué base de datos se conecta? A la que dice la cadena de conexión `Default`, que en desarrollo está en [`appsettings.Development.json`](../../src/OfiFlow.Api/appsettings.Development.json) y apunta a LocalDB:

```
Server=(localdb)\mssqllocaldb;Database=OfiFlow;Trusted_Connection=True;TrustServerCertificate=True;
```

La fábrica que usa `dotnet ef` ([`ApplicationDbContextFactory`](../../src/OfiFlow.Infrastructure/Persistence/ApplicationDbContextFactory.cs)) lee antes la variable de entorno `ConnectionStrings__Default` y, solo si no existe, usa esa LocalDB. Esto no es un capricho: en el pipeline de CI no hay LocalDB (es Linux), y la primera ejecución real de `dast.yml` falló justo por tener esa cadena escrita a mano (capítulo 10).

**Si no tienes LocalDB** (Linux, macOS o un Windows sin Visual Studio), levanta un SQL Server desechable y apunta la variable de entorno hacia él:

```bash
PW="Aa1!$(openssl rand -hex 8)"
docker run -d --name ofiflow-dev -e ACCEPT_EULA=Y -e "MSSQL_SA_PASSWORD=$PW" -p 14333:1433 \
  mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04
export ConnectionStrings__Default="Server=localhost,14333;Database=OfiFlow;User Id=sa;Password=$PW;TrustServerCertificate=True;"
```

Tendrás que exportar la variable en cada terminal desde la que ejecutes los pasos 2 y 3. Para borrar todo: `docker rm -f ofiflow-dev`.

### Paso 3 — Arrancar la API

```bash
dotnet run --project src/OfiFlow.Api
```

Por defecto usa el perfil `http` de [`launchSettings.json`](../../src/OfiFlow.Api/Properties/launchSettings.json): entorno `Development` y dirección **`http://localhost:5279`**. El perfil `https` añade `https://localhost:7228`.

Verás en la consola `Now listening on: ...` y `Application started`. Ya está en marcha.

## Tu primera petición

La API no tiene frontend, pero en `Development` incluye **Swagger UI**: una pantalla que lista todas las operaciones y permite probarlas desde el navegador, sin escribir peticiones a mano. Con la API en marcha, abre **`http://localhost:5279/swagger`** (con el perfil `https`, `https://localhost:7228/swagger`; en Visual Studio se abre sola).

Para probar una ruta protegida (las que tienen un candado):

1. Despliega `POST /api/v1/auth/register`, pulsa **Try it out** y **Execute** para crear una empresa. (Rellena un email y una contraseña propios.)
2. Haz lo mismo con `POST /api/v1/auth/login` y copia el `accessToken` de la respuesta.
3. Pulsa **Authorize** (arriba a la derecha), pega el token **sin** la palabra `Bearer` y confirma.
4. Prueba `GET /api/v1/customers`: devuelve **200** con una lista vacía. Sin autorizar, la misma petición da **401**.

Por debajo, Swagger UI lee la **descripción OpenAPI** de la API (la lista de operaciones, en JSON), que es también lo que lee ZAP en el pipeline y que puedes pedir directamente:

```bash
curl http://localhost:5279/openapi/v1.json
```

Ni esa ruta ni `/swagger` **existen fuera de `Development`**: el código las registra solo en ese entorno ([`Program.cs`](../../src/OfiFlow.Api/Program.cs)), y hay tests que lo comprueban (capítulo 9). Una API en producción no debe ofrecer su propio mapa ni una pantalla para probarla a cualquiera. Swagger necesitó una excepción en las cabeceras de seguridad, explicada en el capítulo 8.

Para probar de verdad, hay una colección ordenada de 51 peticiones en [`OfiFlow.Api.http`](../../src/OfiFlow.Api/OfiFlow.Api.http), que se ejecuta desde Visual Studio (cuenta, clientes, trabajos, errores a propósito, aislamiento entre dos empresas y el equipo: invitar, aceptar, cambiar roles y quitar miembros). Si prefieres la terminal, la secuencia mínima:

1. **Registrar una empresa** (crea la empresa, su primer usuario y lo hace `Owner`). `POST /api/v1/auth/register` con `companyName`, `userName`, `email` y `password`.
2. **Iniciar sesión.** `POST /api/v1/auth/login` con `email` y `password`: devuelve `accessToken` y `refreshToken`.
3. **Usar el token.** `GET /api/v1/customers` con la cabecera `Authorization: Bearer <accessToken>`.

Comprobado: sin el token el paso 3 devuelve **401**, y con él **200**. Esa diferencia es el capítulo 6 en una línea.

Dos cosas que te pueden despistar:

- **Los enumerados entran como número** (por ejemplo el tipo de cliente: 0 = persona, 1 = empresa), aunque las respuestas los devuelven como texto. Es un límite conocido; la colección `.http` lo explica en su cabecera.
- **El registro admite 3 por hora desde la misma IP**, aceptar una invitación 10 por hora, el login 5 por minuto y la renovación 20 por minuto (capítulo 8). Si pruebas mucho te llevarás un **429** con la cabecera `Retry-After`: no es un fallo, es el control funcionando.

## Dónde ver los logs

Mientras la API corre, la consola de `dotnet run` muestra lo que pasa. Al cerrarla se pierde, así que **también se guarda en un fichero** (en desarrollo y en producción): `src/OfiFlow.Api/logs/ofiflow-AAAAMMDD.log` (un fichero por día, los últimos 14, no se sube a Git). Cada línea es un registro JSON con la hora (`@t`), el nivel (`@l`), el mensaje (`@m`) y, si hubo una excepción, su traza (`@x`).

Cuando algo falle, busca los errores y avisos (en PowerShell, desde la raíz del repositorio):

```powershell
Get-Content src\OfiFlow.Api\logs\*.log -Encoding UTF8 | ConvertFrom-Json |
  Where-Object { $_.'@l' -in 'Error','Warning' } | Select-Object '@t','@l','@m','@x'
```

Si la respuesta de un error trae un `traceId`, filtra por él (`$_.TraceId -eq '...'`) para ver todo lo que ocurrió en esa petición. El porqué del formato y de las demás decisiones está en el capítulo 8 y en el [ADR-015](../adr/ADR-015-logs-a-fichero-con-serilog.md).

## Ejecutar los tests

```bash
dotnet test OfiFlow.slnx
```

Compila todo y ejecuta los cuatro proyectos de test (capítulo 9). Qué necesita cada uno:

| Proyecto | ¿Necesita Docker? |
|---|---|
| `OfiFlow.Domain.Tests` | No |
| `OfiFlow.Application.Tests` | No |
| `OfiFlow.Infrastructure.Tests` | Sí, en parte |
| `OfiFlow.Api.Tests` | Sí, en parte |

Los que usan Docker levantan un **SQL Server real en un contenedor** (la imagen `mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04`, fijada a una versión concreta). **No usan tu LocalDB ni tocan tu base de datos de desarrollo**: cada ejecución crea y destruye su propio contenedor. La primera vez Docker descarga la imagen y tarda más; las siguientes, no.

Una observación importante, porque es fácil confundirse: **para arrancar la API a mano necesitas LocalDB (o el contenedor del paso 2) pero no Docker; para los tests necesitas Docker pero no LocalDB.** Son dos necesidades distintas.

Para ejecutar solo lo que no necesita Docker:

```bash
dotnet test tests/OfiFlow.Domain.Tests
dotnet test tests/OfiFlow.Application.Tests
```

## Crear un operador de la plataforma

Un **operador de la plataforma** es la cuenta de quien administra OfiFlow entero (suspender una empresa, por ejemplo), no la de una empresa cliente. Es la cuenta más poderosa del sistema, y por eso **no se crea por la API**: no hay ninguna ruta HTTP para hacerlo. Se crea con un programa que se ejecuta a mano en el servidor, [`OfiFlow.Admin`](../../src/OfiFlow.Admin/Program.cs) (capítulo 3 explica por qué es un proyecto aparte y el capítulo 8, la seguridad que lo rodea).

> **Qué puedes hacer hoy y qué no.** Hoy puedes **crear** y **dar de baja** a un operador. Iniciar sesión como operador y usar el panel llegan en los bloques 5 y 6 de la spec 011: de momento es una cuenta que existe, con su segundo factor ya activo, pero sin ninguna ruta donde entrar.

**Antes de empezar** necesitas lo del paso 1 y el paso 2: la base de datos migrada y la clave del segundo factor (`Mfa:EncryptionKey`). **Esa clave debe ser exactamente la misma que usa la API.** Si fuera otra, el programa cifraría el secreto del operador con una clave que la API no tiene y, cuando exista el inicio de sesión de plataforma, no podría descifrarlo. En local no tienes que hacer nada: los dos proyectos comparten los mismos *user-secrets* y la misma cadena de conexión de desarrollo.

**El procedimiento, paso a paso** (en una terminal de verdad: el programa se niega a funcionar con la entrada redirigida, a propósito):

```bash
# bash
DOTNET_ENVIRONMENT=Development dotnet run --project src/OfiFlow.Admin -- create-platform-operator --email operadora@tu-dominio.com
```

```powershell
# PowerShell
$env:DOTNET_ENVIRONMENT = "Development"
dotnet run --project src/OfiFlow.Admin -- create-platform-operator --email operadora@tu-dominio.com
```

1. **Te pide la contraseña dos veces**, sin mostrar lo que tecleas (8 a 128 caracteres). Nunca se pasa como argumento: quedaría en el historial de la terminal y en la lista de procesos del servidor.
2. **Te muestra una clave y una dirección `otpauth://`.** Añádelas a tu app de autenticación (Google Authenticator, Microsoft Authenticator, Aegis...): escribiendo la clave a mano, o pegando la dirección si tu app lo permite. El programa no dibuja un QR a propósito: así el secreto no pasa por ningún servicio de terceros.
3. **Te pide un código de 6 dígitos** de esa app. Mientras no escribas uno válido **no se guarda nada**: ni cuenta, ni secreto, ni operador. Tienes 5 intentos; si los agotas, el programa termina sin haber creado nada y repites desde el principio.
4. Con un código válido se crea todo a la vez y se te muestran **10 códigos de recuperación**. Guárdalos ahora (un gestor de contraseñas): no se vuelven a mostrar y es la única forma de entrar si pierdes el móvil. Si los pierdes a ellos y al móvil, la única salida es dar de baja a ese operador y crear otro.

**Dar de baja a un operador:**

```bash
DOTNET_ENVIRONMENT=Development dotnet run --project src/OfiFlow.Admin -- disable-platform-operator --email operadora@tu-dominio.com
```

Te pide que escribas `SI`. La baja deja una fila en la auditoría y surtirá efecto en la **siguiente petición** del operador, no cuando caduque su sesión.

**Si algo falla**, el programa dice qué y qué no ha hecho (siempre «no se ha creado nada» o «no se ha modificado nada»):

| Mensaje | Qué pasa | Qué hacer |
|---|---|---|
| `Falta 'ConnectionStrings:Default'` | No encuentra la cadena de conexión | En producción, variable `ConnectionStrings__Default`; en local, `DOTNET_ENVIRONMENT=Development` |
| `Falta 'Mfa:EncryptionKey'` | No tiene la clave del segundo factor | La misma que la API (variable `Mfa__EncryptionKey` o *user-secrets*) |
| `Este programa pide la contraseña por la terminal...` | Entrada redirigida (una tubería, un script) | Ejecútalo en una terminal interactiva |
| `Ese email ya está registrado` | Lo usa una empresa u otro operador | Usa otro email (un email ocupado no se reutiliza) |
| `Código no válido` | Reloj del móvil desfasado, o código de otro minuto | Espera al siguiente código; la app y el servidor aceptan ±30 s |

**En producción** el programa se ejecuta desde el servidor (o desde una sesión con acceso a la base de datos), con las variables de entorno `ConnectionStrings__Default` y `Mfa__EncryptionKey`. **No recibe `Jwt:Secret`**, la clave que firma las sesiones: no la necesita y no debe poder arrancar con ella.

## Errores frecuentes

| Lo que ves | Qué significa | Qué hacer |
|---|---|---|
| La API se detiene al arrancar con **"Falta 'Mfa:EncryptionKey'..."** (o dice que no es Base64 o que mide otra cosa que 32 bytes) | Falta la segunda clave del paso 1, o tiene otro formato | Pon la clave con el comando del paso 1. El mensaje cuenta qué le pasa (falta, no es Base64, mide N bytes) pero nunca reproduce su valor |
| La API se detiene al arrancar con **"Falta 'Jwt:PlatformSecret'..."** (o dice que no es Base64, que mide otra cosa que 32 bytes, que es igual que `Jwt:Secret` o que la audiencia coincide con la de las empresas) | Falta la tercera clave del paso 1, o se parece demasiado a la de las empresas. Si tu API funcionaba antes de la spec 011, te pasará una vez | Pon una clave **distinta** con el comando del paso 1. El mensaje cuenta qué le pasa, pero nunca reproduce una clave |
| La API se detiene al arrancar con **"Falta 'Jwt:Secret'..."** | El paso 1 no está hecho (o lo hiciste para otro `UserSecretsId`) | Repite el paso 1. El mensaje es largo a propósito: lista **cada fuente de configuración que consultó** y qué longitud tiene el valor en cada una (nunca el valor), y la ruta exacta donde buscó `secrets.json` |
| `dotnet ef` no se reconoce | No está instalada la herramienta | `dotnet tool install --global dotnet-ef` |
| El paso 2 no encuentra el servidor | LocalDB no existe o no está iniciada | `sqllocaldb info` y `sqllocaldb start MSSQLLocalDB`; o usa el contenedor Docker de arriba |
| Los tests de integración fallan al instante | Docker no está en marcha | Arranca Docker Desktop y repite |
| **429** en el registro o el login | Superaste el límite de peticiones por IP | Espera lo que indica `Retry-After` |
| **400** al registrar | Datos inválidos, o el email ya está registrado | Lee `code` y `errors` de la respuesta: son estables (capítulo 7) |
| **401** en una ruta de clientes o trabajos | Falta el token o ha caducado (dura 30 minutos) | Inicia sesión de nuevo o usa `/auth/refresh` |

## Dónde mirar después

Con la API en marcha, el siguiente paso natural es **entender qué ha pasado** cuando haces una petición. Los capítulos 2 a 4 recorren la arquitectura y el viaje de una petición, de principio a fin. Si quieres ver cómo se aplica la seguridad, los capítulos 6 a 8.

## Para comprobar que lo has entendido

1. ¿Por qué el secreto JWT no está en `appsettings.json` y dónde está en tu máquina?
2. ¿Qué hace `dotnet ef database update` y en qué se diferencia de arrancar la API?
3. ¿Cómo sabe `dotnet ef` a qué base de datos conectarse, y cómo lo cambias sin tocar ningún fichero?
4. ¿Qué necesitas para arrancar la API a mano y qué para ejecutar los tests? ¿Coinciden?
5. ¿Por qué ni Swagger ni la ruta `/openapi/v1.json` existen en producción, y para qué sirve el botón Authorize?
6. Recibes un 429 al probar el login varias veces. ¿Es un error del programa?
7. La API se detiene con "Falta 'Jwt:Secret'". ¿Cuál es el primer sitio donde mirar y por qué el mensaje es tan largo?

*(Respuestas: 1 = porque quien lo conozca puede fabricar tokens de cualquier usuario y empresa; está en tus user-secrets, un fichero fuera del repositorio (`%APPDATA%\Microsoft\UserSecrets\<UserSecretsId>\secrets.json`), y en producción llegará por la variable `Jwt__Secret`. 2 = aplica las migraciones pendientes a la base de datos (crea o cambia tablas); arrancar la API no toca el esquema. 3 = lee la cadena `Default` de la configuración (LocalDB en `appsettings.Development.json`) y, si existe, la variable de entorno `ConnectionStrings__Default` tiene prioridad: se cambia con esa variable. 4 = API: SQL Server (LocalDB o contenedor) y el secreto, sin Docker; tests: Docker, sin LocalDB; no coinciden. 5 = porque ambas se registran solo en `Development`, y una API en producción no debe ofrecer su mapa ni una pantalla para probarla a cualquiera; Authorize guarda el token de acceso (el que devuelve el login) y lo envía en la cabecera `Authorization` de cada petición, para poder probar las rutas con candado. 6 = no: es el rate limiting de ADR-009 funcionando (login 5 por minuto por IP); espera lo que indica `Retry-After`. 7 = el paso 1, comprobando los user-secrets; el mensaje lista todas las fuentes de configuración consultadas con la longitud de lo que cada una tiene, así que se ve de un vistazo en cuál faltó o cuál lo pisó, sin mostrar nunca el valor.)*

## Siguiente capítulo

El capítulo 2 explica el **estilo arquitectónico**: qué es Clean Architecture, DDD y CQRS, y por qué OfiFlow los combina así.
