# Documentación de OfiFlow

| Carpeta | Qué contiene | Cuándo se crea un fichero nuevo |
|---|---|---|
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
| [010](./adr/ADR-010-pipeline-devsecops.md) | Pipeline DevSecOps y herramientas de seguridad | Propuesto |

## Specs

| Spec | Feature | Estado |
|---|---|---|
| [001-customer](./specs/001-customer/spec.md) | CRUD de clientes | Implementada |
| [002-tenancy-identity](./specs/002-tenancy-identity/spec.md) | Tenant, usuarios, registro, login y JWT | Implementada |
| [003-job](./specs/003-job/spec.md) | Trabajos y transiciones de estado | Implementada |
| [004-security-hardening](./specs/004-security-hardening/spec.md) | Línea base de seguridad (implementa ADR-009) | Aprobada (en curso) |
| [005-devsecops-pipeline](./specs/005-devsecops-pipeline/spec.md) | Repositorio público y pipeline CI de seguridad (implementa ADR-010) | Borrador |

Plantillas para specs nuevas: [TEMPLATE-spec.md](./specs/TEMPLATE-spec.md) y [TEMPLATE-tasks.md](./specs/TEMPLATE-tasks.md).

**Al crear una ADR o una spec nueva, añádela a estas tablas.**
