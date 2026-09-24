# ADR-009 — Línea base de seguridad (Secure by Design)

**Estado:** Aceptado
**Fecha:** 2026-09-24
**Proyecto:** OfiFlow

---

## Contexto

El principio 9 del prompt maestro ("Seguridad desde el diseño") y la sección 35 (Seguridad) enumeran los controles que OfiFlow debe aplicar: autenticación, autorización/RBAC, aislamiento de tenant, rate limiting, validación de entrada, HTTPS, gestión de secretos, logs de auditoría, protección de datos, protección contra IDOR y contra mass assignment.

Con la Fase 1 (MVP) terminada, una revisión del código (2026-09-24) muestra que varios de esos controles **ya existen por diseño** gracias a ADRs anteriores:

| Control (sección 35) | Estado | Dónde |
|---|---|---|
| Inyección SQL | Cubierto — todo el acceso a datos es EF Core + LINQ (consultas parametrizadas); no existe ningún `FromSqlRaw`/`ExecuteSqlRaw` | ADR-001, ADR-006 |
| Tenant isolation / IDOR entre tenants | Cubierto — Global Query Filter por reflexión sobre `ITenantOwned`, tenant tomado del JWT, test de integración obligatorio | ADR-002, ADR-008 |
| Mass assignment de `TenantId` | Cubierto de facto — ningún Command expone `TenantId`; el Handler lo toma de `ITenantContext` | ADR-002 |
| Almacenamiento de credenciales | Cubierto — `PasswordHasher` (PBKDF2), refresh token hasheado con rotación y revocación en cadena | ADR-007 |
| Fuga de detalles internos en errores | Cubierto — `GlobalExceptionHandler` devuelve un 500 genérico | — |
| Arranque inseguro sin secreto | Cubierto — `AddInfrastructure()` falla si falta `Jwt:Secret` | ADR-007 |

Pero la misma revisión encontró **huecos reales**:

1. **Sin rate limiting** en `/api/v1/auth/login`, `/register` y `/refresh` → fuerza bruta de contraseñas sin límite.
2. **Los validadores no limitan longitud** (`MaximumLength`) aunque la BD sí (`HasMaxLength`) → un campo demasiado largo acaba en `DbUpdateException` → 500 en vez de 400; y una contraseña de tamaño arbitrario se hashea igualmente con PBKDF2 (DoS barato).
3. **`GlobalExceptionHandler` no registra nada** → un ataque o un fallo no deja rastro (OWASP A09).
4. **Sin HSTS ni cabeceras de seguridad** en producción.
5. **Nada impide técnicamente** que en el futuro se añada SQL crudo concatenado o un `IgnoreQueryFilters()` sin revisar: hoy la protección depende solo de la disciplina del autor.

## Problema

¿Qué controles de seguridad se fijan como **obligatorios y verificables automáticamente** desde ahora, cuáles se aplazan y con qué criterio, sin sobredimensionar la solución para un único desarrollador (sección "Design for scale, build for current reality")?

## Opciones consideradas

**Opción 1: Revisión manual caso a caso** (checklist de la sección de revisión del prompt maestro en cada feature)
- Ventajas: cero código adicional.
- Desventajas: depende de la memoria del autor; un descuido (un `FromSqlRaw` con interpolación, un `IgnoreQueryFilters()` de más) no lo detecta nadie.

**Opción 2: Plataforma de seguridad completa desde ya** (WAF, Redis para rate limiting distribuido, SIEM, Always Encrypted, pentesting externo)
- Ventajas: postura muy fuerte.
- Desventajas: infraestructura para una escala y un riesgo que todavía no existen; contradice explícitamente el principio de construir para la realidad actual.

**Opción 3: Línea base mínima, aplicada por código y por tests** — controles nativos de .NET 10 sin dependencias nuevas, más "guardarraíles" automáticos (errores de compilación y tests de arquitectura) que convierten las reglas en algo que se rompe si se incumple.
- Ventajas: coste bajo (≈ 8-12 h), cero infraestructura nueva, cada regla tiene un test que la demuestra.
- Desventajas: rate limiting en memoria (no sirve con varias instancias); algunos controles quedan explícitamente aplazados.

## Decisión

Se adopta la **Opción 3**, con estas reglas:

### R1 — Acceso a datos e inyección SQL
- Todo acceso a datos pasa por EF Core + LINQ.
- **Prohibido** `FromSqlRaw`, `ExecuteSqlRaw` y cualquier SQL construido concatenando texto. Si hace falta SQL crudo (solo en Queries, solo tras un profiling que lo justifique — ver memoria de decisión EF Core vs SQL crudo), se usa `FromSql`/`SqlQuery`/`ExecuteSql` con interpolación (EF los convierte en parámetros) o Dapper con parámetros, **pasando siempre `TenantId` explícitamente**.
- Se aplica por compilación: `EF1002` (SQL crudo con interpolación) como error en `Directory.Build.props`, y un test de arquitectura que falla si aparece `FromSqlRaw`/`ExecuteSqlRaw` en `src/`.

### R2 — Aislamiento de tenant (refuerza ADR-002)
- `IgnoreQueryFilters()` solo en una **lista blanca** explícita (hoy: `LoginCommandHandler`, `TokenService`). Un test de arquitectura falla si aparece en cualquier otro fichero; añadir uno nuevo exige actualizar la lista blanca de forma consciente y justificarlo en el código.
- **Ningún Command/Query** (`IRequest`) puede tener una propiedad `TenantId` (protección contra mass assignment). Test de arquitectura por reflexión.

### R3 — Validación de entrada
- Todo Command con campos de texto tiene `MaximumLength` en su Validator, **igual** al `HasMaxLength` de la configuración EF. Para no duplicar números, las longitudes se definen **una vez** como constantes en Domain (ej. `Customer.NameMaxLength`) y las usan tanto la configuración EF como el Validator.
- Contraseña: mínimo 8, **máximo 128** caracteres (NIST SP 800-63B: longitud sobre complejidad; el máximo evita DoS en el hash).
- Superar una longitud devuelve **400**, nunca 500.

### R4 — Rate limiting
- Middleware nativo `Microsoft.AspNetCore.RateLimiting` (sin Redis — sección 38: Redis "solo cuando exista necesidad real"), particionado por IP:
  - `login`: 5 peticiones / minuto
  - `refresh`: 20 peticiones / minuto
  - `register`: 3 peticiones / hora
- Respuesta **429** en formato `ProblemDetails`. Los valores son orientativos y se ajustan con datos reales (Beta).

### R5 — Observabilidad y logs de seguridad
- `GlobalExceptionHandler` registra los 500 con `LogError` (excepción completa en el log, mensaje genérico al cliente).
- Eventos de seguridad con `LogWarning` y campos estructurados: login fallido, reutilización de refresh token revocado (posible robo), rate limit superado.
- **Nunca se registran**: contraseñas, access/refresh tokens, cuerpos de petición, ni datos personales de Customer (email, teléfono, dirección, notas). Se registran **Ids** (`UserId`, `TenantId`, `EntityId`) y la IP.
- `EnableSensitiveDataLogging()` de EF Core prohibido fuera de un depurado local puntual (nunca commiteado).

### R6 — Transporte y cabeceras
- Fuera de Development: `UseHsts()`.
- En todas las respuestas: `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, `Content-Security-Policy: default-src 'none'; frame-ancestors 'none'` (es una API JSON, no sirve HTML).

### R7 — Dependencias
- `NU1903`/`NU1904` (paquete con vulnerabilidad alta/crítica, NuGetAudit) como error de compilación en `Directory.Build.props`.

### R8 — Secretos
- En producción, `Jwt:Secret` y la cadena de conexión llegan por variables de entorno o Key Vault — nunca en `appsettings.json`. `appsettings.json` sigue sin sección `Jwt` para que un despliegue mal configurado falle al arrancar.
- En local, `Jwt:Secret` vive en **`dotnet user-secrets`** (`UserSecretsId` en `OfiFlow.Api.csproj`), fuera del repositorio. **Decidido y aplicado el 2026-09-24.** Hasta entonces, `appsettings.Development.json` contenía un secreto de desarrollo commiteado, lo que chocaba con la sección 35. Ese secreto sigue en el historial de git, así que se **rotó**: el valor actual es nuevo y nunca se ha commiteado. El antiguo no protege nada, porque solo firmó tokens de una base LocalDB de desarrollo, y por eso no se reescribe el historial.
- Si falta `Jwt:Secret`, la aplicación no arranca y el error indica el comando exacto para configurarlo.

## Consecuencias

### Positivas
- Cada regla de R1-R3 y R7 **se rompe sola** (compilación o test) si se incumple: la seguridad deja de depender de acordarse.
- Cierra los 5 huecos detectados con controles nativos de .NET 10, sin infraestructura nueva.
- Documenta de forma explícita lo que ya estaba cubierto por ADRs anteriores (útil para una auditoría futura, RGPD o un cliente que pregunte "¿cómo protegéis mis datos?").

### Negativas / riesgos aceptados
- El rate limiting en memoria se reinicia con cada despliegue y no se comparte entre instancias. Se acepta mientras haya una sola instancia.
- Detrás de un proxy inverso, la IP real exige `UseForwardedHeaders` configurado con proxies de confianza; si no, todos los clientes compartirían la IP del proxy. Se resuelve al preparar el despliegue.
- Los tests de arquitectura por escaneo de texto son simples y pueden dar falsos positivos (por ejemplo, un comentario que mencione `FromSqlRaw`). Se acepta por su coste casi nulo.

### Qué queda cerrado (no se debe reabrir sin una razón nueva y explícita)
- No usar `FromSqlRaw`/`ExecuteSqlRaw` ni SQL concatenado.
- No aceptar `TenantId` desde el cliente en ningún Command/Query.
- No introducir Redis ni un WAF solo por seguridad mientras haya una única instancia.

### Qué queda abierto para revisar más adelante
- **NEXT — RBAC dentro del tenant:** `AuthorizationBehavior` (previsto en ADR-003, no implementado) que compruebe `TenantUser.Role` por Command. Se vuelve necesario en cuanto un tenant tenga más de un usuario (invitaciones). Sigue abierto el punto de ADR-004/007 sobre permisos granulares.
- **NEXT — Enumeración de usuarios:** igualar el tiempo del login cuando el email no existe (verificar contra un hash ficticio) y revisar el mensaje "El email ya está registrado" del registro cuando exista confirmación de email.
- **NEXT — CORS** restringido al origen del frontend Angular, cuando exista.
- **NEXT — Lockout por cuenta** (además del rate limiting por IP), junto con la adopción de `UserManager` que ADR-007/spec 002 dejaron en backlog.
- **LATER — Log de auditoría persistente** (sección 36: User, Tenant, Action, Entity, EntityId, Timestamp, IP, Metadata), prioritario cuando existan Facturas y Pagos.
- **LATER — RGPD/LOPDGDD:** cifrado en reposo (TDE del proveedor SQL), derecho de supresión y exportación de datos del Customer, registro de actividades de tratamiento.
- **LATER — SAST/DAST en CI** (CodeQL o Semgrep, OWASP ZAP) cuando se monte el pipeline.
- **LATER — Seguridad de IA** (sección 35: prompt injection, validación de herramientas), cuando empiece la fase de IA/WhatsApp.

---

## Relación con otros ADR

- Refuerza: ADR-002 (Multi-Tenancy — convierte en test la lista blanca de `IgnoreQueryFilters()` y la ausencia de `TenantId` en Commands).
- Depende de: ADR-003 (el futuro `AuthorizationBehavior` es un Pipeline Behavior de MediatR).
- Depende de: ADR-006 (FluentValidation + `ValidationBehavior` es donde viven las reglas de R3).
- Depende de: ADR-007 (los logs de seguridad de R5 cubren la detección de reutilización de refresh token ya implementada).
- Afecta a: ADR-001 (concreta las condiciones del uso excepcional de SQL crudo).
