# ADR-008 — Estrategia de tests de integración contra SQL Server

**Estado:** Aceptado
**Fecha:** 2026-09-22
**Proyecto:** OfiFlow

---

## Contexto

ADR-006 dejó abierta una decisión: contra qué SQL Server ejecutan sus tests `OfiFlow.Infrastructure.Tests` y `OfiFlow.Api.Tests` (LocalDB vs Testcontainers). Hasta ahora, el test obligatorio de aislamiento de tenant (sección 42 del prompt maestro) solo se ha verificado **manualmente** con `curl` contra una LocalDB real, en tres ocasiones (`001-customer`, `002-tenancy-identity`, `003-job`) — nunca de forma automatizada.

## Problema

¿Qué mecanismo usar para que los tests de integración corran contra una SQL Server real, garantizando una base de datos limpia en cada ejecución?

## Opciones consideradas

### Opción 1: SQL Server LocalDB, con base de datos de nombre único por ejecución

- Ventajas: ya instalada y verificada en este equipo; sin dependencia nueva; el aislamiento entre ejecuciones se consigue creando una base de datos con nombre único por ejecución en la misma instancia de LocalDB.
- Desventajas: es Windows-only; no portable a un runner de CI Linux sin cambios cuando exista un pipeline.

### Opción 2: Testcontainers (`Testcontainers.MsSql`)

- Ventajas: levanta una SQL Server real en un contenedor Docker, aislada, por cada ejecución de tests; funciona exactamente igual en la máquina de desarrollo y en cualquier runner de CI moderno. Docker ya está instalado y en marcha en este equipo (v29.8.0).
- Desventajas: requiere que el daemon de Docker esté arrancado para poder ejecutar estos tests — una dependencia externa que LocalDB no tiene. Cada ejecución tarda algo más (arrancar el contenedor) que conectar a una LocalDB ya en marcha.

## Decisión

Se decide usar **Testcontainers (`Testcontainers.MsSql`)** para los tests de integración de `OfiFlow.Infrastructure.Tests` (y `OfiFlow.Api.Tests` cuando existan tests de integración de API completos).

LocalDB se sigue usando libremente para verificación manual rápida durante el desarrollo (`dotnet run` + `curl`), pero no para tests automatizados.

## Consecuencias

### Positivas

- Los tests de integración funcionarán sin cambios el día que se monte CI/CD — no hay una migración pendiente oculta.
- Aislamiento total entre ejecuciones de test: cada run levanta una base de datos limpia en un contenedor nuevo, sin arrastrar datos de ejecuciones anteriores ni de las verificaciones manuales sobre la LocalDB persistente.

### Negativas / riesgos aceptados

- Ejecutar estos tests exige tener Docker Desktop arrancado localmente — si no lo está, los tests fallan con un error de conexión al daemon, no con un mensaje que indique "arranca Docker". Se acepta este coste de DX menor.
- Tests algo más lentos que contra una LocalDB ya activa (segundos extra por arrancar el contenedor).

### Qué queda cerrado (no se debe reabrir sin una razón nueva y explícita)

- No usar LocalDB para tests automatizados de integración.

### Qué queda abierto para revisar más adelante

- Si en el futuro Docker deja de estar disponible en el entorno de CI elegido, revisar esta decisión.

---

## Relación con otros ADR

- Cierra el punto abierto en ADR-006 ("Mecanismo para los tests de integración... contra SQL Server real").
- Depende de: ADR-001 (SQL Server como motor de base de datos).
