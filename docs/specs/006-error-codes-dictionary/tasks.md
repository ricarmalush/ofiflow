# Tasks — 006-error-codes-dictionary

**Spec relacionada:** [./spec.md](./spec.md)
**ADR relacionada:** [../../adr/ADR-011-errores-codigos-diccionario.md](../../adr/ADR-011-errores-codigos-diccionario.md)
**Estado general:** En curso (spec aprobada 2026-09-24)

---

## Convención

- Cada tarea completable y verificable en una sesión (1-4h).
- Se marca `[x]` solo cuando cumple la Definición de "Done" (sección 86).
- Orden: Domain → Application → API → Tests. Pasos pequeños, explicando el porqué de cada pieza para que el autor pueda defenderla en una entrevista.
- Se trabaja en la rama `feature/devsecops-pipeline`, que aún no se ha publicado, o en una rama propia si la spec 005 avanza antes.

## Checklist

### Domain (≈ 1,5 h)
- [x] `Domain/Common/DomainException.cs` (`Code`, `Arguments`)
- [x] Clases de códigos: `CommonErrors` (email), `CustomerErrors` (nombre y teléfono: `customer.phone_*`, porque `PhoneNumber` vive en Customers y no en el shared kernel), `JobErrors`, `TenancyErrors`, `IdentityErrors`
- [x] `Email`, `PhoneNumber`, `Customer`, `Job`, `Tenant` y `User` lanzan `DomainException` con su código (11 sustituciones; 0 textos en los `throw` de Domain)
- [x] Transiciones de `Job`: `DomainException(JobErrors.CannotStart, Status)` y similares
- [x] Tests de Domain: comprueban el **código** (13 aserciones; en las transiciones, también el estado). 54/54 en verde

### Application (≈ 1,5 h)
- [x] `BusinessRuleException`, `NotFoundException` e `IdentityOperationException` con código y argumentos
- [x] Códigos de Application: `customer.not_found`, `job.not_found`, `tenant_user.not_found`, `customer.has_active_jobs`, `identity.email_already_registered`. **Decisión:** van en las mismas clases por contexto de Domain (`CustomerErrors.NotFound`…), no en clases propias de Application: un solo vocabulario por contexto, visible desde todas las capas, y el test de diccionario recorre un único ensamblado
- [x] `Start/Complete/CancelJobCommandHandler`: eliminado el `catch (InvalidOperationException)`; 0 `catch` en Application
- [x] `IdentityService`: devuelve el código `identity.email_already_registered` en vez del texto
- [x] `ValidationRules`: `.WithErrorCode(...)` en lugar de `.WithMessage("…")` (email y teléfono)
- [x] Tests de Application adaptados: 9 aserciones de código en handlers y 2 en validadores. 53/53 en verde. Adelantado del bloque API: `DomainException` → 400 en `GlobalExceptionHandler`, para no dejar un commit intermedio en el que un error de negocio respondiera 500

### API (≈ 2 h)
- [ ] `Api/Resources/ErrorMessages.cs` (clase marcadora) + `ErrorMessages.resx` en español: todos los códigos, títulos de `ProblemDetails`, mensaje genérico del 500 y mensaje del 429
- [ ] `Program.cs`: `AddLocalization`, `UseRequestLocalization` con lista cerrada (`es`), idioma por defecto `es`
- [ ] `GlobalExceptionHandler`: mapa `DomainException`/`BusinessRuleException` → 400, `NotFoundException` → 404 y resto → 500; mensaje desde el diccionario; `ProblemDetails` con `code`, `errors[]` y `traceId`
- [ ] `RateLimiting`: título y mensaje del 429 desde el diccionario, con `code: "rate_limit.exceeded"`

### Tests (≈ 1,5-2 h)
- [ ] **Diccionario completo:** todas las constantes de las clases `*Errors` (Domain y Application) tienen entrada en `ErrorMessages.resx`
- [ ] **Arquitectura:** Domain no contiene `new ArgumentException(` ni `new InvalidOperationException(`
- [ ] **Integración:** 400 de negocio con `code` y mensaje en español (`job.cannot_complete`)
- [ ] **Integración:** validación con `errors[]` (campo, código, mensaje en español)
- [ ] **Integración:** `Accept-Language: fr` → mensaje en español
- [ ] **Integración:** 404 con `code: "customer.not_found"`
- [ ] Regresión: todas las suites en verde (incluidos el aislamiento de tenant y los tests de seguridad de la spec 004)

### Verificación manual (≈ 0,5 h)
- [ ] `dotnet run` y curl: completar un Job `New` → 400 con `code`; nombre de 201 caracteres → `errors[]` en español; recurso inexistente → 404 con `code`
- [ ] Repetir con `Accept-Language: en-US` → español (inglés todavía no admitido)

---

## Notas / bloqueos

- Hay unos 24 tests que comprueban tipos de excepción genéricos y deben pasar a comprobar códigos.
- Cambia la forma de las respuestas de error (`code`, `errors[]`); se acepta porque todavía no hay ningún cliente.
- Estimación total: ≈ 6-8 h.
