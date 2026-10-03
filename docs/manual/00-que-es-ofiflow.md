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

Los roles que existen (`TenantRole`) son: `Owner`, `Admin`, `Manager`, `Technician` y `Employee`. **Aviso:** hoy los roles se guardan, pero todavía no se comprueban para limitar lo que cada uno puede hacer. Eso está pendiente (ver "Qué NO hace todavía").

### El ciclo de vida de un trabajo

Un `Job` tiene un **estado** (`JobStatus`) que va cambiando:

```
   New ──Start──▶ InProgress ──Complete──▶ Completed
    │                 │
    └──── Cancel ─────┴──▶ Cancelled      (un trabajo Completed ya no se puede cancelar)
```

Las reglas actuales:

- Un trabajo recién creado está en `New`.
- Solo se puede **empezar** (`Start`) un trabajo en `New`.
- Solo se puede **completar** (`Complete`) un trabajo en `InProgress`.
- Se puede **cancelar** (`Cancel`) un trabajo salvo que ya esté `Completed`.

El enumerado también incluye los estados `Pending` y `Scheduled`, pero **todavía no hay transiciones que lleven a ellos**: están reservados para la agenda de la fase 2.

Estas reglas viven en un solo sitio, el código de [`Job`](../../src/OfiFlow.Domain/Jobs/Job.cs), de modo que da igual por dónde llegue la petición: nunca se puede completar un trabajo que no se ha empezado. En el capítulo 2 verás por qué.

## Qué se puede hacer hoy

OfiFlow, por ahora, es **solo una API** (un servidor al que se le hacen peticiones HTTP y responde con JSON). No tiene todavía pantallas: el frontend en Angular y la app móvil llegarán en fases posteriores. Estas son todas las operaciones que existen:

| Grupo | Operación | Ruta |
|---|---|---|
| **Cuenta** (pública, sin iniciar sesión) | Registrarse | `POST /api/v1/auth/register` |
| | Iniciar sesión | `POST /api/v1/auth/login` |
| | Renovar la sesión | `POST /api/v1/auth/refresh` |
| **Clientes** (requiere sesión) | Crear / ver uno / listar / modificar / eliminar | `POST`, `GET`, `GET`, `PUT`, `DELETE` en `/api/v1/customers` |
| **Trabajos** (requiere sesión) | Crear / ver uno / listar / modificar | `POST`, `GET`, `GET`, `PUT` en `/api/v1/jobs` |
| | Empezar / completar / cancelar | `POST /api/v1/jobs/{id}/start`, `/complete`, `/cancel` |
| | Asignar a un técnico | `POST /api/v1/jobs/{id}/assign` |

Una cosa que sorprende al principio: **registrarse crea una empresa nueva**. Al registrarte indicas el nombre de la empresa, tu nombre, tu email y tu contraseña, y el sistema crea de una vez el `Tenant` (tu empresa), el `User` (tú) y el `TenantUser` que te convierte en `Owner` de esa empresa.

Después, al iniciar sesión recibes un **token** (un JWT, se explica en el capítulo 6). Ese token dice quién eres y en qué empresa estás trabajando, y se envía en cada petición posterior. Así el sistema sabe qué datos puede enseñarte.

Para ver todas las rutas en vivo, al arrancar la aplicación en modo desarrollo se publica su descripción en OpenAPI (cómo arrancarla, en el capítulo 1).

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
| **xUnit + Testcontainers** | Los tests. Testcontainers arranca un SQL Server real en Docker para probar contra una base de datos de verdad |
| **GitHub Actions** | Ejecutar tests y comprobaciones de seguridad automáticamente en cada cambio |

## Un único desarrollador: por qué importa

OfiFlow lo construye **una sola persona** con unas 25 horas a la semana, con un horizonte de dos años. Eso condiciona casi todas las decisiones: se prefiere lo simple y mantenible a lo sofisticado, y se evita montar una infraestructura grande que solo tendría sentido con un equipo grande. Verás esa idea repetirse en los ADR ("no sobredimensionar") y en el capítulo 2.

## Qué NO hace todavía

Para no hacerte una idea equivocada, esto es lo que **aún no existe**:

- **Pantallas:** no hay frontend; solo la API.
- **Agenda, presupuestos, facturas y pagos:** son las fases 2 a 4 del roadmap.
- **Inteligencia artificial, WhatsApp y voz:** fases 6 a 9.
- **Control de permisos por rol:** los roles se guardan pero no se comprueban todavía.
- **Bloqueo de cuentas tras varios intentos fallidos de login:** hoy solo hay un límite de peticiones por IP.

El roadmap completo (fases 0 a 12) está en el [README del repositorio](../../README.md#roadmap-por-fases). Las fases 0 y 1 (arquitectura y MVP: empresa, usuario, cliente y trabajo) están hechas.

## Para comprobar que lo has entendido

1. ¿Qué significa que OfiFlow sea *multi-tenant*, y cuál es la garantía más importante que de ahí se deriva?
2. ¿Por qué existe `TenantUser` en vez de poner el rol dentro de `User`?
3. Marta crea un trabajo y lo completa sin haberlo empezado. ¿Qué pasa y dónde está escrita la regla que lo impide?
4. ¿Qué crea el sistema, exactamente, cuando alguien se registra?

*(Respuestas: 1 = muchas empresas comparten aplicación y base de datos, y los datos de una nunca deben verse desde otra. 2 = una persona puede estar en varias empresas con roles distintos. 3 = el sistema rechaza la operación porque solo se puede completar un trabajo `InProgress`; la regla está en `Job.Complete()`. 4 = un `Tenant`, un `User` y un `TenantUser` con rol `Owner`.)*

## Siguiente capítulo

El capítulo 2 explica **cómo está construido por dentro**: qué estilo de arquitectura sigue y por qué.
