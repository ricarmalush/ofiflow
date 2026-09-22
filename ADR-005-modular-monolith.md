# ADR-005 — Modular Monolith

**Estado:** Aceptado
**Fecha:** 2027-01 (ajustar a la fecha exacta en que se cierre)
**Proyecto:** OfiFlow

---

## Contexto

OfiFlow debe empezar pequeño, con un equipo de una persona, y crecer progresivamente hasta convertirse en una plataforma SaaS con múltiples bounded contexts (Identity, Tenancy, Customers, Jobs, Scheduling, Quoting, Invoicing, Payments, Messaging, Documents, AI, Subscriptions, Notifications).

## Problema

¿Qué estilo de arquitectura usará OfiFlow para organizar estos bounded contexts, y cómo se estructura el código para que sea mantenible por un equipo pequeño sin cerrar la puerta a crecer?

## Opciones consideradas

### Opción 1: Microservicios desde el inicio
- Ventajas: escalabilidad y despliegue independiente por contexto desde el día 1.
- Desventajas: sobreingeniería para un equipo de una persona; coste operativo (orquestación, comunicación entre servicios, observabilidad distribuida) desproporcionado para el tamaño actual del proyecto.

### Opción 2: Monolito sin modularizar
- Ventajas: máxima simplicidad inicial.
- Desventajas: sin límites claros entre contextos, el código tiende a acoplarse y dificulta una futura extracción si algún contexto necesita independizarse.

### Opción 3: Modular Monolith
- Ventajas: una sola aplicación desplegable (simplicidad operativa de un monolito) pero con límites claros entre bounded contexts (preparación para extraer un contexto en el futuro si hace falta).
- Desventajas: exige disciplina para no romper los límites entre carpetas/contextos con el tiempo.

## Decisión

Se decide usar **Modular Monolith**. Los bounded contexts se organizan como **carpetas por contexto dentro de cada capa** (ej. `Domain/Jobs`, `Application/Jobs`, `Domain/Customers`, `Application/Customers`), no como proyectos `.csproj` separados.

Estructura de solución:

```
src/
├── OfiFlow.Domain
│   ├── Jobs/
│   ├── Customers/
│   └── ... (un contexto por carpeta)
├── OfiFlow.Application
│   ├── Jobs/
│   ├── Customers/
│   └── ...
├── OfiFlow.Infrastructure
└── OfiFlow.Api

tests/
├── OfiFlow.Domain.Tests
├── OfiFlow.Application.Tests
├── OfiFlow.Infrastructure.Tests
└── OfiFlow.Api.Tests
```

**Criterio para reevaluar extraer un contexto como servicio independiente** (no antes de que se cumpla alguna señal de forma sostenida, no puntual):
- Señal de equipo: hay una persona dedicada solo a ese contexto de forma sostenida.
- Señal de escala: ese contexto necesita escalar de forma independiente al resto (ej. AI o Messaging con mucho más tráfico que Customers/Jobs).
- Señal de despliegue: ese contexto necesita desplegarse con más frecuencia que el resto y el monolito completo frena esos despliegues.

## Consecuencias

### Positivas
- Simplicidad operativa de un único desplegable mientras el proyecto es pequeño.
- Límites claros entre contextos desde el día 1, lo que facilita una futura extracción si alguna señal se cumple.
- Sin coste de infraestructura distribuida (orquestación, red entre servicios) mientras no hace falta.

### Negativas / riesgos aceptados
- Requiere disciplina activa para no dejar que un contexto empiece a depender directamente de las clases internas de otro; si eso ocurre, la ventaja de "límites claros" se pierde con el tiempo.

### Qué queda cerrado (no se debe reabrir sin una razón nueva y explícita)
- No dividir en microservicios mientras ninguna de las tres señales de extracción se cumpla de forma sostenida.

### Qué queda abierto para revisar más adelante
- Revisar periódicamente (por ejemplo, en cada cierre de fase) si alguna de las tres señales de extracción ya se cumple para algún contexto.

---

## Relación con otros ADR

- Depende de: ninguno (es la decisión de arquitectura base).
- Afecta a: ADR-002 (Multi-Tenancy), ADR-003 (CQRS), ADR-004 (Identity) — todas se implementan dentro de esta misma estructura de Modular Monolith.
