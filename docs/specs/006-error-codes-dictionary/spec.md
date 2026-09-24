# Spec — 006-error-codes-dictionary

**Estado:** Aprobada
**Fecha:** 2026-09-24
**Bounded Context:** Transversal (Domain, Application y Api; toca Customers, Jobs, Tenancy e Identity)
**Depende de:** ADR-011 (esta spec lo implementa), ADR-009, `docs/specs/004-security-hardening/`

---

## Objetivo

Sacar del código todos los textos de error que llegan al usuario y sustituirlos por **códigos de error estables** más un **diccionario de mensajes** (`.resx`) en la API. Así la internacionalización queda preparada (sección 34), la API responde en el mismo idioma en cualquier servidor, y el frontend podrá reconocer cada error por su código. No añade funcionalidad visible: mejora la base sobre la que se construirá el resto del flujo de valor.

## Alcance — Qué SÍ incluye

- `DomainException` y clases de códigos por bounded context (`CommonErrors`, `CustomerErrors`, `JobErrors`, `TenancyErrors`, `IdentityErrors`).
- Sustituir los 12 mensajes de Domain por códigos.
- `BusinessRuleException`, `NotFoundException` e `IdentityOperationException` con código; quitar la conversión de `InvalidOperationException` en los 3 handlers de Jobs.
- `.WithErrorCode(...)` en las reglas propias de los validadores.
- `ErrorMessages.resx` (español) en la API, con localización de ASP.NET Core (`AddLocalization`, `UseRequestLocalization`, lista cerrada de idiomas: `es`).
- `GlobalExceptionHandler` y el rechazo del rate limiting traducen código → mensaje y devuelven `code` y `errors[]` en `ProblemDetails`.
- Guardarraíles: test de diccionario completo y test de arquitectura sin excepciones genéricas en Domain.

## Alcance — Qué NO incluye

- **NOW** (fuera de esta spec): ninguno.
- **NEXT:** cómo mostrará los errores el frontend Angular (traducir por código en el cliente o usar el `message` del backend).
- **LATER** (fase 11 del roadmap): otros idiomas (`ErrorMessages.en.resx`), formatos de fecha, número y moneda, zona horaria.
- **NEVER** (decisión de ADR-011): traducir los mensajes de log (su parte estable es el `EventId`) y los mensajes de configuración al arrancar (los leen desarrolladores, no clientes).

## Reglas / invariantes

- Ningún texto para el usuario en Domain ni en Application.
- Todo código de error sigue el formato `{entidad}.{error}` (minúsculas, `snake_case`; prefijo = la entidad a la que se refiere el error) y tiene entrada en `ErrorMessages.resx`.
- Un código publicado no cambia ni se reutiliza con otro significado.
- Los argumentos de un mensaje nunca son datos personales ni el valor introducido por el usuario (ADR-009 R5).
- Un error de negocio (`DomainException`, `BusinessRuleException`, validación) → **400**; `NotFoundException` → **404**; cualquier otra excepción → **500** con mensaje genérico.
- Idioma de la respuesta: el de `Accept-Language` si está en la lista admitida; si no, español.

## Modelo conceptual

```
Domain/Common/DomainException(Code, Arguments)
Domain/Common/CommonErrors         common.email_required, common.email_invalid,
                                   common.phone_required, common.phone_invalid
Domain/Customers/CustomerErrors    customer.name_required
Domain/Jobs/JobErrors              job.title_required, job.cannot_start, job.cannot_complete,
                                   job.cannot_cancel_completed
Domain/Tenancy/TenancyErrors       tenant.name_required
Domain/Identity/IdentityErrors     user.name_required
Application (códigos propios)      customer.not_found, job.not_found, tenant_user.not_found,
                                   customer.has_active_jobs, user.email_already_registered
Api/Resources/ErrorMessages.resx   código → mensaje en español (+ títulos, 500 genérico, 429)
```

La lista exacta de códigos se cierra durante la implementación; el test de diccionario completo garantiza que ninguno se queda sin mensaje.

## Criterios de aceptación

- Dado un `Customer` sin nombre, cuando se crea en Domain, entonces se lanza `DomainException` con código `customer.name_required` (el test comprueba el código, no el texto).
- Dado un `Job` en `New`, cuando se intenta completar, entonces se lanza `DomainException` con código `job.cannot_complete`, y la API responde **400** con `code: "job.cannot_complete"` y el mensaje en español.
- Dado un cliente inexistente, cuando se pide por Id, entonces la API responde **404** con `code: "customer.not_found"`.
- Dado un `CreateCustomerCommand` con el nombre demasiado largo, cuando se envía, entonces la respuesta tiene `code: "validation.failed"` y `errors[]` con `field: "Name"`, un código y un mensaje **en español**, también si el servidor tiene el sistema operativo en inglés.
- Dada una petición con `Accept-Language: fr`, cuando falla, entonces el mensaje sale en español (idioma no admitido → idioma por defecto).
- Dado un código nuevo añadido a una clase `*Errors` sin su entrada en `ErrorMessages.resx`, cuando se ejecuta `dotnet test`, entonces el test de diccionario falla indicando qué código falta.
- Dado un `new ArgumentException(` añadido en Domain, cuando se ejecuta `dotnet test`, entonces el test de arquitectura falla.
- Dado un fallo interno cualquiera, cuando ocurre, entonces sigue siendo **500** con el mensaje genérico (ahora tomado del diccionario) y `traceId`; ya no se puede confundir con un error de negocio.

**Caso multi-tenant obligatorio:** esta spec no crea datos. Los 404 de recursos de otro tenant deben seguir respondiendo exactamente igual que los de recursos inexistentes (mismo código `*.not_found`), para no revelar que el recurso existe en otra empresa. Los tests de aislamiento existentes deben seguir en verde.

## Seguridad (ADR-009)

- **Datos personales:** los argumentos de los mensajes no incluyen valores introducidos por el usuario (ni email ni teléfono). Se mantiene el comportamiento de la spec 004.
- **Autorización:** sin cambios.
- **Entrada:** la cabecera `Accept-Language` la procesa el middleware estándar contra una lista cerrada; no se usa para construir rutas de fichero ni ningún otro dato.
- **Aislamiento:** un 404 de otro tenant es indistinguible de uno inexistente (ver caso multi-tenant).
- **SQL crudo:** no aplica.
- **Abuso:** no aplica.
- **Eventos de seguridad:** sin cambios. El evento 1201 sigue registrando los 500.
- **Mejora:** separar `DomainException` de las excepciones genéricas evita presentar un fallo del sistema como un error del usuario.

## Decisiones técnicas relevantes

- Se apoya en: **ADR-011** (todas las reglas), ADR-003 (`ValidationBehavior`), ADR-009 (R5).
- `IStringLocalizer<ErrorMessages>` con una clase marcadora `ErrorMessages` en `Api/Resources`, sin depender del generador de código de Visual Studio: funciona igual con `dotnet build` y en el CI.
- ¿Requiere una ADR nueva? Sí: **ADR-011**, redactado a la vez que esta spec.

## Fuera de alcance / Backlog relacionado

- Todo lo marcado como NEXT o LATER en ADR-011, apartado "Qué queda abierto".
- La spec prevista de logs de seguridad y MITRE ATT&CK pasa a ser la **007**.

---

## Validación antes de aprobar

> ¿Esta funcionalidad ayuda a conseguir o mantener al primer cliente? (sección 84 del prompt maestro)

**Indirectamente sí, y es más barata ahora que después.**
- Hoy hay 17 mensajes y 5 aggregates. Cada feature nueva (Agenda, Presupuestos, Facturas) añadiría más textos al código; cambiar el enfoque después costaría varias veces más.
- El frontend del piloto recibirá errores con código desde el primer día, sin tener que interpretar frases.
- Coste acotado: unas 6-8 h.
