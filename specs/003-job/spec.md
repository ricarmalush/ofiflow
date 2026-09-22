# Spec — 003-job

**Estado:** Aprobada
**Fecha:** 2026-09-22
**Bounded Context:** Jobs
**Depende de:** `specs/001-customer/` (Customer debe existir), `specs/002-tenancy-identity/` (TenantUser para asignación), ADR-002, ADR-003, ADR-006

---

## Objetivo

Permitir registrar un trabajo (`Job`) para un `Customer` del tenant activo — el segundo eslabón del flujo de valor (cliente → **trabajo** → presupuesto → cita → factura → cobro). Cierra la Fase 1 (MVP) del roadmap: con `Tenant`, `User`, `TenantUser`, `Customer` y `Job` completos, el MVP mínimo queda funcionalmente completo.

## Alcance — Qué SÍ incluye

- Crear un Job para un Customer existente del tenant activo.
- Consultar un Job por Id, listar los Jobs del tenant activo.
- Actualizar los datos básicos de un Job (título, descripción, prioridad).
- Transiciones de estado básicas: `New → InProgress → Completed`, y `Cancelled` desde cualquier estado salvo `Completed`.
- Asignar (opcionalmente) un Job a un técnico — un `TenantUser` del mismo tenant, no un `User` directamente.
- Aislamiento de tenant en todas las operaciones anteriores.

## Alcance — Qué NO incluye

- **NEXT** (Fase 2 — Agenda, según el propio roadmap): calendario/agenda real, detección de conflictos de horario, vista de carga de trabajo por técnico. `ScheduledDate` se guarda como dato simple en esta spec, sin ninguna lógica de agenda alrededor.
- **NEXT** (Fase 2): reglas de negocio sobre asignación (ej. solo se puede asignar a un `TenantUser` con rol `Technician`) — en esta spec cualquier `TenantUser` del tenant puede asignarse, sin validar el rol.
- **LATER**: adjuntar fotos/documentos al Job (sección 33 del prompt maestro).

## Reglas de negocio / invariantes

- Un Job pertenece exactamente a un Tenant (`ITenantOwned`) y a un `Customer` de ese mismo tenant — no se acepta un `CustomerId` de otro tenant.
- `Title` es obligatorio.
- `Status` es un enum con los 6 valores ya decididos en ADR-006 (`New, Pending, Scheduled, InProgress, Completed, Cancelled`), pero esta spec solo expone las transiciones `New → InProgress → Completed` y `→ Cancelled` (cualquier estado salvo `Completed`) — `Pending`/`Scheduled` quedan reservados para cuando exista Agenda (Fase 2), sin transición posible todavía en esta spec.
- Un Job `Completed` no puede transicionar a ningún otro estado.
- `AssignedTenantUserId`, si se informa, debe ser un `TenantUser` del mismo tenant (no de otro tenant, no un `User` sin membresía).
- **Cierra el backlog abierto en `specs/001-customer/spec.md`, no lo aplaza:** no se puede eliminar (`DeleteCustomerCommand`) un `Customer` que tenga al menos un Job en un estado distinto de `Completed`/`Cancelled` — se rechaza con un error de negocio explícito. Hoy es el primer momento en que esta regla tiene algo real que comprobar (antes de `Job`, ningún Customer podía tener trabajos), así que se resuelve ahora, no en una fase posterior.

## Modelo conceptual

```
Job (AggregateRoot, ITenantOwned, IAuditable)
- Id: Guid
- TenantId: Guid
- CustomerId: Guid
- Title: string
- Description: string?
- Status: JobStatus (New | Pending | Scheduled | InProgress | Completed | Cancelled)
- Priority: JobPriority (Low | Normal | High | Urgent)
- ScheduledDate: DateTime?
- AssignedTenantUserId: Guid?
- EstimatedDurationMinutes: int?
- CreatedAt: DateTime
- UpdatedAt: DateTime?
```

## Criterios de aceptación

- Dado un Customer existente del tenant activo, cuando se crea un Job con `Title` válido, entonces se guarda con `TenantId` del tenant activo y `Status = New`.
- Dado un `CustomerId` de otro tenant, cuando se intenta crear un Job sobre él, entonces se rechaza (no se encuentra, por el Global Query Filter).
- Dado un Job en `New`, cuando se marca `InProgress` y luego `Completed`, entonces las transiciones se aplican correctamente.
- Dado un Job en `Completed`, cuando se intenta transicionar a cualquier otro estado, entonces se rechaza.
- Dado un Job en cualquier estado salvo `Completed`, cuando se cancela, entonces pasa a `Cancelled`.
- Dado un `AssignedTenantUserId` de un `TenantUser` de otro tenant, cuando se intenta asignar, entonces se rechaza.
- Dado un usuario del Tenant A, cuando intenta leer/actualizar/borrar un Job del Tenant B, entonces no lo encuentra — caso obligatorio de aislamiento de tenant.
- Dado un Customer con un Job en estado `New`/`InProgress`, cuando se intenta eliminar ese Customer, entonces se rechaza explícitamente.
- Dado un Customer cuyos Jobs están todos en `Completed`/`Cancelled` (o no tiene ninguno), cuando se elimina, entonces se elimina sin problema (comportamiento ya existente, sin cambios).

## Decisiones técnicas relevantes

- Se apoya en: ADR-002 (aislamiento de tenant), ADR-003 (CQRS), ADR-006 (`JobStatus` como enum + guard clauses, ya decidido de antemano).
- ¿Requiere una ADR nueva? No.

## Fuera de alcance / Backlog relacionado

- Agenda/calendario real, detección de conflictos (Fase 2).
- Validar que `AssignedTenantUserId` tenga rol `Technician` (Fase 2).
- Adjuntar fotos/documentos (sección 33, fase posterior).

---

## Validación antes de aprobar

> ¿Esta funcionalidad ayuda a conseguir o mantener al primer cliente?

Sí — con `Job`, el MVP de la Fase 1 queda completo: un profesional puede registrar clientes y los trabajos que les hace, la base mínima indispensable de todo el producto.
