# Spec — 002-tenancy-identity

**Estado:** Aprobada
**Fecha:** 2026-09-21
**Bounded Context:** Identity, Tenancy
**Depende de:** ADR-002 (Multi-Tenancy), ADR-004 (Identity), ADR-006 (Persistencia y Domain Events), ADR-007 (Modelo Identity y Refresh Token)

---

## Objetivo

Permitir que un profesional se registre (creando su empresa/Tenant) y haga login, obteniendo un JWT con el tenant activo — el requisito imprescindible para que cualquier otro endpoint del sistema (empezando por `Customer`, bloqueado en `specs/001-customer/tasks.md`) pueda resolver `TenantContext` y aplicar el aislamiento de tenant de verdad.

## Alcance — Qué SÍ incluye

- Registro: crea `Tenant` + `User` (Domain) + `ApplicationUser` (Identity) + `TenantUser` con rol `Owner`, en una sola transacción.
- Login: valida credenciales y emite Access Token (JWT con `tenant_id`) + Refresh Token.
- Refresh: intercambia un Refresh Token válido por un nuevo Access Token, con rotación (ADR-007).
- Protección de endpoints (`[Authorize]`) y resolución de `TenantContext` a partir del JWT en cada request.

## Alcance — Qué NO incluye

- **NEXT** (en cuanto un usuario pueda pertenecer a varios tenants): endpoint para cambiar de tenant activo (`switch-tenant`, ya previsto conceptualmente en ADR-002).
- **NEXT**: invitar a otros usuarios a un tenant existente con roles distintos de Owner (Admin, Manager, Technician, Employee) — hoy el registro solo cubre al primer Owner.
- **LATER**: recuperación de contraseña, confirmación de email, 2FA.
- **NEVER** (por ahora): permisos granulares además del `Role` — ya marcado como backlog en ADR-004/007, sin necesidad real todavía.

## Reglas de negocio / invariantes

- Un `Tenant` se crea siempre con exactamente un `TenantUser` inicial con rol `Owner` (el que se registra).
- El email de login es único (lo garantiza `ApplicationUser`/Identity).
- El Access Token vive poco tiempo (orientativo 15-60 min); el Refresh Token más (orientativo varios días), con rotación en cada uso.
- Un Refresh Token ya usado que se reutiliza revoca toda la cadena de tokens del usuario (detección de robo — ADR-007).
- El `tenant_id` del JWT nunca se acepta como dato de entrada del cliente sin más: se valida en cada request que el usuario autenticado sigue siendo `TenantUser` de ese tenant.
- Un login con credenciales incorrectas no debe revelar si falló el email o la contraseña.

## Modelo conceptual

```
Tenant (AggregateRoot, IAuditable)                    [Domain/Tenancy]
- Id, Name, CreatedAt, UpdatedAt

TenantUser (AggregateRoot, ITenantOwned, IAuditable)  [Domain/Tenancy]
- Id, TenantId, UserId, Role (Owner | Admin | Manager | Technician | Employee)

User (AggregateRoot, IAuditable)                      [Domain/Identity]
- Id (= mismo Id que ApplicationUser), Name, ContactEmail, CreatedAt, UpdatedAt

ApplicationUser : IdentityUser<Guid>                  [Infrastructure/Identity]
- credenciales únicamente (password hash, email de login, lockout)

RefreshToken                                          [Infrastructure/Identity]
- Id, UserId, TokenHash, ExpiresAt, RevokedAt
```

## Criterios de aceptación

- Dado un email no registrado, cuando se registra con nombre de empresa + datos de usuario válidos, entonces se crean `Tenant`, `User`, `ApplicationUser` y `TenantUser` (rol `Owner`) en una sola transacción.
- Dado un email ya registrado, cuando se intenta registrar de nuevo, entonces se rechaza.
- Dado un usuario registrado, cuando hace login con credenciales correctas, entonces recibe un Access Token (JWT con claim `tenant_id`) y un Refresh Token.
- Dado credenciales incorrectas, cuando se hace login, entonces se rechaza sin indicar cuál de los dos datos falló.
- Dado un Refresh Token válido, cuando se usa, entonces se emite un nuevo Access Token + Refresh Token, y el anterior queda revocado.
- Dado un Refresh Token ya revocado, cuando se reutiliza, entonces se rechaza y se revoca toda la cadena de tokens del usuario.
- Dado un JWT válido de este flujo, cuando se llama a un endpoint protegido (p. ej. `GET /api/v1/customers`), entonces `TenantContext.TenantId` resuelve correctamente y el Global Query Filter de ADR-002/006 se aplica sin excepciones.
- Dado un endpoint protegido, cuando se llama sin JWT o con uno inválido/expirado, entonces responde 401.

## Decisiones técnicas relevantes

- Se apoya en: ADR-002 (tenant activo en el JWT), ADR-004 (ASP.NET Core Identity + rol en `TenantUser`), ADR-006 (Guid plano, `ITenantOwned`, `IAuditable`), ADR-007 (`Domain.User`/`ApplicationUser` separados, Refresh Token con rotación).
- ¿Requiere una ADR nueva? No — es la implementación práctica de ADR-004 y ADR-007, ninguna decisión arquitectónica nueva.

## Fuera de alcance / Backlog relacionado

- Cambio de tenant activo (`switch-tenant`).
- Invitar usuarios a un tenant existente.
- Recuperación de contraseña, confirmación de email, 2FA.
- Permisos granulares además del Role.

---

## Validación antes de aprobar

> ¿Esta funcionalidad ayuda a conseguir o mantener al primer cliente?

Sí — es un bloqueante absoluto. Sin esto, ningún endpoint del sistema (incluido `Customer`, ya construido y esperando) puede probarse ni usarse con una petición HTTP real.
