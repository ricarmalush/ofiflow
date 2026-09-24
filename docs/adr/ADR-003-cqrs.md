# ADR-003 — CQRS

**Estado:** Aceptado
**Fecha:** 2027-01 (ajustar a la fecha exacta en que se cierre)
**Proyecto:** OfiFlow

---

## Contexto

OfiFlow necesita separar operaciones que modifican estado (Commands) de operaciones de solo lectura (Queries) desde el inicio, pero sin caer en una implementación de CQRS extrema (Event Sourcing, bases de datos separadas, arquitectura distribuida) que no está justificada para el tamaño actual del proyecto.

## Problema

¿Cómo se implementa CQRS de forma pragmática, y qué librería (si alguna) se usa para Commands, Queries, Handlers y Pipeline Behaviors transversales?

## Opciones consideradas

### Alcance de CQRS

**Opción 1: CQRS extremo (Event Sourcing, bases de datos separadas de lectura/escritura)**
- Ventajas: máxima escalabilidad y trazabilidad de cambios.
- Desventajas: sobreingeniería total para una sola aplicación con una sola base de datos y sin necesidad real de esa escala todavía.

**Opción 2: CQRS pragmático (una sola app, una sola BD, Commands/Queries separados con Handlers separados)**
- Ventajas: separación clara de responsabilidades sin coste operativo añadido; Queries pueden usar DTOs/Read Models optimizados sin pasar por Aggregates de Domain cuando no es necesario.
- Desventajas: ninguna relevante para el tamaño actual del proyecto.

### Librería para implementar el patrón

**Opción 1: MediatR**
- Ventajas: librería madura y muy usada en el ecosistema .NET para este patrón exacto; Pipeline Behaviors listos para validación, logging, autorización, transacciones y performance sin reinventar el mecanismo de interceptación.
- Desventajas: desde la versión 13 requiere licencia comercial de pago para empresas con más de $5M de ingresos anuales; por debajo de ese umbral (edición Community) sigue siendo gratuita.

**Opción 2: CQRS manual sin librería**
- Ventajas: cero dependencia externa, cero riesgo de licencia futura.
- Desventajas: hay que construir y mantener el mecanismo de dispatch y los Pipeline Behaviors a mano (vía patrón Decorator), trabajo que MediatR ya resuelve.

## Decisión

Se decide:
- Usar **CQRS pragmático**: una sola aplicación, una sola base de datos SQL Server, Commands y Queries con Handlers separados. Las Queries usan DTOs/Read Models optimizados para lectura, sin pasar por Aggregates de Domain salvo que sea necesario.
- Usar **MediatR** (edición Community, gratuita mientras OfiFlow se mantenga por debajo de $5M de ingresos anuales — umbral muy por encima del objetivo orientativo de 100-500 empresas a finales de 2028) para implementar Commands, Queries, Handlers y Pipeline Behaviors.
- Pipeline Behaviors activos desde el MVP inicial (semana 7 del sprint): **Validación, Logging, Autorización, Transacciones y Performance** (medir tiempos).
- No implementar inicialmente: bases de datos separadas de lectura/escritura, replicación, Event Sourcing, Kafka, arquitectura distribuida ni microservicios para este patrón.

## Consecuencias

### Positivas
- Separación clara Command/Query desde el primer Handler, sin coste operativo añadido.
- Los cinco Pipeline Behaviors cubren de entrada los aspectos transversales más importantes (seguridad, corrección, trazabilidad, rendimiento) sin trabajo manual repetido en cada Handler.
- Sin coste de licencia mientras el negocio se mantenga en el rango de ingresos previsto.

### Negativas / riesgos aceptados
- Si OfiFlow supera los $5M de ingresos anuales, MediatR pasaría a requerir licencia comercial de pago — riesgo aceptado porque en ese escenario el negocio ya sería mucho más grande que el objetivo actual.
- Cinco Pipeline Behaviors activos desde el inicio añaden algo de complejidad a cada request comparado con implementarlos progresivamente; se acepta porque son los aspectos que el propio proyecto considera críticos desde el diseño (seguridad, auditoría).

### Qué queda cerrado (no se debe reabrir sin una razón nueva y explícita)
- No implementar Event Sourcing, bases de datos separadas de lectura/escritura, ni arquitectura distribuida para este patrón mientras no exista una necesidad real y justificada.

### Qué queda abierto para revisar más adelante
- Revisar el estado de la licencia de MediatR si el negocio se acerca al umbral de $5M de ingresos anuales.
- Evaluar si conviene fijar la versión de MediatR usada (Community) de forma explícita en el proyecto para evitar actualizaciones accidentales a una versión que requiera licencia distinta.

---

## Relación con otros ADR

- Depende de: ADR-005 (Modular Monolith) — Commands y Queries se organizan dentro de las carpetas por contexto de `Application`.
- Depende de: ADR-002 (Multi-Tenancy) — Commands y Queries reciben el `TenantContext` de forma consistente a través de los Pipeline Behaviors.
