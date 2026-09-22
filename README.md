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

Todas las decisiones arquitectónicas están documentadas como ADR en la raíz del repo (`ADR-001` a `ADR-007` por ahora). No se reabren sin una razón nueva y explícita — ver cada ADR para su contexto, opciones consideradas y consecuencias.

## Roadmap por fases

| Fase | Contenido |
|---|---|
| 0 | Arquitectura (ADRs, esqueleto de solución, Identity, Multi-tenancy) — **en curso** |
| 1 | MVP: Tenant, User, TenantUser, Customer, Job |
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

Calendario orientativo en [`Calendario del Proyecto.txt`](./Calendario%20del%20Proyecto.txt). Especificación completa del producto en [`ProyectoOFIFLOW.txt`](./ProyectoOFIFLOW.txt).

## Cómo se trabaja (Spec-Driven Development)

Cada feature nueva sigue un ciclo Specify → Plan → Tasks → Implement antes de tocar código:

1. **Specify** — `specs/NNN-nombre-feature/spec.md`: objetivo, qué incluye/qué no, reglas de negocio, criterios de aceptación.
2. **Plan** — se apoya en las ADR existentes, o se cierra una ADR nueva si hace falta una decisión técnica que no esté cubierta.
3. **Tasks** — `specs/NNN-nombre-feature/tasks.md`: checklist en incrementos pequeños (Domain → Application → Infrastructure → API → Tests → Frontend).
4. **Implement** — código, marcando cada tarea al completarse según la Definición de "Done".

Plantillas en [`specs/TEMPLATE-spec.md`](./specs/TEMPLATE-spec.md) y [`specs/TEMPLATE-tasks.md`](./specs/TEMPLATE-tasks.md).

## Estructura del repositorio

```
OFIFLOW/
├── ProyectoOFIFLOW.txt          # Especificación completa del producto
├── Calendario del Proyecto.txt  # Roadmap 2027-2028
├── ADR-00X-*.md                 # Decisiones de arquitectura
├── specs/                       # Specify + Tasks por feature
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

Fase 0 casi cerrada: 7 ADR aceptadas, esqueleto de la solución .NET creado y compilando (0 errores). Pendiente antes de pasar a Fase 1: clases base de Domain (`AggregateRoot`, `ITenantOwned`, `IAuditable`) y la primera spec (`001-customer`).
