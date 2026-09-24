# Política de seguridad

## Versiones soportadas

OfiFlow está en desarrollo activo y todavía no tiene versiones publicadas. Solo se da soporte de seguridad a la rama `master`.

| Versión | Soporte |
|---|---|
| `master` | ✅ |
| Cualquier otra rama o commit anterior | ❌ |

## Cómo reportar una vulnerabilidad

**No abras un issue público** para reportar una vulnerabilidad: quedaría visible antes de que exista una corrección.

Usa el **reporte privado de vulnerabilidades de GitHub**:

1. Ve a la pestaña **Security** de este repositorio.
2. Pulsa **Report a vulnerability**.
3. Describe el problema con el máximo detalle posible:
   - componente afectado (endpoint, fichero o capa);
   - pasos para reproducirlo;
   - impacto que estimas (por ejemplo, acceso a datos de otro tenant, elevación de privilegios, fuga de datos personales);
   - si es posible, una puntuación CVSS orientativa.

Solo el mantenedor verá el reporte.

## Qué esperar

OfiFlow lo mantiene una única persona, así que estos plazos son orientativos:

| Paso | Plazo orientativo |
|---|---|
| Acuse de recibo | 7 días |
| Evaluación inicial y severidad | 14 días |
| Corrección de una vulnerabilidad crítica o alta | Lo antes posible, con prioridad sobre cualquier otro trabajo |

Cuando la corrección esté publicada se reconocerá tu contribución en el aviso de seguridad, salvo que prefieras quedar en el anonimato.

## Alcance

Especialmente relevante, por el diseño multi-tenant del producto:

- Cualquier forma de leer o modificar datos de **otro tenant** (otra empresa).
- Saltarse la autenticación o la autorización.
- Exposición de datos personales (clientes, usuarios) en respuestas, errores o logs.
- Inyección (SQL, comandos, cabeceras, logs).
- Robo o reutilización de tokens (JWT, refresh token).

Fuera de alcance:

- Ataques de denegación de servicio por volumen.
- Hallazgos que requieran acceso físico o una máquina ya comprometida.
- El secreto JWT de desarrollo antiguo del historial de git: está rotado y documentado en `.gitleaksignore`.

## Cómo se protege el proyecto

Las decisiones y controles de seguridad están documentados en:

- [ADR-009 — Línea base de seguridad](./docs/adr/ADR-009-seguridad-base.md): validación de entrada, aislamiento de tenant, rate limiting, logs sin datos personales, cabeceras y gestión de secretos.
- [ADR-010 — Pipeline DevSecOps](./docs/adr/ADR-010-pipeline-devsecops.md): SAST (CodeQL), dependencias (Dependabot, NuGetAudit), secretos (secret scanning, Gitleaks) y DAST (OWASP ZAP) en cada pull request.
