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

### Opción 1: Testcontainers (`Testcontainers.MsSql`)

- Ventajas: contenedor Docker nuevo y aislado por ejecución; funciona igual en cualquier CI moderno sin configuración adicional.
- Desventajas: introduce Docker como dependencia obligatoria para ejecutar tests, **sin que exista todavía ningún pipeline de CI/CD que lo necesite** — es resolver un problema que hoy no existe (viola el principio del propio proyecto de "Design for scale, build for current reality"). Se probó a adoptar y se descartó tras revisión (ver Notas).

### Opción 2: SQL Server LocalDB, con base de datos de nombre único por ejecución

- Ventajas: ya instalada y verificada en este equipo; sin dependencia nueva; el aislamiento entre ejecuciones se consigue igual que con Testcontainers — creando una base de datos con nombre único (`OfiFlowTests_{Guid}`) al empezar la ejecución y eliminándola al terminar, en vez de reutilizar una base compartida.
- Desventajas: es Windows-only; el día que exista un pipeline de CI en un runner sin LocalDB, habrá que migrar. Se acepta este coste como trabajo futuro, no como problema actual.

## Decisión

Se decide usar **SQL Server LocalDB con una base de datos de nombre único por ejecución de test**, no Testcontainers.

Los tests de integración crean su propia base de datos (`OfiFlowTests_{Guid.NewGuid()}`) contra la instancia local de LocalDB al arrancar, aplican las migraciones, y la eliminan al terminar — el mismo aislamiento que ofrecería un contenedor Docker nuevo, sin la dependencia de Docker.

LocalDB sigue siendo también el motor usado para verificación manual (`dotnet run` + `curl`) durante el desarrollo, sobre la base de datos persistente `OfiFlow` (no las bases de datos efímeras de test).

## Consecuencias

### Positivas

- Cero dependencias nuevas; los tests de integración funcionan hoy mismo, sin arrancar Docker.
- Aislamiento real entre ejecuciones: cada run tiene su propia base de datos, sin arrastrar datos de ejecuciones anteriores ni de las verificaciones manuales.

### Negativas / riesgos aceptados

- Solo funciona en Windows con LocalDB instalada — no portable a un runner de CI Linux sin cambios.
- Si algo interrumpe el test a mitad de ejecución, podría quedar una base de datos huérfana sin eliminar (mitigable con un `try/finally` en el fixture de test).

### Qué queda cerrado (no se debe reabrir sin una razón nueva y explícita)

- No adoptar Testcontainers ni Docker como dependencia de testing mientras no exista un pipeline de CI/CD real que lo requiera.

### Qué queda abierto para revisar más adelante

- Migrar a Testcontainers en el momento en que se monte CI/CD (sección 45 del prompt maestro) — trabajo previsto, no urgente.

---

## Notas

Se consideró inicialmente Testcontainers y se llegó a añadir el paquete `Testcontainers.MsSql`, pero se revirtió tras cuestionar si aportaba valor **hoy**: el único problema real a resolver (aislamiento entre ejecuciones) ya lo resuelve LocalDB con bases de datos dinámicas, sin necesidad de Docker. Adoptar Testcontainers ahora habría sido anticipar una necesidad de CI/CD que todavía no existe.

## Relación con otros ADR

- Cierra el punto abierto en ADR-006 ("Mecanismo para los tests de integración... contra SQL Server real").
- Depende de: ADR-001 (SQL Server como motor de base de datos).
