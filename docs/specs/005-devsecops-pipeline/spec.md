# Spec — 005-devsecops-pipeline

**Estado:** Aprobada
**Fecha:** 2026-09-24
**Bounded Context:** Transversal (repositorio, CI/CD; sin cambios de dominio)
**Depende de:** ADR-010 (herramientas y pipeline), ADR-009 (controles verificados), ADR-008 (Testcontainers), `docs/specs/004-security-hardening/`

---

## Objetivo

Publicar OfiFlow como repositorio **público** en GitHub con un pipeline DevSecOps que verifique en cada PR:
- el código (SAST);
- las dependencias (SCA);
- los secretos;
- la aplicación en ejecución (DAST);
- el aislamiento entre tenants.

Tiene dos beneficiarios:
- **El producto:** ninguna regresión de seguridad llega a `master` sin que un check la detecte.
- **El autor:** evidencia verificable y pública de competencias AppSec/DevSecOps para entrevistas.

## Alcance — Qué SÍ incluye

- Revisión del historial con Gitleaks **antes** de publicar, y `.gitleaksignore` justificado para el secreto de desarrollo ya rotado.
- Creación del repositorio público en GitHub, con la primera subida de `master`.
- Configuración del repositorio: secret scanning, push protection, Dependabot alerts, Private Vulnerability Reporting y protección de `master`.
- Workflows `ci.yml`, `codeql.yml` y `dast.yml` según ADR-010.
- `dependabot.yml` (NuGet y GitHub Actions, semanal).
- `SECURITY.md` en la raíz.
- Sección "Seguridad" en el README: qué se verifica y con qué herramienta, con enlace a ADR-009 y ADR-010.

## Alcance — Qué NO incluye

- **NEXT:** ZAP autenticado (con JWT), herramientas para Angular (`npm audit`, CodeQL para JS/TS).
- **NEXT (spec 006):** logs de seguridad estructurados, catálogo de eventos mapeados a MITRE ATT&CK y `docs/seguridad/` (modelo de amenazas STRIDE, informe de auditoría con CVSS y playbooks de respuesta).
- **LATER:** Trivy, OpenSSF Scorecard, SBOM y despliegue continuo (CD).
- **NEVER** (por ahora): herramientas comerciales que dupliquen una capa ya cubierta (ADR-010).

## Reglas / invariantes

- Ningún secreto real en el repositorio ni en los workflows. El `Jwt__Secret` de CI se genera en cada ejecución (`openssl rand`).
- Los workflows usan **permisos mínimos** (`permissions:` explícito en cada job; por defecto `contents: read`).
- Las acciones de terceros van **fijadas por SHA de commit**, no por etiqueta, para protegerse de un ataque a la cadena de suministro. Dependabot se encarga de actualizarlas.
- Un PR no puede fusionarse a `master` con `ci` o `codeql` en rojo.
- ZAP no bloquea mientras está en modo informe; se documenta cuándo pasa a bloqueante.

## Modelo conceptual

No aplica: no hay entidades ni migraciones. Ficheros nuevos:

```
.github/
├── workflows/ci.yml, codeql.yml, dast.yml
└── dependabot.yml
.zap/rules.tsv          ← ajuste de falsos positivos de ZAP
.gitleaksignore         ← el secreto de desarrollo antiguo, rotado y justificado
SECURITY.md
```

## Criterios de aceptación

- Dado el historial completo de git, cuando se ejecuta Gitleaks, entonces solo aparece el secreto de desarrollo antiguo, y queda documentado en `.gitleaksignore` con su justificación. Cualquier otro hallazgo **bloquea la publicación** hasta analizarlo.
- Dado un PR a `master`, cuando se abre, entonces se ejecutan `ci`, `codeql` y `dast`, y los dos primeros son obligatorios para fusionar.
- Dado un PR que introduce `FromSqlRaw` con interpolación, cuando corre `ci`, entonces falla: en compilación (EF1002) y en el test de arquitectura de ADR-009.
- Dado un commit con un secreto de prueba con formato real (por ejemplo, un token de GitHub falso), cuando se intenta hacer push, entonces push protection lo bloquea; y si llegara al repositorio, Gitleaks hace fallar `ci`.
- Dado `TenantIsolationIntegrationTests`, cuando corre `ci` en un runner Linux, entonces pasa en verde con Testcontainers (valida ADR-008 aunque Docker no funcione en local).
- Dada la API arrancada en `dast`, cuando ZAP escanea el OpenAPI, entonces el informe HTML/JSON queda como artefacto del workflow y no tiene hallazgos de riesgo alto sin analizar.
- Dado un paquete NuGet con una vulnerabilidad conocida, cuando Dependabot la detecta, entonces abre un PR de actualización.

**Caso multi-tenant obligatorio:** el test de aislamiento de tenant (ADR-002/008) pasa a ser un check **obligatorio** de cada PR.

## Decisiones técnicas relevantes

- Se apoya en: ADR-010 (todas las herramientas), ADR-009 (controles verificados), ADR-008 (Testcontainers en CI).
- ¿Requiere una ADR nueva? Sí: **ADR-010**, redactado a la vez que esta spec.
- **Acciones con efecto externo:** crear el repositorio público, la primera subida y cambiar la configuración del repositorio en GitHub las **confirma el autor explícitamente** en el momento de hacerlas, no con la aprobación de esta spec.

## Fuera de alcance / Backlog relacionado

- Todo lo marcado NEXT/LATER en ADR-010, "Qué queda abierto".
- Spec 006: logs de seguridad, MITRE ATT&CK y `docs/seguridad/`.

---

## Validación antes de aprobar

> ¿Esta funcionalidad ayuda a conseguir o mantener al primer cliente? (sección 84 del prompt maestro)

**Parcialmente, y se aprueba por una razón adicional explícita.**
- Para el producto, evita regresiones de seguridad antes del piloto, en el que habrá datos personales reales.
- La razón principal de adelantarla es que el repositorio va a ser público y se va a usar como portfolio en entrevistas de AppSec/DevSecOps. Es una decisión consciente del autor (ADR-010, Contexto), no sobreingeniería.
- Coste acotado: unas 8-12 h.
