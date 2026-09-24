# Tasks — [NNN-nombre-feature]

**Spec relacionada:** [./spec.md](./spec.md)
**Estado general:** Pendiente

---

## Convención

- Cada tarea debe ser completable y verificable en una sola sesión, orientativamente 1-4h (sección 89 del prompt maestro). Si una tarea parece más grande, se divide.
- Una tarea se marca `[x]` solo cuando cumple la Definición de "Done" (sección 86): código implementado + reglas de negocio + validaciones + autorización + multi-tenancy comprobado + tests + probado manualmente.
- Orden de incrementos por defecto (sección 87): Domain → Application → Infrastructure → API → Tests → Frontend (si aplica esta fase).
- No generar todos los archivos de golpe: completar y verificar un bloque antes de pasar al siguiente.

## Checklist

### Domain
- [ ] ...

### Application
- [ ] Command: ...
- [ ] Validator: ...
- [ ] Query: ...

### Infrastructure
- [ ] Configuración EF Core de la(s) entidad(es)
- [ ] Migración
- [ ] ...

### API
- [ ] Endpoint(s)
- [ ] Documentación OpenAPI

### Tests
- [ ] Unit tests de Domain (invariantes, transiciones de estado)
- [ ] Unit tests de Application (Handlers)
- [ ] Test obligatorio de aislamiento de tenant (sección 42)
- [ ] Integration tests (EF Core + SQL Server)

### Frontend (si aplica en esta fase)
- [ ] ...

---

## Notas / bloqueos

- ...
