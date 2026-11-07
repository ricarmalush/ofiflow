# Capítulo 0 — Qué es OfiFlow

## Qué vas a aprender

- Qué problema real resuelve OfiFlow y para quién.
- Qué conceptos de negocio maneja (empresa, usuario, cliente, trabajo) y cómo se relacionan.
- Qué funciona hoy, qué se puede hacer con la API y qué falta todavía.
- Qué tecnologías se usan, en una frase cada una.

## El problema

Imagina a Marta, una fontanera autónoma. Su día a día:

- Un cliente le escribe por WhatsApp: "tengo una fuga debajo del fregadero".
- Apunta el trabajo en una libreta, o en una hoja de Excel, o en su cabeza.
- Quedan el jueves, pero se le olvida porque no hay agenda.
- Termina el trabajo, tiene que acordarse de hacer un presupuesto, luego una factura, y luego comprobar si le han pagado.

Nada de eso es difícil, pero **es trabajo administrativo disperso** y le quita horas que podría dedicar a trabajar. Lo mismo les pasa a electricistas, técnicos de climatización, jardineros, informáticos y cualquier pequeño negocio de servicios.

## Qué es OfiFlow

OfiFlow es una aplicación web (un **SaaS**: *Software as a Service*, se paga una suscripción y se usa desde internet) que lleva ese flujo completo en un solo sitio:

> **cliente → trabajo → presupuesto → cita → factura → cobro**

Su idea diferenciadora, a largo plazo, es usar inteligencia artificial para reducir al mínimo la burocracia: que Marta pueda escribir "créame un trabajo para Juan, tiene una fuga" y el sistema lo haga. Pero eso es una fase futura. Hoy el proyecto está construyendo los cimientos.

### Multi-tenant: un edificio con muchos pisos

OfiFlow lo usarán muchas empresas a la vez. A cada empresa se la llama **tenant** (inquilino). La imagen que ayuda es la de un edificio de apartamentos:

- Es **un solo edificio** (una sola aplicación y una sola base de datos).
- Cada empresa vive en **su piso** y no puede entrar en el de al lado.

Esto se llama *multi-tenancy*. Lo importante: **los datos de la fontanera Marta jamás deben verse desde la cuenta de otra empresa**, ni siquiera por un fallo de programación. Es la garantía más importante del proyecto y tiene su propio capítulo (el 5).

## Los conceptos del negocio

Hay cinco conceptos. Sus nombres en el código están en inglés, y conviene aprenderlos tal cual:

| Concepto | Qué es | Ejemplo |
|---|---|---|
| **Tenant** | Una empresa que usa OfiFlow | "Fontanería Marta S.L." |
| **User** | Una persona con cuenta (email y contraseña) | Marta, o su empleado Luis |
| **TenantUser** | La relación entre un usuario y una empresa, con un **rol** | Marta es *Owner* de "Fontanería Marta"; Luis es *Technician* |
| **Customer** | Un cliente de esa empresa (persona o empresa) | "Juan Pérez", con su teléfono y dirección |
| **Job** | Un trabajo que se hace para un cliente | "Reparar fuga bajo el fregadero", prioridad *Urgent* |

¿Por qué existe `TenantUser` y no se pone el rol directamente en `User`? Porque **una misma persona puede trabajar en varias empresas con un rol distinto en cada una**. Un técnico autónomo puede ser *Owner* de la suya y *Technician* en la de un amigo. La relación entre persona y empresa es una cosa en sí misma, y por eso tiene su propia tabla.

Los roles que existen (`TenantRole`) son: `Owner`, `Admin`, `Manager`, `Technician` y `Employee`. Cada rol tiene unos **permisos** y el sistema los comprueba en cada operación: un `Technician`, por ejemplo, puede ver clientes y trabajos y empezar o completar un trabajo, pero no crear un cliente ni eliminarlo (recibe un error 403). Es la spec 008. Para probarlo a mano, el `Owner` **invita** a una persona con el rol que quiera y ella acepta la invitación y entra (spec 009): así se pueden ver los 403 de verdad. El `Owner` es el responsable de su empresa (único e intocable); un `Admin` gestiona al equipo pero no a otros `Admin`.

### El ciclo de vida de un trabajo

Un `Job` tiene un **estado** (`JobStatus`) que va cambiando:

```
        ┌── Schedule (reprogramar) ──┐
        │                            │
   New ──Schedule──▶ Scheduled ◀─────┘
    │ ▲                  │
    │ └───Unschedule─────┘
    │                    │
    └────── Start ───────┴──▶ InProgress ──Complete──▶ Completed

   Cancel (desde New, Scheduled o InProgress) ──▶ Cancelled    (un trabajo Completed ya no se puede cancelar)
```

Las reglas actuales:

- Un trabajo recién creado está en `New`.
- Se puede **programar** (`Schedule`) un trabajo en `New` o en `Scheduled` (reprogramarlo): pasa a `Scheduled` con su fecha y, si se sabe, su duración (spec 020).
- Se puede **quitar de la agenda** (`Unschedule`) un trabajo en `Scheduled`: vuelve a `New` y pierde fecha y duración.
- Se puede **empezar** (`Start`) un trabajo en `New` o en `Scheduled` (un trabajo no tiene por qué planificarse). Si estaba programado, **conserva su fecha**.
- Solo se puede **completar** (`Complete`) un trabajo en `InProgress`.
- Se puede **cancelar** (`Cancel`) un trabajo salvo que ya esté `Completed`. Un trabajo cancelado conserva su fecha, pero deja de salir en la agenda.

El enumerado también incluye el estado `Pending`, pero **todavía no hay transiciones que lleven a él**: un estado sin una regla de negocio que lo use sería un estado muerto (ADR-022).

**La fecha de un trabajo es un instante, no una hora de pared** (spec 020, ADR-022): entra con su desfase horario (`2026-11-10T09:00:00+01:00` o terminada en `Z`), la API la guarda en UTC y la devuelve siempre en UTC con la `Z`. Una fecha **sin** desfase se rechaza con un `400`, porque la API no sabe en qué huso la escribió quien la mandó y adivinarlo sería un error silencioso de horas. Qué es «hoy» y «esta semana» lo decide el navegador, no el servidor.

Estas reglas viven en un solo sitio, el código de [`Job`](../../src/OfiFlow.Domain/Jobs/Job.cs), de modo que da igual por dónde llegue la petición: nunca se puede completar un trabajo que no se ha empezado. En el capítulo 2 verás por qué.

## Qué se puede hacer hoy

Este repositorio es **la API** (un servidor al que se le hacen peticiones HTTP y responde con JSON). Las pantallas viven en **otro repositorio**, `ofiflow-frontend` (Angular): hoy tiene el acceso, **crear una cuenta**, la recuperación de contraseña, clientes, trabajos, la agenda, el equipo y la seguridad de la cuenta; la app móvil llegará en una fase posterior. Estas son todas las operaciones que existen:

| Grupo | Operación | Ruta |
|---|---|---|
| **Cuenta** (pública, sin iniciar sesión) | Pedir crear una cuenta (siempre `202`, no crea nada todavía; spec 021) | `POST /api/v1/auth/registration` |
| | Completar el registro con el enlace del correo y elegir la contraseña: **aquí nace la cuenta** (spec 021) | `POST /api/v1/auth/registration/complete` |
| | Iniciar sesión | `POST /api/v1/auth/login` |
| | Renovar la sesión | `POST /api/v1/auth/refresh` |
| | Cerrar la sesión (revoca el refresh token; spec 013) | `POST /api/v1/auth/logout` |
| | Aceptar una invitación (elige nombre y contraseña y acepta las condiciones de uso) | `POST /api/v1/auth/accept-invitation` |
| | Recuperar una contraseña olvidada: pedir el enlace / elegir la nueva (spec 018) | `POST /api/v1/auth/forgot-password`, `POST /api/v1/auth/reset-password` |
| | Segundo paso del login (cuentas con segundo factor) | `POST /api/v1/auth/mfa/verify` |
| **Mi cuenta** (requiere sesión; spec 012) | Quién soy: mis datos, mi empresa, mi rol **efectivo** y mis permisos; y desde la spec 022, **si mi rol exige el segundo factor y todavía no lo tengo** (`secondFactorEnrollmentRequired`) y **si mi rol lo exige, lo tenga o no** (`secondFactorRequired`). Es una de las cuatro operaciones que puede hacer quien debe activar el factor y no lo ha hecho | `GET /api/v1/account/me` |
| | Cerrar **todas** mis sesiones, en todos mis dispositivos (spec 014) | `DELETE /api/v1/account/sessions` |
| | Ver mis sesiones activas: dispositivo, cuándo empezó, última actividad y cuál es esta (spec 016) | `GET /api/v1/account/sessions` |
| | Cerrar **una** de mis sesiones, la de otro dispositivo o la actual (spec 016) | `DELETE /api/v1/account/sessions/{id}` |
| **Clientes** (requiere sesión) | Crear / ver uno / listar / modificar / eliminar | `POST`, `GET`, `GET`, `PUT`, `DELETE` en `/api/v1/customers` |
| **Equipo** (requiere sesión; spec 009) | Invitar / ver pendientes / revocar | `POST`, `GET`, `DELETE` en `/api/v1/invitations` |
| | Ver miembros / cambiar el rol / quitar a uno | `GET /api/v1/users`, `PUT /api/v1/users/{id}/role`, `DELETE /api/v1/users/{id}` |
| | Reiniciar el segundo factor de un miembro (spec 010) | `DELETE /api/v1/users/{id}/mfa` |
| **Segundo factor** (requiere sesión; spec 010) | Ver el estado / pedir el secreto / confirmar / desactivar / regenerar los códigos de recuperación. **Es obligatorio para `Owner` y `Admin` (spec 022):** hasta activarlo solo pueden hacer **ver el estado, pedir el secreto y confirmarlo** (más «quién soy»); todo lo demás da `403 mfa.enrollment_required`; y **no pueden desactivarlo** (`400 mfa.required_for_role`) | `GET`, `POST /setup`, `POST /enable`, `POST /disable`, `POST /recovery-codes` en `/api/v1/account/mfa` |
| **Trabajos** (requiere sesión) | Crear / ver uno / listar / modificar | `POST`, `GET`, `GET`, `PUT` en `/api/v1/jobs` |
| | Empezar / completar / cancelar | `POST /api/v1/jobs/{id}/start`, `/complete`, `/cancel` |
| | Asignar a un técnico | `POST /api/v1/jobs/{id}/assign` |
| | Programar / reprogramar (fecha y duración) y quitar de la agenda (spec 020) | `PUT /api/v1/jobs/{id}/schedule`, `DELETE /api/v1/jobs/{id}/schedule` |
| | La agenda: los trabajos con fecha en un rango de hasta 42 días (spec 020) | `GET /api/v1/jobs/agenda?from=&to=` |
| **Plataforma** (solo operadores, otra sesión; spec 011) | Iniciar sesión en dos pasos (pública) | `POST /api/v1/platform/auth/login`, `POST /api/v1/platform/auth/verify` |
| | Listar / suspender / reactivar empresas (requiere sesión de plataforma) | `GET /api/v1/platform/tenants`, `POST /api/v1/platform/tenants/{id}/suspend`, `/reactivate` |

Una cosa que sorprende al principio: **crear una cuenta crea una empresa nueva**. Pides el registro indicando el nombre de la empresa, tu nombre, tu email y que aceptas las condiciones de uso; te llega un correo con un enlace, y **al abrirlo eliges tu contraseña** y el sistema crea de una vez el `Tenant` (tu empresa), el `User` (tú) y el `TenantUser` que te convierte en `Owner` de esa empresa. **Hasta que abres el enlace no existe ninguna cuenta**: es lo que impide que alguien registre tu email y se quede con ella (capítulo 6, Parte 10). Por eso crear una cuenta necesita el correo configurado.

Después, al iniciar sesión recibes un **token** (un JWT, se explica en el capítulo 6). Ese token dice quién eres y en qué empresa estás trabajando, y se envía en cada petición posterior. Así el sistema sabe qué datos puede enseñarte.

Para ver todas las rutas en vivo, al arrancar la aplicación en modo desarrollo se publica su descripción en OpenAPI (cómo arrancarla, en el capítulo 1). Y para **probarlas a mano** hay una colección lista en [`src/OfiFlow.Api/OfiFlow.Api.http`](../../src/OfiFlow.Api/OfiFlow.Api.http), que se abre en Visual Studio y recorre la cuenta, los clientes, los trabajos y los errores típicos.

## Qué tecnologías se usan

No hace falta dominarlas ahora; cada una se explica cuando toca. Es solo para que los nombres no te suenen a chino:

| Tecnología | Para qué se usa en OfiFlow |
|---|---|
| **.NET 10 / C#** | El lenguaje y la plataforma sobre la que está hecho todo |
| **ASP.NET Core (Minimal API)** | Recibir las peticiones HTTP y devolver las respuestas |
| **SQL Server + EF Core** | Guardar los datos. EF Core es un traductor: tú escribes C# y él genera el SQL |
| **MediatR** | Un "cartero" interno: cada operación (crear un trabajo, por ejemplo) es un mensaje que llega a su manejador |
| **FluentValidation** | Comprobar que los datos que llegan son válidos antes de usarlos |
| **JWT + `PasswordHasher` de ASP.NET Core** | Guardar las contraseñas de forma segura (hash) e iniciar sesión recordando quién eres sin guardar la sesión en el servidor |
| **TOTP (RFC 6238) + AES-GCM** | El segundo factor: el código de 6 dígitos de la app del móvil, con su secreto guardado cifrado |
| **xUnit + Testcontainers** | Los tests. Testcontainers arranca un SQL Server real en Docker para probar contra una base de datos de verdad |
| **GitHub Actions** | Ejecutar tests y comprobaciones de seguridad automáticamente en cada cambio |

## Un único desarrollador: por qué importa

OfiFlow lo construye **una sola persona** con unas 25 horas a la semana, con un horizonte de dos años. Eso condiciona casi todas las decisiones: se prefiere lo simple y mantenible a lo sofisticado, y se evita montar una infraestructura grande que solo tendría sentido con un equipo grande. Verás esa idea repetirse en los ADR ("no sobredimensionar") y en el capítulo 2.

## Qué NO hace todavía

Para no hacerte una idea equivocada, esto es lo que **aún no existe**:

- **App móvil, y unos textos legales:** no hay todavía app móvil. Y crear una cuenta ya funciona desde la web (spec 021), pero **las condiciones de uso y la política de privacidad que se aceptan al registrarse aún no están redactadas**: las escribe el dueño del proyecto, la API solo compara su versión y la pantalla las nombra sin enlazarlas hasta que existan. Hay que tenerlas antes de abrir el registro a clientes reales.
- **Agenda completa, presupuestos, facturas y pagos:** la agenda **mínima** existe desde la spec 020 (programar un trabajo y ver la semana), pero faltan los conflictos de horario, el huso horario por empresa, la vista de mes y arrastrar y soltar. Presupuestos, facturas y pagos son las fases 3 y 4 del roadmap.
- **Inteligencia artificial, WhatsApp y voz:** fases 6 a 9.
- **Invitar por email, y que una persona pertenezca a varias empresas:** las invitaciones funcionan (spec 009) y, con el correo configurado, **se envían por correo** (spec 017; sin correo configurado el token se le entrega a quien invita, como antes). Falta elegir proveedor de correo y dominio, y que una cuenta pueda pertenecer a varias empresas: hoy cada cuenta pertenece a una sola.
- **Del panel de operador de la plataforma** (spec 011) falta lo que la propia spec dejó fuera: plan o tarifa, el motivo y el aviso de una suspensión, buscar por nombre, varios operadores con permisos distintos y la aprobación entre dos. Lo que existe (listar, suspender y reactivar con segundo factor obligatorio) está en los capítulos 6 y 8.
- **Que un técnico solo vea los trabajos que tiene asignados:** hoy un `Technician` puede ver y ejecutar cualquier trabajo de su empresa, no solo los suyos.
- ***Passkeys*, cambiar la contraseña estando dentro y recuperar a un `Owner` que lo pierde todo:** el segundo factor (TOTP) es **obligatorio para `Owner` y `Admin`** (spec 022) y opcional para el resto, junto con el bloqueo de cuenta por intentos fallidos. Lo que falta: el TOTP no resiste al *phishing* (las *passkeys* sí); y un `Owner` que pierde el móvil **y** los códigos de recuperación sigue sin poder recuperarse solo (hace falta la herramienta de plataforma). Queda además la **ventana de enrolamiento**: una cuenta que aún no ha activado el factor puede ser tomada por quien conozca su contraseña activándolo él (ver capítulo 8). La contraseña olvidada **sí se puede recuperar** (spec 018: un enlace por correo, de un solo uso, que al usarse cierra todas las sesiones); lo que todavía no se puede es cambiarla estando dentro (spec 019).

El roadmap completo (fases 0 a 12) está en el [README del repositorio](../../README.md#roadmap-por-fases). Las fases 0 y 1 (arquitectura y MVP: empresa, usuario, cliente y trabajo) están hechas.

## Para comprobar que lo has entendido

1. ¿Qué significa que OfiFlow sea *multi-tenant*, y cuál es la garantía más importante que de ahí se deriva?
2. ¿Por qué existe `TenantUser` en vez de poner el rol dentro de `User`?
3. Marta crea un trabajo y lo completa sin haberlo empezado. ¿Qué pasa y dónde está escrita la regla que lo impide?
4. ¿Qué crea el sistema, exactamente, cuando alguien completa su registro, y cuándo existe la cuenta?

*(Respuestas: 1 = muchas empresas comparten aplicación y base de datos, y los datos de una nunca deben verse desde otra. 2 = una persona puede estar en varias empresas con roles distintos. 3 = el sistema rechaza la operación porque solo se puede completar un trabajo `InProgress`; la regla está en `Job.Complete()`. 4 = un `Tenant`, un `User` (con las condiciones aceptadas) y un `TenantUser` con rol `Owner`, junto con la cuenta de acceso, en una sola confirmación; y la cuenta no existe hasta que se abre el enlace del correo.)*

## Siguiente capítulo

El capítulo 2 explica **cómo está construido por dentro**: qué estilo de arquitectura sigue y por qué.
