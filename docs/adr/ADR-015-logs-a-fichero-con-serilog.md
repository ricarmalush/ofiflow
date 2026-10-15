# ADR-015 — Logs a fichero con Serilog

**Estado:** Aceptado (2026-10-15)
**Fecha:** 2026-10-15
**Proyecto:** OfiFlow

---

## Contexto

OfiFlow ya registra lo importante: 23 eventos de seguridad con un `EventId` estable (ADR-009 R5), los 500 con su excepción completa y un `traceId` que une la respuesta con la entrada del log. Pero **solo se escribe en la consola**. Al cerrar la terminal, o al reiniciar la API, todo desaparece. Quien quiera investigar un fallo ocurrido hace una hora, o ver "de golpe" qué falló durante una sesión de pruebas, no tiene dónde mirar.

Hay que decidir cuatro cosas:

1. **Con qué biblioteca** se escribe a fichero.
2. **Cómo se engancha** a la aplicación sin tocar el código que ya registra eventos ni los tests que lo comprueban.
3. **En qué formato**: texto legible o JSON.
4. **Dónde y cuánto tiempo** se guardan.

## Problema

Cada decisión tiene una opción cómoda con una trampa:

- El fichero de **texto** es lo más fácil de leer, pero la ruta de la petición la elige quien llama y puede traer un salto de línea (`%0A`) seguido de una línea de log inventada: **falsificación de entradas** (*log forging*, CWE-117). En una investigación, una línea falsa es peor que no tener línea.
- La forma habitual de instalar Serilog (`UseSerilog`) **sustituye la fábrica de loggers** de Microsoft. Los tests de OfiFlow que comprueban los eventos de seguridad añaden su propio proveedor de logging; con la sustitución dejarían de ver nada y **pasarían en falso**.
- Un fichero que crece sin límite acaba llenando el disco, y uno que se conserva para siempre acumula direcciones IP, que son datos personales.

## Opciones consideradas

### 1. Biblioteca

**A. Un proveedor propio sobre `Microsoft.Extensions.Logging`.** Cero dependencias, pero habría que escribir rotación, retención, formato y escritura concurrente segura: reinventar lo que ya está resuelto.

**B. Serilog (`Serilog.AspNetCore`).** Estándar de facto en .NET: destino de fichero con rotación diaria y retención, formateadores JSON y configuración por `appsettings`. Una dependencia más en la cadena de suministro, que se mitiga con la política del proyecto (versión de más de tres meses, auditoría NuGet y Dependabot).

**C. Un servicio externo (Seq, Application Insights, ELK).** Es la respuesta correcta para producción, pero exige una infraestructura que todavía no existe.

### 2. Cómo engancharlo

**A. `UseSerilog`.** Sustituye el `ILoggerFactory`. Descartada por lo dicho arriba.

**B. `builder.Logging.AddSerilog(logger)`.** Serilog como **un proveedor más**, junto a la consola y a lo que añadan los tests. Los handlers siguen recibiendo `ILogger<T>` de Microsoft y no se enteran.

### 3. Formato

**A. Texto.** Legible a simple vista; vulnerable a la falsificación de entradas.

**B. JSON, un registro por línea (`RenderedCompactJsonFormatter`).** El salto de línea de un valor queda escapado **dentro** del mismo registro. Incluye el mensaje ya compuesto (`@m`), el nivel, la excepción (`@x`) y las propiedades (`EventId`, `TraceId`, `SourceIp`...) como campos que se pueden filtrar.

## Decisión

**B en las tres.** Serilog, enganchado con `AddSerilog` como un proveedor más, escribiendo JSON.

- **Dónde:** en `Development` y en `Production` (`appsettings.Development.json` y `appsettings.Production.json`), `logs/ofiflow-AAAAMMDD.log` relativo al directorio desde el que arranca la API (la carpeta está en `.gitignore`). Un fichero por día. La consola se mantiene en ambos.
- **Cuánto:** se conservan 14 ficheros; los anteriores se borran solos.
- **Qué nivel:** `Information` para el código de OfiFlow; `Warning` para `Microsoft.*` y `System.*` (si no, cada petición añade dos líneas de ruido); `Information` para `Microsoft.Hosting.Lifetime`, que dice cuándo arranca y para la API.
- **Cambiar el destino en producción sin recompilar:** la ruta se sobrescribe con una variable de entorno (`Serilog__WriteTo__0__Args__path=/var/log/ofiflow/ofiflow-.log`). En un contenedor, sin volumen montado el fichero se pierde al reiniciar: allí se montará un volumen o se sustituirá el destino por otro (Seq, Application Insights) cuando se sepa dónde se despliega. Está verificado arrancando la API en `Production`: crea el fichero y escribe en él.
- **Los tests con SQL Server real** arrancan la API en el entorno `Testing`, que no lee `appsettings.Development.json`, y no escriben ficheros de log (ver la última consecuencia para los que no).

Cuatro tests de arquitectura (`LoggingConfigurationTests`, cinco casos) hacen cumplir las reglas que no se ven: todo destino de fichero usa un formateador JSON, Development y Production escriben a fichero, `logs/` está en `.gitignore` y se usa `AddSerilog` y no `UseSerilog`.

## Consecuencias

**Lo bueno**
- Un fallo se puede investigar después: `EventId`, `TraceId` y `SourceIp` quedan en el fichero, y el `traceId` que recibe el cliente en un error sirve para encontrar la entrada exacta.
- Los handlers y los tests existentes no cambian.
- Los logs no se pueden falsificar con saltos de línea.

**Lo que hay que saber**
- **Dos filtros de nivel, no uno.** `AddSerilog` desactiva el filtro `Logging:LogLevel` de Microsoft para su propio proveedor: lo que llega al fichero lo decide solo `Serilog:MinimumLevel`. La consola sigue gobernada por `Logging:LogLevel`. Hay que mantener ambos si se quiere el mismo ruido en los dos.
- **Datos personales.** El fichero guarda IPs y Ids. El log ya está diseñado para no llevar emails, contraseñas ni tokens (ADR-009 R5), pero una IP es un dato personal para el RGPD: de ahí los 14 días y que la carpeta no se suba al repositorio. En producción, además, la carpeta de logs **debe tener permisos restringidos** a la cuenta que ejecuta la API (esto no lo hace el código: es una tarea del despliegue).
- **Dos ficheros de configuración repetidos.** `Development` y `Production` definen el mismo destino. Se duplica a propósito: ponerlo en `appsettings.json` lo activaría también en `Testing` y los tests escribirían ficheros. Si cambia uno, hay que cambiar el otro.
- **Tamaño.** Serilog deja de escribir en un fichero al llegar a 1 GB. Con rotación diaria no debería ocurrir, pero un ataque que genere logs a ráfagas puede acercarse; eso se vigilará con el destino de producción.
- **Los factories de test que arrancan en `Development` o `Production`** (los de Swagger y el contrato de errores necesitan Development; los de arranque del MFA, Production) sí crean ficheros en la carpeta de salida de los tests. Es inofensivo (`bin/` está ignorado) pero no es elegante.

## NEXT

- Confirmar el destino de producción cuando exista el despliegue (volumen para el fichero, stdout recogido por la plataforma, Seq o Application Insights).
- **Alertas** sobre los `EventId` críticos (`1003`, `1005`, `1504`, `1507`): hoy hay registro, no vigilancia.
- Revisar la política de retención con lo que decida el responsable de protección de datos.
- Registro de auditoría persistente ("quién cambió qué y cuándo"), previsto para cuando existan facturas.
