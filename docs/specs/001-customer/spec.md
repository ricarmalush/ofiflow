# Spec — 001-customer

**Estado:** Aprobada
**Fecha:** 2026-09-21
**Bounded Context:** Customers
**Depende de:** ADR-002 (Multi-Tenancy), ADR-003 (CQRS), ADR-006 (Persistencia y Domain Events)

---

## Objetivo

Permitir que un usuario de un tenant registre y gestione sus clientes (personas o empresas), como base para poder crear `Job` sobre ellos más adelante. Es el primer eslabón del flujo de valor: cliente → trabajo → presupuesto → cita → factura → cobro.

## Alcance — Qué SÍ incluye

- Crear un Customer (persona o empresa) con nombre, email, teléfono, dirección y notas.
- Consultar un Customer por Id.
- Listar los Customers del tenant activo.
- Actualizar los datos de contacto de un Customer existente.
- Eliminar un Customer (borrado físico, sin soft delete — ADR-006).
- Aislamiento estricto por tenant en las cinco operaciones anteriores.

## Alcance — Qué NO incluye

- **NEXT** (fase siguiente, cuando haga falta para Job/Quote/Invoice): historial de trabajos y facturas del Customer, búsqueda/filtrado avanzado.
- **LATER**: múltiples direcciones, múltiples contactos por Customer, documentos adjuntos.
- **NEVER** (por ahora, sin necesidad real): impedir el borrado de un Customer con Jobs asociados. Hoy no existe `Job`, así que esta restricción no tiene nada que disparar todavía — se añade como regla de negocio cuando se construya `Job` (backlog explícito más abajo), no antes.

## Reglas de negocio / invariantes

- Un Customer pertenece exactamente a un Tenant (`ITenantOwned`); el `TenantId` lo asigna el sistema a partir del `TenantContext` del usuario autenticado, nunca se acepta como dato de entrada.
- `Type` es `Person` o `Company`.
- `Name` es obligatorio y no puede estar vacío.
- `Email`, si se informa, debe tener formato válido (Value Object `Email`).
- `Phone`, si se informa, debe tener formato válido (Value Object `PhoneNumber`).
- `Address` y `Notes` son texto libre opcional en esta fase (sin estructurar).
- Un usuario solo puede crear, consultar, listar o actualizar Customers de su tenant activo (JWT), nunca de otro tenant.

## Modelo conceptual

```
Customer (AggregateRoot, ITenantOwned, IAuditable)
- Id: Guid
- TenantId: Guid
- Type: CustomerType (Person | Company)
- Name: string
- Email: Email? (Value Object)
- Phone: PhoneNumber? (Value Object)
- Address: string?
- Notes: string?
- CreatedAt: DateTime
- UpdatedAt: DateTime?
```

## Criterios de aceptación

- Dado un usuario autenticado de un tenant, cuando crea un Customer con `Name` válido, entonces se guarda con el `TenantId` de su tenant activo.
- Dado un intento de crear un Customer sin `Name`, entonces se rechaza con error de validación, sin llegar a Domain.
- Dado un `Email` con formato inválido, cuando se crea o actualiza un Customer, entonces se rechaza.
- Dado un usuario del Tenant A, cuando intenta consultar (`GetCustomerQuery`) un Customer del Tenant B, entonces no lo encuentra — **caso obligatorio de aislamiento de tenant** (sección 42 del prompt maestro).
- Dado un usuario autenticado, cuando ejecuta `GetCustomersQuery`, entonces solo recibe los Customers de su tenant activo.
- Dado un Customer existente del tenant activo, cuando se actualizan sus datos de contacto, entonces `UpdatedAt` se rellena automáticamente (vía el interceptor de ADR-006), sin que el Handler lo asigne a mano.
- Dado un Customer del tenant activo, cuando el usuario lo elimina, entonces se borra físicamente y deja de aparecer en `GetCustomersQuery` y `GetCustomerQuery`.
- Dado un usuario del Tenant A, cuando intenta eliminar (`DeleteCustomerCommand`) un Customer del Tenant B, entonces la operación no encuentra el Customer y no lo borra — mismo caso obligatorio de aislamiento de tenant.

## Decisiones técnicas relevantes

- Se apoya en: ADR-002 (aislamiento por TenantId + Global Query Filter), ADR-003 (CQRS con MediatR), ADR-006 (Guid plano como Id, `ITenantOwned`, `IAuditable`, FluentValidation, `IApplicationDbContext` sin repositorio genérico).
- ¿Requiere una ADR nueva? No — es la primera aplicación práctica de las decisiones ya cerradas, no introduce ninguna decisión arquitectónica nueva.

## Fuera de alcance / Backlog relacionado

- **Regla pendiente para cuando exista `Job`:** impedir (o decidir cómo tratar) el borrado de un Customer con Jobs activos/incompletos. Se resuelve en la spec de `Job` o en una revisión posterior de esta misma spec — no bloquea esta feature.
- Múltiples direcciones y contactos (sección 21 del prompt maestro ya lo marca como "considerar posteriormente").
- Búsqueda/filtrado avanzado, historial de trabajos y facturas (llegará de forma natural cuando existan `Job`/`Invoice`).

---

## Validación antes de aprobar

> ¿Esta funcionalidad ayuda a conseguir o mantener al primer cliente?

Sí — es imprescindible: sin `Customer` no se puede crear ningún `Job`, y sin `Job` no hay flujo de valor. Es la base mínima de todo el MVP (Fase 1).
