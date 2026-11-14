# OfiFlow

## Qué es

OfiFlow es una plataforma SaaS para **autónomos, técnicos y pequeñas empresas de servicios** (fontanería, electricidad, climatización, mantenimiento, reparaciones, informática, jardinería, construcción y otros servicios profesionales).

Propuesta de valor:

> **"Convierte tu trabajo diario en un flujo automático: cliente → trabajo → presupuesto → cita → factura → cobro."**

OfiFlow no quiere ser "otro CRM". Su diferenciador es usar **IA para reducir al mínimo el trabajo administrativo**: el profesional puede interactuar por texto, voz, WhatsApp, fotos o documentos, y la IA interpreta la intención y ejecuta acciones sobre el sistema respetando siempre tenant, usuario, rol, permisos, reglas de negocio y auditoría.

Ejemplos de lo que debe poder hacer:

- *"Créame un trabajo para Juan, tiene una fuga debajo del fregadero."*
- *"¿Qué trabajos tengo mañana?"*
- *"He terminado el trabajo de Pedro, fueron dos horas y cambié el condensador."*

## Modelo de negocio

SaaS con suscripción **por usuario/seat** (no por empresa):

| Plan | Precio orientativo | Incluye |
|---|---|---|
| Starter | ~19 €/usuario/mes | Clientes, trabajos, agenda, presupuestos, facturas, IA básica |
| Professional | ~29 €/usuario/mes | + IA avanzada, WhatsApp, automatizaciones, voz, documentos |
| Business | ~39-49 €/usuario/mes | + equipos, permisos avanzados, estadísticas, API |
| Enterprise | A medida | — |

Más adelante, consumo adicional (usage-based billing) para IA, WhatsApp, SMS y almacenamiento.

**Objetivo a finales de 2028:** 100-500 empresas de pago, priorizando product-market fit sobre crecimiento artificial.

## Equipo y plazos

- **Desarrollador:** una única persona (el autor de este repo).
- **Disponibilidad:** ~25h/semana.
- **Horizonte:** enero 2027 – diciembre 2028 (24 meses, ~2.000-2.300 horas útiles).

Esta limitación condiciona todas las decisiones técnicas del proyecto: se prioriza simplicidad, mantenibilidad y evitar sobreingeniería sobre soluciones pensadas para equipos grandes.

## Arquitectura

- **Clean Architecture** + **DDD** + **CQRS pragmático** + **Modular Monolith** (no microservicios, no Event Sourcing).
- **.NET 10**, **SQL Server** + **EF Core**, **ASP.NET Core Identity** + **JWT**, **MediatR**.
- Multi-tenant desde el primer commit: una única base de datos, aislamiento por `TenantId` + Global Query Filter de EF Core.

Todas las decisiones arquitectónicas están documentadas como ADR en [`docs/adr/`](./docs/adr/); el índice con el estado de cada una está en [`docs/README.md`](./docs/README.md). No se reabren sin una razón nueva y explícita — ver cada ADR para su contexto, opciones consideradas y consecuencias.

Si llegas nuevo al proyecto, empieza por el [**manual técnico**](./docs/manual/README.md): explica de qué va la aplicación y cómo está construida, pensado para un programador junior.

## Seguridad

La seguridad no depende de que alguien se acuerde: está en el código (ADR-009) y se verifica en cada Pull Request (ADR-010). `master` está protegida: solo se cambia por PR, y `build-and-test`, `gitleaks`, `analyze` (CodeQL) y `zap-scan` tienen que estar en verde para poder fusionar — la regla vale también para el propietario del repositorio. Cada capa tiene **una** herramienta, sin solaparse:

| Capa | Qué comprueba | Herramienta |
|---|---|---|
| Código (SAST, en compilación) | Toda la categoría *Security* de los analizadores de .NET es **error de compilación** (inyección SQL con `FromSqlRaw` interpolado, criptografía débil...) | Analizadores de .NET (`AnalysisModeSecurity=All` + `.editorconfig`) |
| Código (SAST, flujo de datos) | De la entrada del usuario a un sumidero peligroso | CodeQL (en cada PR y cada semana) |
| Dependencias (SCA) | Paquetes NuGet con vulnerabilidades conocidas: alta/crítica rompe la compilación | NuGetAudit + Dependabot |
| Secretos | Ningún secreto en el repositorio ni en el historial | GitHub secret scanning + push protection + Gitleaks |
| Aplicación en ejecución (DAST) | La API arrancada con SQL Server real, escaneada contra su OpenAPI | OWASP ZAP (API scan) |
| Aislamiento entre empresas | Un usuario de un tenant nunca ve datos de otro, contra SQL Server real | `TenantIsolationIntegrationTests` (Testcontainers) |

Dentro de la propia API: rate limiting en autenticación, cabeceras de seguridad y HSTS, validación con longitudes máximas, contrato de errores estable (`ProblemDetails` con `code`, sin filtrar tipos internos) y el secreto JWT fuera del repositorio (`user-secrets` en local).

- Decisiones y razones: [ADR-009](./docs/adr/ADR-009-seguridad-base.md) (qué controles lleva el código) y [ADR-010](./docs/adr/ADR-010-pipeline-devsecops.md) (con qué se verifican).
- Cómo reportar una vulnerabilidad: [`SECURITY.md`](./SECURITY.md) (reporte privado de GitHub, no un issue público).

## Roadmap por fases

| Fase | Contenido |
|---|---|
| 0 | Arquitectura (ADRs, esqueleto de solución, Identity, Multi-tenancy) — **hecha** |
| 1 | MVP: Tenant, User, TenantUser, Customer, Job — **hecha**; y desde la spec 021 **una persona puede crear su cuenta sola** desde la web (pedir el registro, abrir el enlace del correo y elegir su contraseña), aceptando las condiciones de uso; y desde la spec 022 **el segundo factor es obligatorio para `Owner` y `Admin`**; y desde la spec 019 **cualquier persona con sesión puede cambiar su contraseña** (con la actual y, si lo tiene, un código del segundo factor); y desde la spec 023 **se pueden preparar, enviar y resolver presupuestos** (con el total calculado por el servidor); y desde la spec 024 **cada empresa guarda sus datos fiscales** (nombre fiscal, NIF validado por su dígito de control, dirección), que no llegan nunca a un registro |
| 2 | Agenda, estados de trabajo, asignación de técnicos — **agenda mínima hecha** (spec 020: programar un trabajo y ver la semana); faltan los conflictos de horario y el huso por empresa |
| 3 | Presupuestos, PDF — **presupuestos hechos** (spec 023: crear, editar, enviar, aceptar y rechazar, con el total calculado por el servidor) y **los datos de la empresa que emite los documentos** (spec 024: nombre fiscal, NIF/NIE/CIF validado, dirección, teléfono y email); y desde la spec 025 **se descargan en PDF**, a nombre de la empresa y con la marca BORRADOR si lo son; falta el envío por correo (spec 026) |
| 4 | Facturas, pagos, seguimiento |
| 5 | .NET MAUI (app móvil para el técnico) |
| 6 | IA básica (crear trabajo por lenguaje natural, consultas, presupuestos asistidos) |
| 7 | WhatsApp Business |
| 8 | Chatbot por tenant |
| 9 | Voz |
| 10 | Automatizaciones |
| 11 | Internacionalización |
| 12 | Escalabilidad avanzada |

Calendario orientativo en [`Calendario del Proyecto.txt`](./docs/producto/Calendario%20del%20Proyecto.txt). Especificación completa del producto en [`ProyectoOFIFLOW.txt`](./docs/producto/ProyectoOFIFLOW.txt).

## Cómo se trabaja (Spec-Driven Development)

Cada feature nueva sigue un ciclo Specify → Plan → Tasks → Implement antes de tocar código:

1. **Specify** — `docs/specs/NNN-nombre-feature/spec.md`: objetivo, qué incluye/qué no, reglas de negocio, criterios de aceptación.
2. **Plan** — se apoya en las ADR existentes, o se cierra una ADR nueva si hace falta una decisión técnica que no esté cubierta.
3. **Tasks** — `docs/specs/NNN-nombre-feature/tasks.md`: checklist en incrementos pequeños (Domain → Application → Infrastructure → API → Tests → Frontend).
4. **Implement** — código, marcando cada tarea al completarse según la Definición de "Done".

Plantillas en [`docs/specs/TEMPLATE-spec.md`](./docs/specs/TEMPLATE-spec.md) y [`docs/specs/TEMPLATE-tasks.md`](./docs/specs/TEMPLATE-tasks.md).

## Arrancar en local

Requisitos: .NET 10 SDK, SQL Server LocalDB (o un SQL Server en Docker) y la herramienta `dotnet-ef`. Para ejecutar los tests hace falta además **Docker** (los de integración levantan un SQL Server real). Guía paso a paso y comprobada: [capítulo 1 del manual](./docs/manual/01-poner-en-marcha.md).

```bash
# 1. Secreto JWT: nunca se guarda en el repositorio (ADR-009 R8). Hay que generarlo una vez por máquina.
dotnet user-secrets set "Jwt:Secret" "$(openssl rand -base64 32)" --project src/OfiFlow.Api

# 1b. Clave que cifra los secretos del segundo factor (spec 010). Sin ella la API no arranca.
dotnet user-secrets set "Mfa:EncryptionKey" "$(openssl rand -base64 32)" --project src/OfiFlow.Api

# 1c. Clave que firma las sesiones de los operadores de la plataforma (spec 011): distinta de Jwt:Secret. Sin ella la API no arranca.
dotnet user-secrets set "Jwt:PlatformSecret" "$(openssl rand -base64 32)" --project src/OfiFlow.Api

# 2. Base de datos
dotnet ef database update --project src/OfiFlow.Infrastructure

# 3. API
dotnet run --project src/OfiFlow.Api
```

**Para crear una cuenta hacen falta además el correo y las condiciones (spec 021).** Ya no existe el registro inmediato: una cuenta nace al abrir el enlace que llega por correo, y **sin correo configurado o sin `Legal:TermsVersion` el registro está cerrado** (responde `202` y no pasa nada). Lo mismo vale para aceptar una invitación (pide la versión de las condiciones). En local, con [Mailpit](https://mailpit.axllent.org/) (sin enviar nada a internet):

```bash
docker run -d --name mailpit -p 127.0.0.1:1025:1025 -p 127.0.0.1:8025:8025 -e MP_SMTP_DISABLE_RDNS=true axllent/mailpit:v1.33.0
dotnet user-secrets set "Email:Provider" "Smtp" --project src/OfiFlow.Api
dotnet user-secrets set "Email:From" "no-reply@ofiflow.example" --project src/OfiFlow.Api
dotnet user-secrets set "Email:LinkBaseUrl" "http://localhost:4200" --project src/OfiFlow.Api
dotnet user-secrets set "Email:Smtp:Host" "127.0.0.1" --project src/OfiFlow.Api
dotnet user-secrets set "Email:Smtp:Port" "1025" --project src/OfiFlow.Api
dotnet user-secrets set "Email:Smtp:Security" "None" --project src/OfiFlow.Api
dotnet user-secrets set "Legal:TermsVersion" "2026-11" --project src/OfiFlow.Api
```

Los correos se leen en <http://localhost:8025>. Cómo crear la primera cuenta sin pantalla: [capítulo 1 del manual](./docs/manual/01-poner-en-marcha.md#crear-tu-primera-cuenta-en-local-spec-021).

Sin `openssl`, en PowerShell (funciona también en Windows PowerShell 5.1):

```powershell
$b = New-Object byte[] 32; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b)
dotnet user-secrets set "Jwt:Secret" ([Convert]::ToBase64String($b)) --project src/OfiFlow.Api
# (para la clave del segundo factor y la de la plataforma, lo mismo con "Mfa:EncryptionKey" y "Jwt:PlatformSecret")
```

Para probar la API a mano, abre [`src/OfiFlow.Api/OfiFlow.Api.http`](./src/OfiFlow.Api/OfiFlow.Api.http) en Visual Studio: es una colección de peticiones en orden (cuenta, clientes, trabajos, errores a propósito y aislamiento entre empresas), con instrucciones al principio.

Si la API no arranca por falta del secreto, el mensaje de error indica qué fuentes de configuración consultó y la ruta exacta en la que buscó `secrets.json`.

## Estructura del repositorio

```
OFIFLOW/
├── README.md
├── docs/
│   ├── README.md                # Índice de ADRs y specs con su estado
│   ├── manual/                  # Manual técnico para programadores junior
│   ├── producto/                # ProyectoOFIFLOW.txt + Calendario del Proyecto.txt
│   ├── adr/                     # ADR-00X-*.md: decisiones de arquitectura
│   └── specs/                   # NNN-feature/spec.md + tasks.md, y plantillas
├── .github/                     # Workflows (ci, codeql, dast) y dependabot.yml
├── .zap/rules.tsv               # Falsos positivos de ZAP, cada uno con su motivo
├── SECURITY.md                  # Política de divulgación de vulnerabilidades
├── Directory.Build.props        # Configuración común a todos los .csproj
├── .editorconfig                # Severidad de los analizadores de seguridad
├── OfiFlow.slnx                 # Solución .NET
├── src/
│   ├── OfiFlow.Domain
│   ├── OfiFlow.Application
│   ├── OfiFlow.Infrastructure
│   └── OfiFlow.Api
└── tests/
    ├── OfiFlow.Domain.Tests
    ├── OfiFlow.Application.Tests
    ├── OfiFlow.Infrastructure.Tests
    └── OfiFlow.Api.Tests
```

## Estado actual

Fases 0 y 1 (MVP) completas: Tenant, User, TenantUser, Customer y Job funcionan de extremo a extremo (JWT, aislamiento de tenant, permisos por rol y gestión del equipo: invitar, aceptar, cambiar roles y quitar miembros; segundo factor con login en dos pasos y bloqueo de cuenta, **obligatorio para `Owner` y `Admin`**). ADR-001 a ADR-028 aceptadas (entre las últimas: la 026, los presupuestos; la 027, el perfil fiscal de la empresa; la 028, el PDF del presupuesto). Implementadas las specs 001 a 004, 006 a 025 (línea base de seguridad, diccionario de errores, aislamiento endurecido, RBAC, gestión de usuarios, segundo factor con bloqueo de cuenta, panel de operador con identidad aparte, sesión propia y segundo factor obligatorio, y «quién soy» —`GET /account/me`, con el rol efectivo y los permisos—, cierre y purga de sesiones, correo e invitaciones, recuperar la contraseña, la agenda mínima —programar un trabajo y ver la semana—, crear una cuenta desde la web, el segundo factor obligatorio para `Owner` y `Admin`, cambiar la contraseña estando dentro, los presupuestos, el perfil fiscal de la empresa y el PDF del presupuesto incluidos); la 005 (pipeline DevSecOps) funciona y tiene pendientes dos pruebas manuales. El frontend web vive en otro repositorio (`ofiflow-frontend`) y ya cubre acceso, crear una cuenta, clientes, trabajos, agenda, presupuestos, equipo y seguridad de la cuenta (segundo factor, sesiones y cambiar la contraseña). Siguiente: el envío por correo (spec 026) de los presupuestos, y las facturas. Detalle en [`docs/README.md`](./docs/README.md).
