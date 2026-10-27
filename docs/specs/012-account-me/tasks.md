# Tasks — 012-account-me

**Spec relacionada:** [./spec.md](./spec.md)
**ADR relacionada:** [ADR-012](../../adr/ADR-012-autorizacion-rbac.md) (se amplía con un permiso; sin ADR nueva)
**Estado general:** Implementada y verificada (2026-10-27), pendiente de commit; push, PR y merge, a la espera de una orden explícita

---

## Convención

- Cada tarea completable y verificable en una sesión.
- Se marca `[x]` solo cuando cumple la Definición de «Done»: compila, tests en verde, mutaciones detectadas y manual al día.
- Las mutaciones se hacen a mano sobre el código real, con copia de seguridad, y se anotan aquí.

---

## Bloques

### Bloque 1 — El permiso y los nombres estables (Domain)
- [x] `Permission.AccountRead` y su fila en `RolePermissions` para los cinco roles
- [x] `PermissionNames`: el nombre `Entidad.Acción` de cada permiso, derivado del enum (un valor fuera del enum no tiene nombre)
- [x] Tests: cada permiso tiene nombre con el formato, único y estable (los doce con su texto exacto escrito aparte); los cinco roles tienen `AccountRead`; la matriz aprobada actualizada
- [x] [manual] Capítulo 8 (tabla de permisos) y ADR-012 (relación)

### Bloque 2 — La consulta (Application)
- [x] `GetCurrentAccountQuery` con `[RequiresPermission(Permission.AccountRead)]`, `CurrentAccountDto` y su handler (parte de `TenantUsers`; rol y permisos de la base de datos)
- [x] Entra en la tabla «operación → permiso» (36 operaciones) y en las listas blancas de lectura de `Users` y `Tenants`, con su motivo
- [x] Tests con dobles: los datos de quien pide, el rol efectivo (no el del token), los permisos de cada rol, varias personas y empresas, la pertenencia ausente (denegar), la forma exacta de la respuesta y que la consulta no tenga parámetros
- [x] [manual] Capítulo 3 (carpeta nueva)

### Bloque 3 — La ruta (Api)
- [x] `GET /api/v1/account/me` (`AccountEndpoints`), `Cache-Control: no-store`, etiqueta de OpenAPI
- [x] Tests con SQL Server real y la API entera: los cinco roles; el rol cambiado con el mismo token; el `MembershipId` de un técnico igual al de un trabajo asignado; dos empresas; empresa suspendida; token de plataforma, sin token y manipulado; miembro quitado; parámetros ignorados; sin secretos en el cuerpo (con el segundo factor activado)
- [x] Sección en la colección `OfiFlow.Api.http`
- [x] [manual] Capítulos 0 (tabla de rutas), 5 (listas blancas), 6 (parte 8) y 8 (permisos y «quién soy»)

### Bloque 4 — Mutaciones, verificación real y cierre
- [x] Mutaciones sobre el código real: **15, todas detectadas** (Domain 3, Application 6, Infrastructure 1, API 4 y 1 de la lista blanca):
  nombre del permiso sin punto (Q1), técnico sin `Account.Read` (Q2), permisos sin ordenar (Q3), rol del token en vez del de la base de datos (Q4), todos los permisos en vez de los del rol (Q5), consulta sin filtrar por la persona (Q6) o por la empresa del token (Q7), consulta que se salta el filtro de empresa (Q8, la caza Infrastructure), sin denegar sin pertenencia (Q9), operación sin su declaración de permiso (Q10), campo de más en la respuesta (Q11), respuesta cacheable (Q12), ruta sin exigir sesión (Q13), ruta sin mapear (Q14) y handler fuera de la lista blanca de `Users` (Q15). Se comprobó al final que `src` y `tests` quedaron idénticos a la copia de seguridad.
- [x] Verificación contra la API publicada de verdad (Kestrel + LocalDB, `verify012.sh` fuera del repositorio): **26 comprobaciones correctas**, entre ellas el desfase del token a la vista (el token dice `Technician` antes y después del cambio de rol; `/me` dice `Manager`), el `membershipId` del técnico igual al de su trabajo asignado, la empresa B sin ver nada de la A y los parámetros ajenos ignorados
- [x] [manual] Capítulo 9 (cifras y tests nuevos) y `docs/README.md`; estado de la spec y de las tareas
- [x] Suite, proyecto a proyecto: Domain 229, Application 575, Infrastructure 315, Api 461 (**1580 casos**)
- [ ] [externo] Push de la rama, PR y merge, cada uno con confirmación explícita
