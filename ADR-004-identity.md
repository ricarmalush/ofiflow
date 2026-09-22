# ADR-004 — Identity

**Estado:** Aceptado
**Fecha:** 2027-01 (ajustar a la fecha exacta en que se cierre)
**Proyecto:** OfiFlow

---

## Contexto

OfiFlow necesita autenticación y gestión de usuarios desde el primer commit. Un mismo usuario (`User`) puede pertenecer a varios `Tenant` con un rol distinto en cada uno (ej. Owner en Tenant A, Technician en Tenant B). La API será consumida por Angular, .NET MAUI, y por la capa de IA/WhatsApp, todos con el mismo backend.

## Problema

¿Qué framework de autenticación usar, cómo modelar los roles cuando dependen del tenant (no del usuario global), y qué mecanismo de autenticación usar para una API consumida por múltiples clientes (web, móvil, IA)?

## Opciones consideradas

### Framework de autenticación

**ASP.NET Core Identity**
- Ventajas: integrado de forma nativa en .NET, maneja hashing de contraseñas, tokens de confirmación, recuperación de contraseña, etc. sin reinventar nada.
- Desventajas: sus roles nativos (`AspNetRoles`) son globales por usuario, no por tenant.

### Modelo de roles

**Opción 1: Usar AspNetRoles directamente**
- Ventajas: aprovecha la infraestructura nativa de Identity sin trabajo extra.
- Desventajas: no soporta que un usuario tenga un rol distinto en cada tenant — limitación real dado que un usuario puede ser Owner en un tenant y Technician en otro.

**Opción 2: Rol como campo propio en TenantUser**
- Ventajas: el rol se modela junto a la pertenencia usuario-tenant, que es donde conceptualmente vive (`TenantUser.Role`); soporta de forma natural que un mismo usuario tenga roles distintos en cada tenant.
- Desventajas: no se aprovecha el sistema de roles nativo de Identity para autorización; hay que construir el chequeo de autorización sobre este campo propio en vez de sobre `[Authorize(Roles = "...")]` nativo.

### Mecanismo de autenticación para la API

**Opción 1: JWT**
- Ventajas: sin estado en servidor, funciona igual para Angular, MAUI y llamadas desde la capa de IA/WhatsApp; estándar para APIs consumidas por múltiples clientes distintos.
- Desventajas: requiere gestión propia de expiración/refresh de tokens.

**Opción 2: Cookies de sesión clásicas de Identity**
- Ventajas: integración más directa con ASP.NET Core Identity out-of-the-box.
- Desventajas: pensado para aplicaciones web con navegador; no es el mecanismo natural para un cliente móvil nativo (MAUI) ni para llamadas desde servicios de IA/WhatsApp.

## Decisión

Se decide:
- Usar **ASP.NET Core Identity** para la gestión base de usuarios (registro, hashing de contraseñas, recuperación de cuenta).
- El **rol se modela como campo propio en `TenantUser`**, no en `AspNetRoles`. La autorización se construye sobre este campo, combinado con el `TenantContext` (usuario + tenant activo → rol efectivo en ese tenant).
- Usar **JWT** como mecanismo de autenticación para la API, consumida por igual desde Angular, MAUI y la capa de IA/WhatsApp.

## Consecuencias

### Positivas
- Un mismo usuario puede tener roles distintos en cada tenant sin ninguna limitación artificial.
- Un único mecanismo de autenticación (JWT) sirve a todos los clientes (web, móvil, IA) sin necesidad de mecanismos distintos por canal.

### Negativas / riesgos aceptados
- Se renuncia a `[Authorize(Roles = "...")]` nativo de Identity; hay que construir un mecanismo de autorización propio basado en `TenantUser.Role` + `TenantContext`, con el trabajo de desarrollo y testing que eso implica.
- Gestión propia de expiración y refresh de tokens JWT.

### Qué queda cerrado (no se debe reabrir sin una razón nueva y explícita)
- No usar `AspNetRoles` para modelar el rol del usuario dentro de un tenant.
- No usar cookies de sesión como mecanismo de autenticación de la API.

### Qué queda abierto para revisar más adelante
- Estrategia exacta de refresh token (duración, rotación, revocación).
- Si se necesitan permisos granulares (`Customers.Read`, `Jobs.Write`, etc., mencionados en el prompt maestro) además del rol, y cómo conviven con `TenantUser.Role`.

---

## Relación con otros ADR

- Depende de: ADR-005 (Modular Monolith) — Identity se ubica en su propia carpeta de contexto dentro de Domain/Application.
- Afecta a: ADR-002 (Multi-Tenancy) — el modelo `User / Tenant / TenantUser` con `Role` en `TenantUser` es la base de la autorización multi-tenant.
