# Tasks — 005-devsecops-pipeline

**Spec relacionada:** [./spec.md](./spec.md)
**ADR relacionada:** [../../adr/ADR-010-pipeline-devsecops.md](../../adr/ADR-010-pipeline-devsecops.md)
**Estado general:** Pendiente (spec en Borrador, pendiente de aprobación)

---

## Convención

- Cada tarea completable y verificable en una sesión (1-4h).
- Se marca `[x]` solo cuando cumple la Definición de "Done" (sección 86).
- **Prerrequisitos:**
  - la spec 004 implementada, para que los guardarraíles que el pipeline ejecuta existan;
  - el cambio pendiente de Testcontainers (ADR-008) commiteado.
- ⚠️ Las tareas marcadas **[externo]** publican algo o cambian la configuración de GitHub: se piden en el momento de hacerlas y requieren confirmación explícita del autor.

## Checklist

### Antes de publicar (≈ 1 h)
- [ ] Instalar Gitleaks (binario oficial de GitHub Releases) y ejecutar `gitleaks git` sobre el historial completo
- [ ] Analizar cada hallazgo. El esperado es el secreto JWT de desarrollo antiguo, ya rotado: se añade a `.gitleaksignore` con un comentario que lo justifique. **Cualquier otro hallazgo bloquea la publicación.**
- [ ] Revisar a mano que el historial no contiene nada privado (datos personales, cadenas de conexión con contraseña, rutas de clientes reales)
- [ ] `SECURITY.md`: versiones soportadas, cómo reportar una vulnerabilidad (Private Vulnerability Reporting) y tiempo de respuesta orientativo

### Repositorio (≈ 1 h) [externo]
- [ ] Crear el repositorio público `ofiflow` en GitHub y hacer la primera subida de `master`
- [ ] Activar secret scanning, push protection, Dependabot alerts y Private Vulnerability Reporting
- [ ] Proteger `master`: PR obligatorio, checks `ci` y `codeql` obligatorios, sin force-push

### Workflows (≈ 4-6 h)
- [ ] `ci.yml`: `setup-dotnet` 10 → `dotnet build` (analizadores de seguridad como error) → `dotnet test` para todas las suites, con la de Infrastructure usando Testcontainers en el Docker del runner → paso de Gitleaks. Con `permissions: contents: read` y acciones fijadas por SHA.
- [ ] `Directory.Build.props`: `AnalysisModeSecurity=All` y reglas de seguridad `CA2xxx`/`CA3xxx`/`CA5xxx` como error (se verificó el 2026-09-24 que hoy dan 0 avisos)
- [ ] `codeql.yml`: C#, build manual (`dotnet build`), en push/PR y cada semana; resultados en la pestaña Security
- [ ] `dast.yml`: SQL Server como service container → `dotnet ef database update` → API con `Jwt__Secret` generado con `openssl rand -base64 32` → esperar a que responda → `zaproxy/action-api-scan` contra `/openapi/v1.json` → subir el informe como artefacto
- [ ] `.zap/rules.tsv`: analizar el primer informe de ZAP y documentar cada regla que se ignore con su motivo
- [ ] `dependabot.yml`: ecosistemas `nuget` y `github-actions`, semanal

### Verificación (≈ 1-2 h)
- [ ] PR de prueba con `FromSqlRaw` interpolado → `ci` en rojo (EF1002 y test de arquitectura) → cerrar sin fusionar
- [ ] PR de prueba con un secreto falso con formato real → push protection lo bloquea; hacer una captura como evidencia
- [ ] `TenantIsolationIntegrationTests` en verde en el runner
- [ ] Informe de ZAP descargado y revisado, sin riesgos altos sin analizar
- [ ] Sección "Seguridad" en el README: tabla de capas y herramientas, enlaces a ADR-009, ADR-010 y `SECURITY.md`

---

## Notas / bloqueos

- Los PRs de prueba de la verificación son también la **demo para la entrevista**: enseñan un check en rojo por una vulnerabilidad introducida a propósito.
- Estimación total: ≈ 8-12 h.
