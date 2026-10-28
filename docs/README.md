# Documentación de OfiFlow

| Carpeta | Qué contiene | Cuándo se crea un fichero nuevo |
|---|---|---|
| [`manual/`](./manual/) | Manual técnico para programadores junior: de qué va la aplicación y cómo está construida | Al escribir o actualizar un capítulo; cuando cambia algo que el manual explica |
| [`producto/`](./producto/) | Especificación completa del producto y calendario | Casi nunca: son los documentos maestros |
| [`adr/`](./adr/) | Decisiones de arquitectura (Architecture Decision Records) | Cuando una feature necesita una decisión técnica que ninguna ADR cubre (paso **Plan** del ciclo SDD) |
| [`specs/`](./specs/) | Una carpeta por feature con `spec.md` + `tasks.md` | Al empezar cada feature (pasos **Specify** y **Tasks** del ciclo SDD) |

## Producto

- [ProyectoOFIFLOW.txt](./producto/ProyectoOFIFLOW.txt): prompt maestro, unas 105 secciones
- [Calendario del Proyecto.txt](./producto/Calendario%20del%20Proyecto.txt): roadmap 2027-2028

## ADRs

| ADR | Título | Estado |
|---|---|---|
| [001](./adr/ADR-001-motor-base-datos.md) | Motor de base de datos | Aceptado |
| [002](./adr/ADR-002-multi-tenancy.md) | Multi-Tenancy | Aceptado |
| [003](./adr/ADR-003-cqrs.md) | CQRS | Aceptado |
| [004](./adr/ADR-004-identity.md) | Identity | Aceptado |
| [005](./adr/ADR-005-modular-monolith.md) | Modular Monolith | Aceptado |
| [006](./adr/ADR-006-persistencia-domain-events.md) | Persistencia con EF Core y Domain Events | Aceptado |
| [007](./adr/ADR-007-identity-refresh-token.md) | Modelo Identity y Refresh Token | Aceptado |
| [008](./adr/ADR-008-testing-integracion.md) | Estrategia de tests de integración contra SQL Server | Aceptado |
| [009](./adr/ADR-009-seguridad-base.md) | Línea base de seguridad (Secure by Design) | Aceptado |
| [010](./adr/ADR-010-pipeline-devsecops.md) | Pipeline DevSecOps y herramientas de seguridad | Aceptado |
| [011](./adr/ADR-011-errores-codigos-diccionario.md) | Errores de dominio, códigos de error y diccionario de mensajes | Aceptado |
| [012](./adr/ADR-012-autorizacion-rbac.md) | Autorización por permisos (RBAC) | Aceptado |
| [013](./adr/ADR-013-gestion-de-usuarios.md) | Gestión de usuarios de una empresa (invitaciones, jerarquía de roles, revalidación por petición) | Aceptado |
| [014](./adr/ADR-014-mfa-y-bloqueo-de-cuenta.md) | Segundo factor (MFA) y bloqueo de cuenta | Aceptado |
| [015](./adr/ADR-015-logs-a-fichero-con-serilog.md) | Logs a fichero con Serilog | Aceptado |
| [016](./adr/ADR-016-panel-de-operador-de-la-plataforma.md) | Panel de operador de la plataforma (identidad aparte, sesión propia, suspensión de empresas) | Aceptado |
| [017](./adr/ADR-017-cors-para-el-frontend-web.md) | CORS para el frontend web (lista cerrada de orígenes, en Program.cs) | Propuesto |

## Specs

| Spec | Feature | Estado |
|---|---|---|
| [001-customer](./specs/001-customer/spec.md) | CRUD de clientes | Implementada |
| [002-tenancy-identity](./specs/002-tenancy-identity/spec.md) | Tenant, usuarios, registro, login y JWT | Implementada |
| [003-job](./specs/003-job/spec.md) | Trabajos y transiciones de estado | Implementada |
| [004-security-hardening](./specs/004-security-hardening/spec.md) | Línea base de seguridad (implementa ADR-009) | Implementada |
| [005-devsecops-pipeline](./specs/005-devsecops-pipeline/spec.md) | Repositorio público y pipeline CI de seguridad (implementa ADR-010) | Aprobada (en curso) |
| [006-error-codes-dictionary](./specs/006-error-codes-dictionary/spec.md) | Códigos de error y diccionario de mensajes (implementa ADR-011) | Implementada |
| [007-tenant-isolation-hardening](./specs/007-tenant-isolation-hardening/spec.md) | Guardarraíles y tests que endurecen el aislamiento entre empresas (amplía ADR-009 R2) | Implementada |
| [008-rbac-authorization](./specs/008-rbac-authorization/spec.md) | Autorización por rol: comprobar los permisos de cada operación (implementa ADR-012) | Implementada |
| [009-user-management](./specs/009-user-management/spec.md) | Invitar y gestionar usuarios de una empresa: invitaciones, cambio de rol y baja con efecto inmediato (implementa ADR-013) | Implementada |
| [010-mfa-and-account-lockout](./specs/010-mfa-and-account-lockout/spec.md) | Segundo factor (TOTP), login en dos pasos y bloqueo de cuenta por intentos (implementa ADR-014) | Implementada |
| [011-platform-operator-panel](./specs/011-platform-operator-panel/spec.md) | Panel de operador de la plataforma: listar, suspender y reactivar empresas con una identidad aparte y segundo factor obligatorio (implementa ADR-016) | Implementada |
| [012-account-me](./specs/012-account-me/spec.md) | «Quién soy»: `GET /account/me` con los datos de la propia cuenta, su empresa, su rol efectivo y sus permisos (amplía ADR-012) | Implementada |
| [013-server-logout](./specs/013-server-logout/spec.md) | Cierre de sesión en el servidor: `POST /auth/logout` revoca el refresh token (amplía ADR-007) | Implementada (API; el frontend es el bloque 5) |
| [014-logout-all](./specs/014-logout-all/spec.md) | Cerrar todas mis sesiones: `DELETE /account/sessions` revoca todos los refresh tokens de la cuenta (amplía ADR-007 y ADR-012) | Implementada (API; el frontend es el bloque 5) |

Plantillas para specs nuevas: [TEMPLATE-spec.md](./specs/TEMPLATE-spec.md) y [TEMPLATE-tasks.md](./specs/TEMPLATE-tasks.md).

**Al crear una ADR o una spec nueva, añádela a estas tablas.**
