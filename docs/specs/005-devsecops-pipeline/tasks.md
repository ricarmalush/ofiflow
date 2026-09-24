# Tasks — 005-devsecops-pipeline

**Spec relacionada:** [./spec.md](./spec.md)
**ADR relacionada:** [../../adr/ADR-010-pipeline-devsecops.md](../../adr/ADR-010-pipeline-devsecops.md)
**Estado general:** En curso (spec aprobada 2026-09-24)

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
- [x] Gitleaks v8.30.1 con la imagen Docker oficial (`ghcr.io/gitleaks/gitleaks`), repositorio montado en solo lectura, sobre el historial completo (7 commits)
- [x] Un único hallazgo: el secreto JWT de desarrollo antiguo del commit inicial, ya rotado. Documentado en `.gitleaksignore` por su huella exacta. Con el ignore: `no leaks found`.
- [x] Escaneo también del directorio de trabajo: solo claves de sesión de IIS Express en `.vs/`, cifradas para la máquina local, **nunca commiteadas** y cubiertas por `.gitignore` (igual que `bin/`, `obj/` y `*.user`)
- [x] Revisión manual: sin rutas personales, contraseñas ni IPs en ficheros versionados
- [x] **Hallazgo de privacidad:** los 7 commits llevaban el email personal del autor. Antes de publicar se reescribió el autor y el committer al email privado de GitHub (`…@users.noreply.github.com`) con `git filter-branch` solo sobre `master`. Se comprobó que el contenido es idéntico y que el email personal no aparece en ningún mensaje ni contenido. Copia del historial original en la rama local `backup/pre-noreply` (**no se publica**; se borra tras verificar la publicación). `user.email` local configurado al email privado para los commits futuros.
- [x] `SECURITY.md`: versiones soportadas, reporte privado de vulnerabilidades, plazos orientativos y alcance (con prioridad al aislamiento entre tenants)

### Repositorio (≈ 1 h) [externo]
- [x] Repositorio público creado: https://github.com/ricarmalush/ofiflow. Solo se subió `master`. Verificado por la API de GitHub: los 8 commits con email privado (0 con el personal) y sin ficheros sensibles publicados. Después se borró la rama local `backup/pre-noreply` y se purgó el historial antiguo.
- [x] Activados y verificados por la API: secret scanning, push protection, Dependabot alerts y Private Vulnerability Reporting. Alertas abiertas al activar: 0 de secretos y 0 de Dependabot.
- [ ] Proteger `master`: PR obligatorio, checks `ci` y `codeql` obligatorios, sin force-push

### Workflows (≈ 4-6 h)
- [x] `ci.yml`: `setup-dotnet` 10 → `dotnet build` (analizadores de seguridad como error) → `dotnet test` para todas las suites, con la de Infrastructure usando Testcontainers en el Docker del runner → paso de Gitleaks. Con `permissions: contents: read` y acciones fijadas por SHA. **Sin verificar en un runner real todavía** — pendiente de pushear/abrir PR.
- [x] `Directory.Build.props`: `AnalysisModeSecurity=All` + `.editorconfig` (`dotnet_analyzer_diagnostic.category-Security.severity = error`, mecanismo estándar para una categoría completa en vez de listar cada CA en `WarningsAsErrors`). Verificado con `bin`/`obj` limpios: 0 avisos, 0 errores.
- [x] `codeql.yml`: C#, build manual (`dotnet build`), en push/PR y cada semana; resultados en la pestaña Security. **Sin verificar en un runner real todavía.**
- [x] `dast.yml`: SQL Server como service container → `dotnet ef database update` → API con `Jwt__Secret` generado con `openssl rand -base64 32` → esperar a que responda → `zaproxy/action-api-scan` contra `/openapi/v1.json` → subir el informe como artefacto. `continue-on-error: true` mientras dure el modo informe (ADR-010). **Sin verificar en un runner real todavía — es la pieza con más riesgo de necesitar ajustes.**
- [x] `.zap/rules.tsv`: creado vacío a propósito, con la explicación de por qué (no hay informe real todavía del que sacar reglas) — la tarea de rellenarlo con hallazgos reales sigue pendiente, ver "Verificación"
- [x] `dependabot.yml`: ecosistemas `nuget` y `github-actions`, semanal

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
