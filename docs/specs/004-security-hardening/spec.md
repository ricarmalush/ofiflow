# Spec — 004-security-hardening

**Estado:** Borrador
**Fecha:** 2026-09-24
**Bounded Context:** Transversal (Api + Application + Infrastructure; toca Identity, Customers y Jobs)
**Depende de:** ADR-009 (línea base de seguridad — esta spec implementa su parte NOW), ADR-002, ADR-003, ADR-006, ADR-007, `specs/002-tenancy-identity/`

---

## Objetivo

Cerrar los 5 huecos de seguridad detectados en la revisión del MVP (2026-09-24) **antes** de que un cliente piloto meta datos reales de sus clientes. OfiFlow va a guardar datos personales (nombre, email, teléfono y dirección de los clientes de cada profesional). Una fuga, o una cuenta comprometida por fuerza bruta, destruiría la confianza en el producto antes de tener el primer cliente de pago. No añade funcionalidad visible: protege el flujo de valor completo (cliente → trabajo → …).

## Alcance — Qué SÍ incluye

- **Rate limiting** en los tres endpoints anónimos de autenticación (`login`, `refresh`, `register`), con respuesta 429 (ADR-009 R4).
- **Límites de longitud** en todos los Validators de Commands con texto, alineados con la BD mediante constantes definidas una sola vez en Domain (R3).
- **Logging** de errores 500 y de eventos de seguridad, sin datos sensibles (R5).
- **HSTS y cabeceras de seguridad** HTTP (R6).
- **Guardarraíles automáticos:**
  - `EF1002`, `NU1903` y `NU1904` como errores de compilación (R1, R7).
  - Tests de arquitectura: sin `FromSqlRaw`/`ExecuteSqlRaw`, `IgnoreQueryFilters()` solo en la lista blanca, ningún `IRequest` con `TenantId` (R1, R2).

## Alcance — Qué NO incluye

- **NOW** (necesario ahora, pero fuera de esta spec): decidir qué hacer con el secreto JWT de desarrollo commiteado (ADR-009 R8). Es una decisión del autor; si se elige `user-secrets`, es una tarea de 15 minutos que puede añadirse al final de esta misma lista.
- **NEXT:**
  - `AuthorizationBehavior` / RBAC por rol dentro del tenant (en cuanto exista la invitación de usuarios).
  - Igualar el tiempo de respuesta del login para evitar la enumeración de emails.
  - CORS para Angular.
  - Lockout por cuenta.
- **LATER:**
  - Log de auditoría persistente (sección 36).
  - RGPD: cifrado en reposo, derecho de supresión y exportación.
  - SAST/DAST en CI.
  - Seguridad de IA.
  - Rate limiting distribuido (Redis).
- **NEVER** (para esta fase): WAF propio, Always Encrypted columna a columna, pentest externo de pago. Ninguno aporta valor proporcional con cero clientes.

## Reglas / invariantes

- Una petición con un campo de texto más largo que su límite devuelve **400** con un mensaje de validación, **nunca 500**.
- Una contraseña de más de 128 caracteres se rechaza en el Validator, **antes** de llegar a `PasswordHasher`.
- Superar el límite de un endpoint de autenticación devuelve **429** `ProblemDetails`, sin ejecutar el Handler.
- Un error 500 deja una entrada `Error` en el log con la excepción completa, y el cliente sigue recibiendo solo el mensaje genérico.
- Ningún log contiene contraseñas, tokens, cuerpos de petición ni email, teléfono, dirección o notas de un Customer.
- Las respuestas de la API incluyen las cabeceras de seguridad de ADR-009 R6; fuera de Development, también HSTS.
- El build falla si:
  - aparece SQL crudo con interpolación en `FromSqlRaw` (EF1002);
  - o un paquete NuGet tiene una vulnerabilidad alta o crítica.
- Los tests de arquitectura fallan si:
  - aparece `FromSqlRaw` o `ExecuteSqlRaw` en `src/`;
  - aparece `IgnoreQueryFilters()` fuera de `LoginCommandHandler` y `TokenService`;
  - algún tipo que implemente `IRequest`/`IRequest<T>` tiene una propiedad `TenantId`.

## Modelo conceptual

No hay entidades ni migraciones nuevas. Solo se añaden **constantes de longitud** a los aggregates existentes, como fuente única para EF y los Validators:

```
Customer: NameMaxLength = 200, EmailMaxLength = 320, PhoneMaxLength = 20,
          AddressMaxLength = 500, NotesMaxLength = 2000
Job:      TitleMaxLength = 200, DescriptionMaxLength = 2000
Tenant:   NameMaxLength = 200
User:     NameMaxLength = 200, EmailMaxLength = 320
Password (Application, no es Domain): MinLength = 8, MaxLength = 128
```

Los valores son los que ya existen hoy en las configuraciones EF: el esquema de BD **no cambia**.

## Criterios de aceptación

- Dado un `CreateCustomerCommand` con `Name` de 201 caracteres, cuando se envía, entonces se obtiene 400 (no 500) y no se escribe nada en BD. Lo mismo para cada campo con límite de Customer, Job, Register y los Update.
- Dado un `RegisterCommand` con contraseña de 129 caracteres, cuando se envía, entonces se obtiene 400 y `PasswordHasher` no llega a ejecutarse.
- Dada una misma IP, cuando hace 6 peticiones a `/api/v1/auth/login` en menos de un minuto, entonces la 6.ª recibe 429 `ProblemDetails`.
- Dada una IP que recibió 429, cuando pasa la ventana de tiempo, entonces vuelve a poder hacer login.
- Dado un Handler que lanza una excepción no controlada, cuando se procesa la petición, entonces el cliente recibe 500 con "Ha ocurrido un error inesperado." y el log contiene una entrada `Error` con la excepción.
- Dado un refresh token revocado que se reutiliza, cuando se llama a `/refresh`, entonces se registra un `Warning` con `UserId`, `TenantId` e IP, y **sin** el valor del token.
- Dado un login fallido, cuando se registra en el log, entonces aparece la IP pero **no** el email en claro ni la contraseña.
- Dada cualquier respuesta de la API, cuando se inspeccionan sus cabeceras, entonces incluye `X-Content-Type-Options`, `X-Frame-Options`, `Referrer-Policy` y `Content-Security-Policy`.
- Dado un `FromSqlRaw` o un `IgnoreQueryFilters()` añadido fuera de la lista blanca, cuando se ejecuta `dotnet test`, entonces el test de arquitectura correspondiente falla con un mensaje que indica el fichero.

**Caso multi-tenant obligatorio:** esta spec no crea datos nuevos, pero **refuerza** el aislamiento:
- el test de arquitectura sobre `IgnoreQueryFilters()` y sobre la ausencia de `TenantId` en Commands convierte en automático lo que hasta hoy era disciplina;
- los tests de aislamiento existentes (`TenantIsolationIntegrationTests`) deben seguir en verde.

## Decisiones técnicas relevantes

- Se apoya en: **ADR-009** (todas las reglas), ADR-006 (FluentValidation + `ValidationBehavior`), ADR-007 (detección de reutilización de refresh token, ya implementada, que ahora se registra en el log).
- Rate limiting con el middleware nativo `Microsoft.AspNetCore.RateLimiting`: sin paquetes nuevos, `FixedWindowLimiter` particionado por `RemoteIpAddress`.
- Cabeceras de seguridad con un middleware propio de pocas líneas en `OfiFlow.Api/Common/`, sin paquete de terceros.
- Tests de arquitectura en `OfiFlow.Api.Tests`, el único proyecto de tests que ve todos los ensamblados. Se hacen con escaneo de ficheros `.cs` y reflexión, sin añadir NetArchTest.
- Tests de rate limiting con `WebApplicationFactory` (`Microsoft.AspNetCore.Mvc.Testing`, primera dependencia de `OfiFlow.Api.Tests`). Se envían peticiones de login con cuerpo inválido: `ValidationBehavior` responde 400 sin tocar la BD, así que el test no necesita SQL Server, y la 6.ª debe dar 429.
- ¿Requiere una ADR nueva? Sí: **ADR-009**, redactado a la vez que esta spec.

## Fuera de alcance / Backlog relacionado

- Todo lo marcado NEXT/LATER en ADR-009, "Qué queda abierto".
- `UseForwardedHeaders` con proxies de confianza: se configura al preparar el primer despliegue real (si no, el rate limiting por IP vería solo la IP del proxy).

---

## Validación antes de aprobar

> ¿Esta funcionalidad ayuda a conseguir o mantener al primer cliente? (sección 84 del prompt maestro)

**Sí, indirectamente pero de forma crítica.**
- El primer piloto va a introducir datos personales reales de sus clientes, y está sujeto al RGPD.
- Una cuenta comprometida por fuerza bruta o una fuga de datos antes o durante el piloto acabaría con el producto.
- Coste acotado: unas 8-12 h, sin infraestructura nueva ni cambios de esquema.
- Hacerlo ahora, con 5 aggregates, es mucho más barato que añadir `MaximumLength` y guardarraíles cuando haya 15.
