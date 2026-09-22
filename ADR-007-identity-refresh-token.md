# ADR-007 — Modelo Identity y Refresh Token

**Estado:** Aceptado
**Fecha:** 2026-09-20
**Proyecto:** OfiFlow

---

## Contexto

ADR-004 ya decidió usar ASP.NET Core Identity para la gestión base de usuarios, modelar el rol como campo propio en `TenantUser` (no en `AspNetRoles`), y usar JWT como mecanismo de autenticación de la API. Esa misma ADR dejó explícitamente abiertos dos puntos que afectan el esquema de la primera migración: la relación entre el `User` de Domain y el `ApplicationUser` de Identity, y la estrategia exacta de refresh token.

## Problema

¿Cómo convive el `User` de Domain (independiente de frameworks, principio 6 del prompt maestro) con `ApplicationUser` de ASP.NET Core Identity, y qué estrategia de refresh token se usa para no gestionar la expiración del JWT solo con reintentos de login?

## Opciones consideradas

### Relación entre Domain.User e Identity

**Opción 1: `Domain.User` hereda de `IdentityUser<Guid>`**
- Ventajas: menos código, una sola tabla.
- Desventajas: Domain pasaría a depender del paquete `Microsoft.AspNetCore.Identity`, violando el principio de que Domain sea independiente de frameworks externos (sección 6 del prompt maestro, ya aplicado en ADR-005/ADR-006).

**Opción 2: `Domain.User` y `ApplicationUser` son entidades separadas que comparten el mismo `Id` (`Guid`)**
- Ventajas: Domain permanece libre de dependencias de Identity; `ApplicationUser` (en Infrastructure) gestiona exclusivamente credenciales (password hash, email confirmado, lockout); `Domain.User` gestiona exclusivamente datos de negocio (nombre, preferencias).
- Desventajas: registrar un usuario implica crear dos filas relacionadas por el mismo Id, en la misma transacción.

### Estrategia de refresh token

**Opción 1: Sin refresh token — el usuario vuelve a hacer login cuando el JWT expira**
- Ventajas: cero código adicional.
- Desventajas: mala experiencia de usuario si el JWT tiene una vida corta (necesaria por seguridad); si tiene vida larga para evitar logins frecuentes, aumenta la ventana de riesgo si el token se compromete.

**Opción 2: Refresh token de larga duración, sin rotación, almacenado en claro**
- Ventajas: simple de implementar.
- Desventajas: si se filtra un refresh token, sirve indefinidamente hasta su expiración; no hay forma de detectar un uso indebido.

**Opción 3: Refresh token con rotación y almacenamiento hasheado**
- Ventajas: cada uso invalida el token anterior y emite uno nuevo, lo que permite detectar reutilización de un token robado (si un token ya usado se presenta de nuevo, se revoca toda la cadena); el valor se guarda hasheado, igual que una contraseña, así que una fuga de la base de datos no expone tokens usables directamente.
- Desventajas: algo más de lógica que la opción 2 (comparar y rotar en cada refresh).

## Decisión

Se decide:
- **`Domain.User` y `ApplicationUser` son entidades separadas**, relacionadas por compartir el mismo `Id` (`Guid`). `ApplicationUser` (que hereda de `IdentityUser<Guid>`) vive en `Infrastructure/Identity` y gestiona únicamente credenciales. `Domain.User` vive en `Domain/Identity` y gestiona únicamente datos de negocio de la persona. El registro de un usuario crea ambas filas en la misma transacción.
- **Refresh token con rotación**, mediante una entidad `RefreshToken` (`Id`, `UserId`, `TokenHash`, `ExpiresAt`, `RevokedAt`) en `Infrastructure/Identity`. Cada uso del refresh token emite uno nuevo y revoca el anterior; si se presenta un token ya revocado, se revoca toda la cadena de tokens de ese usuario como medida de contención.
- El JWT de acceso tiene una vida corta (a definir en implementación, orientativamente 15-60 minutos); el refresh token tiene una vida más larga (orientativamente varios días), sujeta a ajuste con datos reales de uso.

## Consecuencias

### Positivas
- Domain permanece completamente libre de dependencias de `Microsoft.AspNetCore.Identity`, consistente con el principio 6 del prompt maestro y con ADR-005/ADR-006.
- El mecanismo de rotación de refresh tokens detecta y contiene el uso de un token robado, sin necesidad de infraestructura adicional (no requiere Redis ni bases de datos separadas).
- Un JWT de vida corta limita la ventana de riesgo si un access token se compromete, sin perjudicar la experiencia de usuario gracias al refresh token.

### Negativas / riesgos aceptados
- Registrar un usuario requiere coordinar dos escrituras (`ApplicationUser` + `Domain.User`) en la misma transacción; una migración futura de Identity a otro proveedor tendría que revisar esta relación.
- La rotación de refresh tokens añade una tabla y algo de lógica adicional comparado con no tener refresh token; se acepta porque la alternativa (sin refresh token, o con un token de larga duración sin rotación) implica peor experiencia de usuario o peor postura de seguridad.

### Qué queda cerrado (no se debe reabrir sin una razón nueva y explícita)
- No hacer que `Domain.User` herede de `IdentityUser` ni de ningún tipo del paquete de Identity.
- No usar un refresh token sin rotación ni almacenado en claro.

### Qué queda abierto para revisar más adelante
- Duración exacta del JWT de acceso y del refresh token — ajustar con datos reales de uso durante la fase Beta (sección 69 del prompt maestro).
- Mecanismo de revocación manual de sesiones (ej. "cerrar sesión en todos los dispositivos") — no necesario para el MVP inicial.
- Si se necesitan permisos granulares (`Customers.Read`, `Jobs.Write`, etc.) además del rol en `TenantUser`, y cómo convivirían con este modelo — sigue abierto desde ADR-004, sin cambios.

---

## Relación con otros ADR

- Depende de: ADR-004 (Identity — usa ASP.NET Core Identity, `TenantUser.Role` y JWT ya decididos allí; esta ADR cierra los dos puntos que ADR-004 dejó explícitamente abiertos).
- Depende de: ADR-005 (Modular Monolith — `Domain.User` e Identity se ubican en sus carpetas de contexto correspondientes).
- Afecta a: ADR-002 (Multi-Tenancy — el `Id` compartido entre `Domain.User` y `ApplicationUser` es el mismo que referencia `TenantUser.UserId`).
