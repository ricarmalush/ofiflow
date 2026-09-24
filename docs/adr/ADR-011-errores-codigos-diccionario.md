# ADR-011 — Errores de dominio, códigos de error y diccionario de mensajes

**Estado:** Aceptado
**Fecha:** 2026-09-24
**Proyecto:** OfiFlow

---

## Contexto

Una revisión de código (2026-09-24) detectó **17 mensajes de error en español escritos directamente en el código**, repartidos entre Domain (value objects, aggregates, transiciones de estado), Application (validadores, excepciones) e Infrastructure. Además:

- La sección 34 del prompt maestro pide preparar la **internacionalización (idioma) desde el inicio**, y la fase 11 del roadmap la implementa. Con los textos en el código, traducir exigiría tocar todas las capas.
- Los mensajes **por defecto de FluentValidation** ("no debería estar vacío"…) salen en el idioma del sistema operativo del servidor: español en el equipo del autor, inglés en un runner Linux de GitHub o en un servidor en la nube. El comportamiento de la API depende de dónde se ejecute.
- El cliente (futuro frontend Angular) solo recibe un **texto**. Para reaccionar a un error concreto (marcar un campo, mostrar una ayuda) tendría que interpretar frases, que pueden cambiar.
- Domain lanza excepciones **genéricas de .NET** (`ArgumentException`, `InvalidOperationException`), y tres handlers de Jobs capturan `InvalidOperationException` para convertirla en `BusinessRuleException`. Un fallo real del sistema que usara ese mismo tipo podría acabar presentado como "error del usuario" (400) en vez de como error interno (500).

## Problema

¿Cómo se representan los errores de negocio para que (1) los textos no estén en el código, (2) se puedan traducir sin tocar Domain ni Application, (3) el cliente pueda identificarlos sin interpretar texto, y (4) no se confundan con fallos del sistema?

## Opciones consideradas

**Opción A: Agrupar los textos en clases de constantes** (`CustomerErrors.NameRequired = "El nombre…"`)
- Ventajas: barata (≈ 1 h).
- Desventajas: solo cambia el texto de sitio. Sigue en el código, sigue sin poder traducirse y el cliente sigue sin un identificador estable.

**Opción B: Códigos de error estables con un mensaje por defecto en el código**
- Ventajas: identificador estable para el cliente y para los tests.
- Desventajas: los textos siguen en el código; traducir más adelante seguiría exigiendo tocarlo.

**Opción C: Códigos de error estables + diccionario de mensajes en ficheros de recursos (`.resx`) en la capa API** (propuesta del autor, 2026-09-24, con dos matices: cada mensaje va asociado a un código, y el diccionario vive en la API)
- Ventajas: ningún texto para el usuario en Domain ni en Application. Traducir es **añadir un fichero** (`ErrorMessages.en.resx`) sin tocar código. Mecanismo estándar de .NET (`IStringLocalizer`), sin dependencias nuevas.
- Desventajas: algo más de estructura (≈ 6-8 h) y un contrato nuevo que hay que mantener: cada código necesita su entrada en el diccionario.

**Opción D: C + varios idiomas funcionando ya**
- Desventajas: construye la fase 11 del roadmap antes de tiempo, sin ningún cliente que lo necesite. Contradice "diseñar para escalar, construir para la realidad actual".

## Decisión

Se adopta la **Opción C**, con estas reglas:

### R1 — Domain solo conoce códigos
- Cada bounded context define sus códigos en una clase estática: `CustomerErrors`, `JobErrors`, `TenancyErrors`, `IdentityErrors`, y `CommonErrors` para el shared kernel (`Email`, `PhoneNumber`).
- Formato del código: `{contexto}.{error}` en minúsculas y `snake_case`. Ejemplos: `customer.name_required`, `job.cannot_start`, `common.email_invalid`.
- Domain lanza una excepción propia, **`DomainException(code, params args)`**, en lugar de `ArgumentException` o `InvalidOperationException`. Los argumentos solo pueden ser datos **no personales** (estados, Ids), nunca el valor que introdujo el usuario (ADR-009 R5).
- Las excepciones de programación siguen siendo las de .NET (`ArgumentNullException` y similares): indican un bug, no un error de negocio, y deben acabar en 500.

### R2 — Application también usa códigos
- `BusinessRuleException`, `NotFoundException` e `IdentityOperationException` reciben un **código** (y argumentos), no un texto.
- Los handlers **dejan de capturar** `InvalidOperationException`: la `DomainException` llega sin transformar al `GlobalExceptionHandler`.
- Los validadores de FluentValidation usan `.WithErrorCode(código)` en las reglas propias. Las reglas estándar (`NotEmpty`, `MaximumLength`…) conservan sus códigos de FluentValidation y sus traducciones incluidas, que ahora siguen el idioma de la petición.

### R3 — El diccionario vive en la API
- `src/OfiFlow.Api/Resources/ErrorMessages.resx`: **español, idioma por defecto**. Clave = código de error; valor = mensaje, con marcadores `{0}` para los argumentos.
- También van al diccionario los demás textos que llegan al cliente: los títulos de `ProblemDetails`, el mensaje genérico del 500 y el del 429.
- Localización estándar de ASP.NET Core: `AddLocalization` + `UseRequestLocalization`, con **lista cerrada** de idiomas admitidos (hoy solo `es`). La cabecera `Accept-Language` solo puede elegir entre ellos; cualquier otro valor cae al español.
- `GlobalExceptionHandler` traduce código → mensaje con `IStringLocalizer<ErrorMessages>`.

### R4 — Contrato de respuesta
Todas las respuestas de error mantienen `ProblemDetails` (RFC 9457) y añaden campos estables:

```json
{
  "status": 400,
  "title": "Error de validación",
  "detail": "La petición contiene errores.",
  "code": "validation.failed",
  "errors": [ { "field": "Name", "code": "MaximumLengthValidator", "message": "…" } ],
  "traceId": "00-…"
}
```

- `code` y `errors[].code` son **contrato público**: no se cambian ni se reutilizan con otro significado.
- `detail` y `message` son texto para personas: pueden cambiar de redacción en cualquier momento.

### R5 — Guardarraíles automáticos
- **Test de diccionario completo**: recorre por reflexión todas las constantes de las clases `*Errors` y falla si alguna no tiene entrada en `ErrorMessages.resx`. Así nunca llega al usuario un `customer.name_required` sin traducir.
- **Test de arquitectura**: Domain no contiene `new ArgumentException(` ni `new InvalidOperationException(` (los errores de negocio usan `DomainException`).
- Los tests de Domain y Application comprueban el **código**, no el texto.

## Consecuencias

### Positivas
- Ningún texto para el usuario en Domain ni en Application; todos en un único diccionario, revisable sin tocar lógica.
- Internacionalización preparada (sección 34): un idioma nuevo es un fichero `.resx` más y una entrada en la lista de idiomas admitidos.
- Idioma coherente de la API en cualquier servidor.
- El frontend puede reaccionar a errores concretos por su código.
- Los errores de negocio (`DomainException`) quedan separados de los fallos del sistema: se elimina el riesgo de presentar un bug como un 400.

### Negativas / riesgos aceptados
- Contrato nuevo que mantener (código ↔ entrada del diccionario), mitigado con el test de R5.
- Cambia la forma de las respuestas de error (`code` y `errors[]`). Se acepta porque todavía no existe ningún cliente (el frontend no se ha empezado).
- Unos 24 tests actuales comprueban tipos de excepción (`ArgumentException`, `InvalidOperationException`…) y hay que adaptarlos.

### Qué queda cerrado (no se debe reabrir sin una razón nueva y explícita)
- No escribir textos para el usuario en Domain ni en Application.
- No cambiar ni reutilizar un código de error ya publicado.
- No usar excepciones genéricas de .NET para errores de negocio.

### Qué queda abierto para revisar más adelante
- **Fuera de alcance, a propósito:** los **mensajes de log** (los leen operadores; lo estable es su `EventId`, ADR-009 R5) y los **mensajes de configuración al arrancar** (`Falta 'Jwt:Secret'`…), que leen desarrolladores y no clientes.
- **LATER (fase 11):** inglés u otros idiomas (`ErrorMessages.en.resx`), formatos de fecha, número y moneda, zona horaria (resto de la sección 34).
- **NEXT (frontend):** si Angular traduce por código en su lado o muestra el `message` del backend.

---

## Relación con otros ADR

- Depende de: ADR-003 (el `ValidationBehavior` y los handlers de MediatR se adaptan a códigos).
- Depende de: ADR-005 (una clase de códigos por bounded context, coherente con el monolito modular).
- Refuerza: ADR-009 (R5: los argumentos de los mensajes nunca son datos personales; y separa errores de negocio de fallos del sistema).
- Afecta a: ADR-006 (Domain sustituye las excepciones genéricas de las transiciones de `Job` por `DomainException`).
