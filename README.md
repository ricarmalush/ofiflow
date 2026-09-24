# OfiFlow

## Qué es

OfiFlow es una plataforma SaaS para **autónomos, técnicos y pequeñas empresas de servicios** (fontanería, electricidad, climatización, mantenimiento, reparaciones, informática, jardinería, construcción y otros servicios profesionales).

Propuesta de valor:

> **"Convierte tu trabajo diario en un flujo automático: cliente → trabajo → presupuesto → cita → factura → cobro."**

OfiFlow no quiere ser "otro CRM". Su diferenciador es usar **IA para reducir al mínimo el trabajo administrativo**: el profesional puede interactuar por texto, voz, WhatsApp, fotos o documentos, y la IA interpreta la intención y ejecuta acciones sobre el sistema respetando siempre tenant, usuario, rol, permisos, reglas de negocio y auditoría.

Ejemplos de lo que debe poder hacer:

- *"Créame un trabajo para Juan, tiene una fuga debajo del fregadero."*
- *"¿Qué trabajos tengo mañana?"*
- *"He terminado el trabajo de Pedro, fueron dos horas y cambié el condensador."*

## Modelo de negocio

SaaS con suscripción **por usuario/seat** (no por empresa):

| Plan | Precio orientativo | Incluye |
|---|---|---|
| Starter | ~19 €/usuario/mes | Clientes, trabajos, agenda, presupuestos, facturas, IA básica |
| Professional | ~29 €/usuario/mes | + IA avanzada, WhatsApp, automatizaciones, voz, documentos |
| Business | ~39-49 €/usuario/mes | + equipos, permisos avanzados, estadísticas, API |
| Enterprise | A medida | — |

Más adelante, consumo adicional (usage-based billing) para IA, WhatsApp, SMS y almacenamiento.

**Objetivo a finales de 2028:** 100-500 empresas de pago, priorizando product-market fit sobre crecimiento artificial.

## Equipo y plazos

- **Desarrollador:** una única persona (el autor de este repo).
- **Disponibilidad:** ~25h/semana.
- **Horizonte:** enero 2027 – diciembre 2028 (24 meses, ~2.000-2.300 horas útiles).

Esta limitación condiciona todas las decisiones técnicas del proyecto: se prioriza simplicidad, mantenibilidad y evitar sobreingeniería sobre soluciones pensadas para equipos grandes.

## Arquitectura

- **Clean Architecture** + **DDD** + **CQRS pragmático** + **Modular Monolith** (no microservicios, no Event Sourcing).
- **.NET 10**, **SQL Server** + **EF Core**, **ASP.NET Core Identity** + **JWT**, **MediatR**.
- Multi-tenant desde el primer commit: una única base de datos, aislamiento por `TenantId` + Global Query Filter de EF Core.

Todas las decisiones arquitectónicas están documentadas como ADR en [`docs/adr/`](./docs/adr/); el índice con el estado de cada una está en [`docs/README.md`](./docs/README.md). No se reabren sin una razón nueva y explícita — ver cada ADR para su contexto, opciones consideradas y consecuencias.

## Roadmap por fases

| Fase | Contenido |
|---|---|
| 0 | Arquitectura (ADRs, esqueleto de solución, Identity, Multi-tenancy) — **hecha** |
| 1 | MVP: Tenant, User, TenantUser, Customer, Job — **hecha** |
| 2 | Agenda, estados de trabajo, asignación de técnicos |
| 3 | Presupuestos, PDF |
| 4 | Facturas, pagos, seguimiento |
| 5 | .NET MAUI (app móvil para el técnico) |
| 6 | IA básica (crear trabajo por lenguaje natural, consultas, presupuestos asistidos) |
| 7 | WhatsApp Business |
| 8 | Chatbot por tenant |
| 9 | Voz |
| 10 | Automatizaciones |
| 11 | Internacionalización |
| 12 | Escalabilidad avanzada |

Calendario orientativo en [`Calendario del Proyecto.txt`](./docs/producto/Calendario%20del%20Proyecto.txt). Especificación completa del producto en [`ProyectoOFIFLOW.txt`](./docs/producto/ProyectoOFIFLOW.txt).

## Cómo se trabaja (Spec-Driven Development)

Cada feature nueva sigue un ciclo Specify → Plan → Tasks → Implement antes de tocar código:

1. **Specify** — `docs/specs/NNN-nombre-feature/spec.md`: objetivo, qué incluye/qué no, reglas de negocio, criterios de aceptación.
2. **Plan** — se apoya en las ADR existentes, o se cierra una ADR nueva si hace falta una decisión técnica que no esté cubierta.
3. **Tasks** — `docs/specs/NNN-nombre-feature/tasks.md`: checklist en incrementos pequeños (Domain → Application → Infrastructure → API → Tests → Frontend).
4. **Implement** — código, marcando cada tarea al completarse según la Definición de "Done".

Plantillas en [`docs/specs/TEMPLATE-spec.md`](./docs/specs/TEMPLATE-spec.md) y [`docs/specs/TEMPLATE-tasks.md`](./docs/specs/TEMPLATE-tasks.md).

## Estructura del repositorio

```
OFIFLOW/
├── README.md
├── docs/
│   ├── README.md                # Índice de ADRs y specs con su estado
│   ├── producto/                # ProyectoOFIFLOW.txt + Calendario del Proyecto.txt
│   ├── adr/                     # ADR-00X-*.md: decisiones de arquitectura
│   └── specs/                   # NNN-feature/spec.md + tasks.md, y plantillas
├── Directory.Build.props        # Configuración común a todos los .csproj
├── OfiFlow.slnx                 # Solución .NET
├── src/
│   ├── OfiFlow.Domain
│   ├── OfiFlow.Application
│   ├── OfiFlow.Infrastructure
│   └── OfiFlow.Api
└── tests/
    ├── OfiFlow.Domain.Tests
    ├── OfiFlow.Application.Tests
    ├── OfiFlow.Infrastructure.Tests
    └── OfiFlow.Api.Tests
```

## Estado actual

Fases 0 y 1 (MVP) completas: Tenant, User, TenantUser, Customer y Job funcionan de extremo a extremo (JWT + aislamiento de tenant). ADR-001 a ADR-008 aceptadas; ADR-009 (seguridad) y la spec `004-security-hardening` están en borrador. Detalle en [`docs/README.md`](./docs/README.md).
