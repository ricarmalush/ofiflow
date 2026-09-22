# ADR-001 — Motor de base de datos

**Estado:** Aceptado
**Fecha:** 2027-01 (ajustar a la fecha exacta en que se cierre)
**Proyecto:** OfiFlow

---

## Contexto

OfiFlow necesita un motor de base de datos relacional desde el primer commit. El proyecto empezará en desarrollo local/VPS barato y podrá escalar a AWS más adelante si el negocio crece.

## Problema

¿Qué motor de base de datos usará OfiFlow, ahora y si el proyecto escala?

## Opciones consideradas

### Opción 1: SQL Server
- Ventajas: buen soporte con EF Core; sin coste relevante en fase inicial/VPS; compatible con hosting .NET barato (p. ej. Somee).
- Desventajas: coste de licencia por vCPU que crece si se despliega en la nube (AWS) a mayor escala.

### Opción 2: PostgreSQL desde el inicio
- Ventajas: sin coste de licencia; disponible como servicio gestionado en AWS (RDS).
- Desventajas: exige comprobar soporte en el hosting barato elegido (Somee no lo soporta, según lo comprobado); no elimina el problema de coste, solo lo traslada de proveedor.

## Decisión

Se decide usar **SQL Server** en la fase inicial (desarrollo y MVP). Si el proyecto crece hasta necesitar AWS y el coste de licencia se vuelve significativo, se migrará a **PostgreSQL** en ese momento.

## Consecuencias

### Positivas
- Sin fricción de hosting ahora; compatible con proveedores de hosting .NET baratos.
- No se introduce complejidad de migración antes de tener el MVP funcionando.

### Negativas / riesgos aceptados
- La migración futura a PostgreSQL no es trivial: tipos de datos distintos (identity/sequences, DATETIME2 vs TIMESTAMP, NVARCHAR vs TEXT), procedimientos almacenados no compatibles (T-SQL vs PL/pgSQL), migraciones de EF Core a regenerar, y migración de datos de producción sin downtime.
- Este coste se asume como trabajo futuro planificado, no como tarea gratuita o trivial.

### Qué queda cerrado (no se debe reabrir sin una razón nueva y explícita)
- No reevaluar el motor de base de datos mientras el proyecto esté en fase de desarrollo/MVP.

### Qué queda abierto para revisar más adelante
- Definir el punto exacto (número de clientes, coste mensual de licencia) que dispara la migración a PostgreSQL, para no descubrirlo tarde con clientes ya en producción.

---

## Relación con otros ADR

- Afecta a: ADR-002 (Multi-Tenancy) — el modelo de aislamiento por TenantId debe funcionar igual en SQL Server y en una futura migración a PostgreSQL.
- Sustituye a: ninguno (primera versión de esta decisión; una decisión anterior informal apuntaba a PostgreSQL desde el inicio, sustituida por esta).
