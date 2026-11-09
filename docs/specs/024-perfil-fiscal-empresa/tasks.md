# Tasks — 024-perfil-fiscal-empresa

**Spec relacionada:** [./spec.md](./spec.md)
**ADR relacionadas:** [ADR-012](../../adr/ADR-012-autorizacion-rbac.md) (los dos permisos), [ADR-002](../../adr/ADR-002-multi-tenancy.md) (aislamiento por empresa), [ADR-006](../../adr/ADR-006-persistencia-domain-events.md) (entidades y `Guid`), [ADR-009](../../adr/ADR-009-seguridad-owasp.md) (datos personales y registro), [ADR-011](../../adr/ADR-011-errores-codigos-diccionario.md) (códigos de error), [ADR-024](../../adr/ADR-024-segundo-factor-obligatorio.md) (la puerta se aplica sola) y la nueva **ADR-027** («Perfil fiscal de la empresa»)
**Estado general:** Pendiente de aprobación de la spec

---

## Convención

- Cada tarea completable y verificable en una sesión.
- Se marca `[x]` solo cuando cumple la Definición de «Done»: compila, tests en verde, mutaciones detectadas, comprobado en vivo y manual al día.
- **Nivel de riesgo de las pruebas: medio.** Es poco modelo y sin dinero, pero toca un **dato personal** (el NIF de un autónomo es su DNI) y una **validación por algoritmo** que si falla deja pasar identificadores falsos o rechaza los buenos. Se aplica: una **tabla de casos fijos del NIF, NIE y CIF** (válidos de cada tipo e inválidos de cada causa), los permisos en los cinco roles, el aislamiento entre empresas, la concurrencia del alta y que ningún dato del perfil llega al registro. Las mutaciones se hacen sobre lo que decide.
- Las mutaciones se hacen a mano sobre el código real, con copia de seguridad y **`touch` al restaurar**; solo cuenta como «detectada» una con tests en rojo (`[FAIL]`); un test colgado o un error de compilación no cuentan. Los guiones van **en ficheros**.
- Las suites se lanzan **por proyecto, una detrás de otra**. Si `dotnet build` choca con una DLL bloqueada (IIS Express), se compila a una carpeta aparte con `-p:OutDir`.
- Verificación en vivo con la receta de siempre (base desechable, API real, Mailpit, TOTP; en el bloque 3 `ng serve` y el navegador integrado). Los valores de prueba (NIF, direcciones) se generan en ejecución.
- **Orden de entrega:** el backend **puede fusionarse solo** (operaciones nuevas, nada existente cambia). El frontend va en el mismo par de PR por limpieza.

---

## Bloques

### Bloque 1 — El dominio, la validación y la persistencia (Domain + Application + Infrastructure)
- [x] **`SpanishTaxId`** (objeto de valor): `Normalize` (mayúsculas, sin espacios, guiones ni puntos) e `IsValid` con el algoritmo oficial de **NIF** (8 cifras y letra: resto de 23), **NIE** (X, Y, Z → 0, 1, 2 y la misma letra) y **CIF** (letra de entidad, 7 cifras, dígito o letra de control según el tipo de entidad). Una sola fuente.
- [x] **`SpanishPostalCode`**: cinco cifras y provincia 01–52.
- [x] **`CompanyProfile`** (`ITenantOwned`, `IAuditable`): `Create` y `Update` (sustituyen todo; las invariantes viven aquí: obligatorios, límites desde constantes, NIF y código postal válidos, teléfono y email con los objetos de valor de los clientes), con `IsComplete`. Cotas como constantes (fuente única para dominio, validador y EF).
- [x] **Errores y diccionario:** `company.tax_id_invalid` y `company.postal_code_invalid` (de campo), con su texto en `ErrorMessages.resx` y su caso en los tests de contrato. Los demás errores de campo reutilizan los de teléfono y email.
- [x] **Permisos (Domain):** `CompanyRead` y `CompanyManage` con la tabla de la R7. 18 → 20 permisos, 90 → 100 celdas y la tabla de operaciones, actualizadas **a propósito**.
- [x] **Evento `1409`** en `SecurityEventIds` (alta o modificación; sin datos).
- [x] **Persistencia:** `CompanyProfileConfiguration` (longitudes desde las constantes, filtro global por empresa, **índice único `(TenantId)`**), `IApplicationDbContext.CompanyProfiles` y la **migración** (revisada a mano: una tabla, un índice, nada más).
- [x] **Casos de uso (Application):** `GetCompanyProfile` (perfil o uno vacío con `isComplete = false`), `UpsertCompanyProfile` (crea o sustituye; la violación del índice único por un alta simultánea se resuelve releyendo y modificando) y su `Validator` (longitudes **antes** de tocar la base). Cada operación declara su permiso. El `ToString()` del comando no imprime sus propiedades.
- [x] **Tests de Domain:** la **tabla de casos fijos** (NIF, NIE y CIF válidos de cada tipo de entidad, y cada causa de invalidez: letra equivocada, longitud, prefijo desconocido, caracteres no válidos; la normalización), el código postal en sus dos lados (00, 01, 52, 53, 4 y 6 cifras, letras), `Create`/`Update` con cada obligatorio ausente, cada límite, el opcional que se borra y que `IsComplete` refleja los cinco obligatorios.
- [x] **Tests de Application (dobles):** lectura sin perfil, alta, modificación (un opcional ausente borra), el alta simultánea resuelta, el validador (cada cota en sus dos lados, con su código) y que el comando nunca lleva una empresa.
- [x] **Tests de Infrastructure (SQL Server real):** el filtro por empresa, el índice único por empresa, **dos altas simultáneas** (queda un perfil y ningún `500`), la ida y vuelta de los textos y el plan de la lectura por `TenantId`.
- [x] **Mutaciones:** cada paso del algoritmo del NIF/NIE/CIF (el módulo, la tabla de letras, el mapeo de X/Y/Z, el control por tipo de entidad), la normalización, el rango de provincias, cada límite y obligatorio, el opcional que no se borra, `IsComplete`, el filtro por empresa quitado, el índice único quitado, el permiso que falta a un rol o sobra a `Technician`.
- [x] **[manual]** Se hace en el **bloque 4**.
- [ ] [externo] Commit del bloque 1

### Bloque 2 — La API y sus pruebas de extremo a extremo (Api)
- [ ] **Los dos endpoints** (`GET` y `PUT /api/v1/company/profile`) con `200`/`204`, `Cache-Control: no-store` en la lectura, el cuerpo **sin** `tenantId`, documentados en OpenAPI y en el `.http`.
- [ ] **Evento `1409`** emitido al guardar (alta o modificación) y **escaneos de arquitectura**: ninguna petición lleva una empresa, ninguna operación se salta el filtro, ninguna operación está en la lista cerrada del factor (siguen siendo cuatro), todo comando declara su permiso y **nada del perfil llega al registro** (ni NIF, dirección, teléfono ni email).
- [ ] **Tests de extremo a extremo** (SQL Server real): lectura sin perfil, alta y lectura normalizada, modificación que borra un opcional, cada error en su código y su `HTTP` (NIF, código postal, obligatorios, límites), la **matriz de roles** (2 operaciones × 5 roles con control positivo en cada permitido), un `Owner` y un `Admin` sin factor (`403 mfa.enrollment_required`), sin sesión (`401`), **aislamiento entre dos empresas** (cada una ve el suyo o uno vacío; un `tenantId` ajeno en el `PUT` se ignora), **dos `PUT` simultáneos** de una empresa sin perfil y que el evento `1409` queda y no contiene ningún dato.
- [ ] **Mutaciones** de la capa API: endpoint sin permiso o con otro, verbo o código cambiado, sin `no-store`, `Company.Manage` concedido a `Manager`, un dato del perfil registrado, el factor saltado.
- [ ] **Comprobación en vivo** (API real, base desechable, dos empresas, roles reales, TOTP): el flujo completo con NIF de cada tipo, los errores, los roles, el aislamiento y que el registro de la API no contiene el NIF ni la dirección.
- [ ] **[manual]** Se hace en el **bloque 4**.
- [ ] [externo] Commit del bloque 2

### Bloque 3 — Frontend (`ofiflow-frontend`)
- [ ] **Modelo y servicio:** `CompanyProfile`, el servicio con sus dos llamadas, los dos permisos en el modelo de permisos y en `fake-account`, y las constantes de límites.
- [ ] **Validación en pantalla del NIF/NIE/CIF** con **los mismos casos fijos** que el backend (copiados como datos): una ayuda al teclear; manda siempre la API.
- [ ] **Pantalla «Datos de la empresa»** (entrada de menú con `Company.Read`): formulario con los cinco obligatorios y los tres opcionales; **editable solo con `Company.Manage`**, solo lectura para los demás; errores de la API por campo; aviso «completa los datos» cuando `isComplete` es falso; el NIF normalizado tras guardar.
- [ ] **Tests (Karma/Jasmine):** modelo, servicio, el validador del NIF con la tabla fija, el componente (lectura, edición, errores por campo, solo lectura sin permiso, perfil vacío) y las rutas con su guardián.
- [ ] **Mutaciones** del frontend: el guardián, la edición habilitada sin permiso, el validador, la normalización, los errores por campo.
- [ ] **Comprobación en el navegador** integrado: perfil vacío, guardar, error de NIF, solo lectura con un `Employee`, y sin entrada de menú para `Technician`.
- [ ] [externo] Commit del bloque 3

### Bloque 4 — Documentación y cierre
- [ ] **ADR-027** «Perfil fiscal de la empresa» (decisiones D4 a D8, el modelo de amenazas y los riesgos aceptados) y anotaciones «Ampliada por» en ADR-012 (los dos permisos y las 100 celdas) y ADR-002 (una entidad más de la empresa).
- [ ] **Manual:** capítulos afectados (modelo, permisos, API, errores, eventos, tests) y anexo; recuentos (20 permisos, 100 celdas, 57 operaciones, 66 códigos de error, 54 eventos) comprobados contra el código.
- [ ] **Spec y tasks cerradas**, `README.md` y `docs/README.md` al día; la ADR-026 y las specs siguientes renumeradas (PDF → 025, correo → 026).
- [ ] [externo] Commit del bloque 4, push de las dos ramas y PR (solo con orden expresa)

---

## Notas / bloqueos

- Los recuentos del bloque 4 (códigos de error y eventos) son estimaciones por ahora: se confirman contra el código al cerrar.
- El perfil de una empresa **nace al guardarlo**, no al registrarse: no hay migración de datos para las empresas existentes.
- **Bloque 1 hecho (2026-11-09)** — Domain 615 (+150), Application 1215 (+63), Infrastructure 879 (+14), Api 965 (sin cambios); migración `AddCompanyProfile` (una tabla y un índice único, revisada a mano). Lo que se decidió o se encontró al hacerlo:
  - **`IsComplete` solo en el DTO**, no en la entidad: como el `PUT` exige los cinco obligatorios, un perfil que existe siempre está completo; el DTO vacío es el único incompleto.
  - **Los objetos de valor son los que garantizan la forma:** `SpanishTaxId` y `SpanishPostalCode` no existen sin ser válidos; `CompanyProfile` solo recibe los ya construidos. Solo se pasan a mayúscula las letras **ASCII**: con `ToUpperInvariant`, la «ſ» (s larga) se habría convertido en una «S» válida (lo destapó una mutación).
  - **Un fallo real encontrado por un test:** con el NIF ausente en el JSON (`null`), el validador lanzaba `NullReferenceException` (un `500`); ahora cada campo da un único error y un valor ausente nunca llega a la regla de forma.
  - **NIF validado contra 8 identificadores reales y públicos** (Telefónica, Santander, Inditex, Iberdrola, Repsol y organismos) además de los calculados: 91 casos fijos en `TaxIdVectors.json` (el frontend copiará el fichero).
  - **Mutaciones: 109/111 detectadas** (`mutate024b1.mjs`: el algoritmo paso a paso, los tipos de entidad, la normalización, el rango de provincias, cada cota, los seis permisos de la tabla, la carrera del alta, el registro sin datos, el esquema real de SQL Server). Dos supervivientes **equivalentes por diseño**: `IsDigit` en lugar de `IsAsciiDigit` en la primera cifra del NIF (la comprobación posterior de cifras ASCII ya lo rechaza) y en el código postal (el rango de provincia 01–52 ya rechaza cualquier cifra de otro alfabeto). Siete mutaciones se replantearon (mal escritas por mi parte, no tests flojos) y una no compilaba y se rehízo.
  - Los NIF especiales **K, L y M** (menores y extranjeros sin NIE) no se admiten por ahora (D6).
  - El perfil se probó contra **SQL Server real** con ocho rondas de cuatro altas simultáneas (queda un solo perfil y nadie recibe un error), el NIF repetido entre empresas sin diferencia observable y el plan de lectura por el índice de la empresa.
