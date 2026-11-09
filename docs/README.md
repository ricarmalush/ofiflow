# Documentación de OfiFlow

| Carpeta | Qué contiene | Cuándo se crea un fichero nuevo |
|---|---|---|
| [`manual/`](./manual/) | Manual técnico para programadores junior: de qué va la aplicación y cómo está construida | Al escribir o actualizar un capítulo; cuando cambia algo que el manual explica |
| [`producto/`](./producto/) | Especificación completa del producto y calendario | Casi nunca: son los documentos maestros |
| [`adr/`](./adr/) | Decisiones de arquitectura (Architecture Decision Records) | Cuando una feature necesita una decisión técnica que ninguna ADR cubre (paso **Plan** del ciclo SDD) |
| [`specs/`](./specs/) | Una carpeta por feature con `spec.md` + `tasks.md` | Al empezar cada feature (pasos **Specify** y **Tasks** del ciclo SDD) |

## Producto

- [ProyectoOFIFLOW.txt](./producto/ProyectoOFIFLOW.txt): prompt maestro, unas 105 secciones
- [Calendario del Proyecto.txt](./producto/Calendario%20del%20Proyecto.txt): roadmap 2027-2028
- [ideas.md](./ideas.md): ideas de producto que aún no son una spec (hoy: el chatbot que prepara un presupuesto cuando el profesional no contesta; la de «crear cuenta» ya es la spec 021)

## ADRs

| ADR | Título | Estado |
|---|---|---|
| [001](./adr/ADR-001-motor-base-datos.md) | Motor de base de datos | Aceptado |
| [002](./adr/ADR-002-multi-tenancy.md) | Multi-Tenancy | Aceptado |
| [003](./adr/ADR-003-cqrs.md) | CQRS | Aceptado |
| [004](./adr/ADR-004-identity.md) | Identity | Aceptado |
| [005](./adr/ADR-005-modular-monolith.md) | Modular Monolith | Aceptado |
| [006](./adr/ADR-006-persistencia-domain-events.md) | Persistencia con EF Core y Domain Events | Aceptado |
| [007](./adr/ADR-007-identity-refresh-token.md) | Modelo Identity y Refresh Token | Aceptado |
| [008](./adr/ADR-008-testing-integracion.md) | Estrategia de tests de integración contra SQL Server | Aceptado |
| [009](./adr/ADR-009-seguridad-base.md) | Línea base de seguridad (Secure by Design) | Aceptado |
| [010](./adr/ADR-010-pipeline-devsecops.md) | Pipeline DevSecOps y herramientas de seguridad | Aceptado |
| [011](./adr/ADR-011-errores-codigos-diccionario.md) | Errores de dominio, códigos de error y diccionario de mensajes | Aceptado |
| [012](./adr/ADR-012-autorizacion-rbac.md) | Autorización por permisos (RBAC) | Aceptado |
| [013](./adr/ADR-013-gestion-de-usuarios.md) | Gestión de usuarios de una empresa (invitaciones, jerarquía de roles, revalidación por petición) | Aceptado |
| [014](./adr/ADR-014-mfa-y-bloqueo-de-cuenta.md) | Segundo factor (MFA) y bloqueo de cuenta | Aceptado |
| [015](./adr/ADR-015-logs-a-fichero-con-serilog.md) | Logs a fichero con Serilog | Aceptado |
| [016](./adr/ADR-016-panel-de-operador-de-la-plataforma.md) | Panel de operador de la plataforma (identidad aparte, sesión propia, suspensión de empresas) | Aceptado |
| [017](./adr/ADR-017-cors-para-el-frontend-web.md) | CORS para el frontend web (lista cerrada de orígenes, en Program.cs) | Propuesto |
| [018](./adr/ADR-018-sesion-ligada-al-access-token.md) | El access token queda ligado a su sesión (`sid`, comprobado en cada petición; cambia ADR-007) | Aceptado |
| [019](./adr/ADR-019-purga-de-sesiones-y-datos-del-dispositivo.md) | Purga de refresh tokens caducados en segundo plano (primer servicio de fondo) y datos del dispositivo de cada sesión (IP truncada, navegador como etiqueta fija) | Aceptado |
| [020](./adr/ADR-020-correo-saliente.md) | Correo saliente: puerto `IEmailSender`, transporte SMTP (MailKit) y la invitación por correo en vez de entregar el token (cierra el riesgo de ADR-013) | Aceptado |
| [021](./adr/ADR-021-recuperacion-de-contrasena.md) | Recuperar la contraseña: respuesta idéntica y tiempo medido (suelo de 250 ms), cola de correo en memoria, token de una hora y un solo uso, y restablecer cierra todas las sesiones sin tocar el segundo factor | Aceptado |
| [022](./adr/ADR-022-agenda.md) | La agenda: fechas como instante UTC con desfase obligatorio, el día lo decide el navegador (medianoche local, no 168 horas), el permiso `Jobs.Schedule`, un rango obligatorio de hasta 42 días y qué se difiere (conflictos, huso por empresa, concurrencia) | Aceptado |
| [023](./adr/ADR-023-crear-una-cuenta.md) | Crear una cuenta: sin cuenta hasta abrir el enlace del correo (contra el *pre-hijacking*), contraseña elegida al completar, respuesta y tiempo uniformes (suelo de 250 ms medido), condiciones de uso con versión y fecha, y el registro inmediato retirado | Aceptado |
| [024](./adr/ADR-024-segundo-factor-obligatorio.md) | Segundo factor obligatorio para `Owner` y `Admin`: una puerta **por petición** en la API con el rol efectivo (sin token «limitado»), una **lista cerrada** de cuatro operaciones permitidas, no se puede desactivar con un rol que lo exige, el estado del factor en la misma consulta que la pertenencia, y `me` con dos campos nuevos; cierra el límite 3b de ADR-014 | Aceptado |
| [025](./adr/ADR-025-cambiar-contrasena-dentro.md) | Cambiar la contraseña estando dentro: la actual y el código como prueba (sesión robada frente a buzón comprometido), todo o nada en **una sola confirmación**, cerrar las demás sesiones y **conservar esta** (revocar, no borrar), anular los enlaces de recuperación y que no esté en la lista cerrada del segundo factor | Aceptado |
| [026](./adr/ADR-026-presupuestos.md) | Presupuestos: el dinero en **céntimos enteros** calculado solo por el servidor, el IVA **una vez por tipo** (coincide con la factura), un presupuesto enviado **inmutable**, el número por empresa con índice único y reintento, y el control de concurrencia con versión de fila y `Revision` | Aceptado |
| [027](./adr/ADR-027-perfil-fiscal-empresa.md) | Perfil fiscal de la empresa: una **entidad propia de la empresa** (no columnas de `Tenant`), el NIF/NIE/CIF **validado por algoritmo** y guardado normalizado, `GET` vacío y `PUT` que sustituye entero, dos permisos (`Company.Read` y `Company.Manage`) y **ningún dato personal en un registro** (el evento `1409` solo dice quién y cuándo) | Aceptado |

## Specs

| Spec | Feature | Estado |
|---|---|---|
| [001-customer](./specs/001-customer/spec.md) | CRUD de clientes | Implementada |
| [002-tenancy-identity](./specs/002-tenancy-identity/spec.md) | Tenant, usuarios, registro, login y JWT | Implementada |
| [003-job](./specs/003-job/spec.md) | Trabajos y transiciones de estado | Implementada |
| [004-security-hardening](./specs/004-security-hardening/spec.md) | Línea base de seguridad (implementa ADR-009) | Implementada |
| [005-devsecops-pipeline](./specs/005-devsecops-pipeline/spec.md) | Repositorio público y pipeline CI de seguridad (implementa ADR-010) | Aprobada (en curso) |
| [006-error-codes-dictionary](./specs/006-error-codes-dictionary/spec.md) | Códigos de error y diccionario de mensajes (implementa ADR-011) | Implementada |
| [007-tenant-isolation-hardening](./specs/007-tenant-isolation-hardening/spec.md) | Guardarraíles y tests que endurecen el aislamiento entre empresas (amplía ADR-009 R2) | Implementada |
| [008-rbac-authorization](./specs/008-rbac-authorization/spec.md) | Autorización por rol: comprobar los permisos de cada operación (implementa ADR-012) | Implementada |
| [009-user-management](./specs/009-user-management/spec.md) | Invitar y gestionar usuarios de una empresa: invitaciones, cambio de rol y baja con efecto inmediato (implementa ADR-013) | Implementada |
| [010-mfa-and-account-lockout](./specs/010-mfa-and-account-lockout/spec.md) | Segundo factor (TOTP), login en dos pasos y bloqueo de cuenta por intentos (implementa ADR-014) | Implementada |
| [011-platform-operator-panel](./specs/011-platform-operator-panel/spec.md) | Panel de operador de la plataforma: listar, suspender y reactivar empresas con una identidad aparte y segundo factor obligatorio (implementa ADR-016) | Implementada |
| [012-account-me](./specs/012-account-me/spec.md) | «Quién soy»: `GET /account/me` con los datos de la propia cuenta, su empresa, su rol efectivo y sus permisos (amplía ADR-012) | Implementada |
| [013-server-logout](./specs/013-server-logout/spec.md) | Cierre de sesión en el servidor: `POST /auth/logout` revoca el refresh token (amplía ADR-007) | Implementada (API; el frontend es el bloque 5) |
| [014-logout-all](./specs/014-logout-all/spec.md) | Cerrar todas mis sesiones: `DELETE /account/sessions` revoca todos los refresh tokens de la cuenta (amplía ADR-007 y ADR-012) | Implementada (API; el frontend es el bloque 5) |
| [015-access-token-session](./specs/015-access-token-session/spec.md) | El access token deja de valer en cuanto se cierra su sesión: `sid` en el token, comprobado en cada petición (401); `RefreshToken.SessionId` con migración (cambia ADR-007, crea ADR-018) | Implementada (API y frontend) |
| [016-sessions-purge-and-list](./specs/016-sessions-purge-and-list/spec.md) | Purga de refresh tokens caducados (servicio en segundo plano) y ver y cerrar mis sesiones concretas: `GET` y `DELETE /account/sessions/{id}`, con datos del dispositivo (amplía ADR-007 y ADR-018, crea ADR-019) | Implementada |
| [017-email-invitations](./specs/017-email-invitations/spec.md) | Correo de OfiFlow: puerto `IEmailSender` con transporte SMTP y, como primer uso, la invitación de un miembro por correo en lugar de pasar el token a mano (cierra la nota de ADR-013, crea ADR-020) | Implementada |
| [018-password-recovery](./specs/018-password-recovery/spec.md) | Recuperar la contraseña olvidada: `POST /auth/forgot-password` (siempre la misma respuesta) y `POST /auth/reset-password` (cierra todas las sesiones), con una cola de correo en memoria; segundo uso del puerto de correo (amplía ADR-007 y ADR-014, crea ADR-021) | Implementada (API y frontend) |
| [020-agenda-minima](./specs/020-agenda-minima/spec.md) | La agenda mínima: programar, reprogramar y quitar de la agenda un trabajo (`PUT` y `DELETE /jobs/{id}/schedule`, permiso `Jobs.Schedule`), `Start` también desde `Scheduled` y `GET /jobs/agenda` (rango semiabierto de hasta 42 días); en el frontend, la pantalla de la agenda y la sección «Programación» (amplía ADR-006 y ADR-012, crea ADR-022) | Implementada (API y frontend) |
| [021-crear-cuenta](./specs/021-crear-cuenta/spec.md) | Crear una cuenta desde la web: `POST /auth/registration` (siempre la misma respuesta, tiempo medido) y `POST /auth/registration/complete` (la cuenta nace al abrir el enlace del correo, con la contraseña elegida entonces), condiciones de uso aceptadas, registro inmediato retirado y pantallas de registro y de completar; tercer uso del puerto de correo (amplía ADR-007, ADR-009, ADR-013 y ADR-020, crea ADR-023) | Implementada (API y frontend) |
| [022-mfa-obligatorio](./specs/022-mfa-obligatorio/spec.md) | Segundo factor obligatorio para `Owner` y `Admin`: mientras no lo tengan activo su sesión solo sirve para activarlo (`403 mfa.enrollment_required` en lo demás, por petición y sin token nuevo), no pueden desactivarlo (`400 mfa.required_for_role`), `GET /account/me` dice si deben activarlo y el frontend los lleva directos al asistente (crea ADR-024; cierra el límite 3b de ADR-014) | Implementada |
| [019-cambiar-contrasena](./specs/019-cambiar-contrasena/spec.md) | Cambiar la contraseña estando dentro: `PUT /account/password` con la contraseña **actual** (cuenta para el bloqueo) y, si hay segundo factor, un **código**; todo o nada en una confirmación (la contraseña, las demás sesiones cerradas **conservando esta**, los enlaces de recuperación anulados), permiso `Account.ChangePassword`, evento `1023` y una tarjeta en la pantalla de seguridad (crea ADR-025) | Implementada |
| [023-presupuestos](./specs/023-presupuestos/spec.md) | Presupuestos (fase 3): un borrador con líneas, un total que **calcula el servidor** (céntimos enteros, IVA una vez por tipo), un número por empresa, estados borrador → enviado → aceptado o rechazado con el enviado **inmutable**, control de concurrencia, tres permisos (`Quotes.Read`, `Write` e `Issue`), ocho endpoints, y en el frontend el listado y una pantalla con vista previa del total (crea ADR-026) | Implementada |
| [024-perfil-fiscal-empresa](./specs/024-perfil-fiscal-empresa/spec.md) | El perfil fiscal de la empresa (fase 3, prerrequisito del PDF): nombre fiscal, NIF/NIE/CIF validado por su dígito de control, dirección, teléfono y email; uno por empresa que nace al guardarlo, con `GET` y `PUT /api/v1/company/profile`, dos permisos (`Company.Read` y `Company.Manage`), el evento `1409` sin ningún dato personal y, en el frontend, la pantalla «Datos de la empresa» (crea ADR-027) | Implementada |

Plantillas para specs nuevas: [TEMPLATE-spec.md](./specs/TEMPLATE-spec.md) y [TEMPLATE-tasks.md](./specs/TEMPLATE-tasks.md).

**Al crear una ADR o una spec nueva, añádela a estas tablas.**
