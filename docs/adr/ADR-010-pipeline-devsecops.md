# ADR-010 — Pipeline DevSecOps y herramientas de seguridad

**Estado:** Aceptado
**Fecha:** 2026-09-24
**Proyecto:** OfiFlow

---

## Contexto

ADR-009 fija **qué** controles de seguridad lleva el código (proteger) y los convierte en errores de compilación y tests de arquitectura. Falta decidir **con qué herramientas se verifica** de forma continua que esos controles se cumplen, y dónde se ejecutan.

Hasta ahora se había decidido de forma consciente **no montar CI** (ver ADR-008: "no front-loading de infraestructura para un CI hipotético"). Hay dos **razones nuevas y explícitas** para reabrirlo:

1. El repositorio pasa a ser **público en GitHub** (decisión del autor, 2026-09-24).
2. OfiFlow se va a usar como **proyecto de portfolio en entrevistas de AppSec/DevSecOps**. Un pipeline de seguridad funcionando es precisamente la competencia que se quiere demostrar.

Punto de partida medido (2026-09-24): con `AnalysisModeSecurity=All`, los analizadores de .NET dan **0 avisos de seguridad** sobre el código actual. Las herramientas no se añaden para arreglar un código inseguro, sino para **mantenerlo** así y **demostrarlo**.

Al ser público, GitHub ofrece gratis funciones que en un repositorio privado son de pago: **CodeQL** (code scanning), **secret scanning** y **push protection**.

## Problema

¿Qué herramientas cubren cada capa (código, dependencias, secretos, aplicación en ejecución) con el menor coste de mantenimiento para un único desarrollador, sin duplicar herramientas que hacen lo mismo?

## Opciones consideradas

**Opción 1: Solo verificaciones locales** (analizadores de .NET, NuGetAudit y los tests de ADR-009)
- Ventajas: cero infraestructura.
- Desventajas: sin DAST, sin escaneo de secretos del historial y sin evidencia visible para terceros.

**Opción 2: Plataforma comercial** (SonarCloud, Snyk, GitGuardian, Burp Enterprise)
- Ventajas: paneles pulidos.
- Desventajas: cuentas y límites de planes gratuitos, dependencia de terceros, y varias herramientas que se solapan.

**Opción 3: GitHub Actions + herramientas open source y nativas de GitHub, una por capa**
- Ventajas: gratis en un repositorio público, todo dentro del repositorio (workflows versionados), resultados en la pestaña *Security* de GitHub en formato SARIF, y estándar del sector (lo que un entrevistador espera ver).
- Desventajas: los runners de GitHub tardan unos minutos por ejecución; ZAP necesita la API arrancada con base de datos dentro del CI.

## Decisión

Se adopta la **Opción 3**, con **una herramienta por capa**:

| Capa | Herramienta | Por qué esta y no otra |
|---|---|---|
| SAST en compilación | Analizadores de .NET (`AnalysisModeSecurity=All`, reglas de seguridad como error) | Nativos, sin dependencias, respuesta inmediata en el IDE |
| SAST profundo | **CodeQL** (`github/codeql-action`, C#) | Gratis en repos públicos, análisis de flujo de datos (taint) de entrada a sumidero, estándar de GitHub. **Se descarta Semgrep** para no duplicar; se reconsidera cuando llegue Angular si CodeQL para TypeScript no basta. |
| SCA (dependencias) | **NuGetAudit** (en compilación, ADR-009 R7) + **Dependabot** (alertas y PRs de actualización semanales) | Nativos de .NET y GitHub |
| Secretos | **GitHub secret scanning + push protection** + **Gitleaks** en CI | Push protection bloquea el secreto *antes* de que llegue a GitHub; Gitleaks cubre el historial completo y reglas personalizadas |
| DAST | **OWASP ZAP API scan** (`zaproxy/action-api-scan`) contra el OpenAPI de la API | Estándar open source, usa el documento OpenAPI que la API ya genera |
| Arquitectura y aislamiento de tenant | Tests xUnit de ADR-009 + `TenantIsolationIntegrationTests` (Testcontainers) | Ya existen o están previstos; los runners Linux de GitHub traen Docker, así que ADR-008 funciona en CI aunque Docker falle en local |

**Estructura del pipeline** (`.github/workflows/`):

```
ci.yml            push/PR a master
  build (con analizadores, EF1002/NU1903/NU1904 como error)
  → test (Domain, Application, Infrastructure con Testcontainers, Api con arquitectura)
  → gitleaks (historial completo)

codeql.yml        push/PR a master + semanal

dast.yml          PR a master + manual
  SQL Server como service container → migraciones → API con Jwt__Secret efímero
  → ZAP API scan contra /openapi/v1.json → informe como artefacto
```

**Política de fallos:**
- Rompen el build: errores de compilación, tests en rojo, cualquier hallazgo de Gitleaks y alertas de CodeQL de severidad alta o crítica.
- ZAP arranca en **modo informe** (no bloquea) hasta ajustar los falsos positivos en `.zap/rules.tsv`. Después, solo bloquean los hallazgos `FAIL`.

**Reglas del repositorio público:**
- `SECURITY.md`: política de divulgación responsable (cómo reportar una vulnerabilidad en privado mediante GitHub Private Vulnerability Reporting).
- Rama `master` protegida: los cambios entran por PR con los checks en verde, el mismo flujo que en IrrigationMaster.
- **Antes de publicar**, Gitleaks sobre el historial completo. El secreto JWT de desarrollo antiguo aparecerá. Ya está rotado (ADR-009 R8) y no protege nada, así que se documenta en `.gitleaksignore` con su justificación, en lugar de reescribir el historial.

## Consecuencias

### Positivas
- Cada capa de ataque (código, dependencias, secretos, aplicación en ejecución) tiene un control automático y visible.
- El test obligatorio de aislamiento de tenant (ADR-002/008) pasa a ejecutarse en cada PR, también mientras Docker no funcione en local.
- Es evidencia verificable para una entrevista: pestaña *Security*, workflows versionados e historial de PRs con checks.

### Negativas / riesgos aceptados
- Se adelanta la infraestructura de CI que ADR-008 había aplazado, por las dos razones nuevas indicadas en el Contexto.
- El DAST en CI necesita SQL Server en contenedor y unos minutos por ejecución. Se limita a PRs y a ejecución manual, no a cada push.
- ZAP sin autenticar solo ve los endpoints anónimos (`/auth/*`) y comprueba que el resto devuelve 401. El escaneo autenticado queda en NEXT.
- En CI el OpenAPI se sirve con `ASPNETCORE_ENVIRONMENT=Development`, porque hoy solo se publica en Development. Se acepta porque es un entorno efímero y aislado.

### Qué queda cerrado (no se debe reabrir sin una razón nueva y explícita)
- Una herramienta por capa: no añadir Semgrep, SonarCloud, Snyk ni similares mientras la tabla anterior cubra la capa.
- Ningún secreto real en el repositorio ni en los workflows: los de CI se generan en cada ejecución o viven en GitHub Actions Secrets.

### Qué queda abierto para revisar más adelante
- **NEXT — ZAP autenticado:** el workflow registra un usuario, hace login e inyecta el JWT para escanear Customers y Jobs.
- **NEXT — Frontend Angular:** `npm audit`, CodeQL para JavaScript/TypeScript y CSP del frontend.
- **LATER — Trivy** (imagen de contenedor e IaC) cuando exista un Dockerfile de despliegue.
- **LATER — OpenSSF Scorecard** y badges de seguridad en el README.
- **LATER — SBOM** (CycloneDX) en cada release.

---

## Relación con otros ADR

- Depende de: ADR-009 (los controles que este pipeline verifica).
- Modifica: ADR-008. Los tests de integración con Testcontainers pasan a ejecutarse en CI; se reabre por las razones nuevas del Contexto.
- Depende de: ADR-002 (el test obligatorio de aislamiento de tenant es un check del pipeline).
