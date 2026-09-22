# Spec — [NNN-nombre-feature]

**Estado:** Borrador
**Fecha:**
**Bounded Context:** [Identity | Tenancy | Customers | Jobs | ...]
**Depende de:** [otras specs y/o ADR relevantes]

---

## Objetivo

¿Qué problema le resuelve esta feature al profesional? Una o dos frases, conectadas al flujo de valor (cliente → trabajo → presupuesto → cita → factura → cobro).

## Alcance — Qué SÍ incluye

- ...
- ...

## Alcance — Qué NO incluye

Ideas relacionadas descartadas explícitamente para esta feature, clasificadas (sección 104 del prompt maestro):

- **NOW** (necesario ahora, pero fuera de esta spec concreta): ...
- **NEXT** (siguiente fase): ...
- **LATER** (interesante, no prioritario): ...
- **NEVER** (no aporta suficiente valor): ...

## Reglas de negocio / invariantes

- ...
- ...

## Modelo conceptual

Aggregate(s) implicados, relaciones por Id (no por navegación directa), estados si aplica.

```
[Aggregate]
- Id
- TenantId
- ...
```

## Criterios de aceptación

- Dado ..., cuando ..., entonces ...
- Dado ..., cuando ..., entonces ...

Caso multi-tenant obligatorio: un usuario del Tenant A nunca puede leer ni modificar datos de esta feature pertenecientes al Tenant B.

## Decisiones técnicas relevantes

- Se apoya en: ADR-00X (...)
- ¿Requiere una ADR nueva? [Sí/No — si sí, cuál y por qué]

## Fuera de alcance / Backlog relacionado

- ...

---

## Validación antes de aprobar

> ¿Esta funcionalidad ayuda a conseguir o mantener al primer cliente? (sección 84 del prompt maestro)

[Respuesta breve — si la respuesta es no, esta spec no debería pasar a Plan/Tasks todavía]
