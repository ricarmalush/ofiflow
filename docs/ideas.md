# Ideas de producto pendientes

Ideas que **todavía no son una spec**: se anotan para que no se pierdan y para que, el día que toquen, ya estén pensados sus prerrequisitos y sus riesgos. Una idea pasa a spec (`specs/NNN-.../`) cuando se decide hacerla; entonces se mueve aquí su enlace y se deja de mantener este texto.

---

## Chatbot que prepara un presupuesto cuando el profesional no contesta

**Origen:** idea del dueño del proyecto, 2026-10-31. **Estado:** idea; no está en el documento maestro tal cual (el *disparador* es nuevo), pero encaja con piezas que sí están.

### La idea

Un cliente escribe al profesional (por WhatsApp, por ejemplo) y el profesional **no contesta**. En lugar de perder al cliente, el chatbot de la empresa responde con la información de esa empresa y deja preparado un **borrador de presupuesto** para que el profesional lo revise y lo envíe.

### Dónde encaja en el documento maestro (`producto/ProyectoOFIFLOW.txt`)

| Pieza | Sección | Qué dice |
|---|---|---|
| IA para reducir la burocracia | §1 y §23 | La diferenciación del producto. Ejemplo literal: «Prepárame un presupuesto para este cliente». Funciones previstas: `AnalyzeMessage`, `CreateJobFromMessage`, **`GenerateQuote`**, `GenerateResponse` |
| La IA actúa por Commands y Queries | §24 y §25 | Nunca tiene acceso directo e ilimitado a la base de datos |
| Humano en el circuito | §26 | Las operaciones sensibles (enviar comunicaciones, crear facturas, cobrar) requieren confirmación: la IA **propone**, la persona **confirma** |
| WhatsApp | §27 | Cliente → WhatsApp → webhook → procesamiento → IA → respuesta. Detrás de una abstracción (`IMessageProvider`) |
| Chatbot por empresa | §28 | Cada empresa tiene su agente, que conoce solo información autorizada de esa empresa (servicios, horarios, precios, zonas, preguntas frecuentes, disponibilidad, políticas) y **nunca** la de otra |
| Hoja de ruta | §48 | Fase 3: presupuestos. Fase 6: IA básica, con «presupuestos asistidos». Fase 7: WhatsApp. Fase 8: chatbot por empresa |

### Prerrequisitos (nada de esto existe hoy)

1. **Presupuestos** (fase 3): sin ellos no hay qué borrador generar.
2. **Un canal de mensajes** (WhatsApp y/o correo) y **correo** en general, que tampoco existe todavía.
3. **Los datos de cada empresa cargados**: precios, zonas, horarios, qué servicios ofrece. Sin ellos el chatbot inventaría o no sabría responder.
4. **IA básica** (fase 6) y su modelo de seguridad.

### Decisiones abiertas (para el día que se haga la spec)

- **¿Qué significa «no contesta»?** Un plazo sin responder (¿cuánto?), fuera de horario, el profesional marcado como «no disponible», una cola que se desborda… Es el corazón de la idea y está sin definir.
- **¿Borrador o envío automático?** Por defecto, el documento maestro (§26) exige que el profesional confirme. Enviar un presupuesto solo, sin que nadie lo vea, **compromete un precio ante un cliente**: si algún día se permite, debe ser una decisión explícita, por empresa, con límites (importe máximo, tipos de servicio permitidos).
- **¿Qué le promete el chatbot al cliente?** Un plazo de respuesta, un precio orientativo, una cita… cada una tiene consecuencias distintas.
- **¿Quién es «el cliente» para el sistema?** Hoy un cliente final (`Customer`) es un dato de la empresa, no un usuario. El chatbot trataría con una persona **anónima y no confiable**.

### Riesgos que ya se ven

- **Entrada no confiable:** quien escribe al chatbot controla el texto (*prompt injection*). ADR-009 deja la «Seguridad de IA» anotada como pendiente (§35 del maestro: validación de herramientas).
- **Aislamiento entre empresas:** la garantía de «nunca información de otra empresa» pasa a ser crítica en un componente nuevo y con lenguaje natural de por medio.
- **Coste y abuso:** un chatbot expuesto a internet es un endpoint costoso (la plantilla de specs ya pide límite de peticiones para IA).
- **Datos personales (RGPD):** los mensajes de clientes finales son datos personales de personas que no han firmado nada con OfiFlow; hay que decidir qué se guarda, cuánto tiempo y quién los ve.
- **Políticas del proveedor** (WhatsApp Business: plantillas, ventana de 24 horas, consentimiento).
- **Compromiso comercial:** lo que el chatbot diga puede ser reclamado como una oferta.

### Lo que ya tenemos y ayudará

- La autorización se comprueba en Application, no en los endpoints, **para que valga igual venga de HTTP o de la IA** (ADR-012, spec 008).
- Aislamiento entre empresas probado (filtro por empresa, lista blanca de lecturas, tests de aislamiento).
- Eventos de seguridad y registro sin datos sensibles (ADR-009), que la IA tendrá que respetar.
- Límites de peticiones por política y por IP.

### Qué NO hacer todavía

Nada de código ni de spec hasta tener presupuestos y un canal de mensajes. Es posterior al primer piloto con un autónomo.

---

## Crear cuenta desde la web y confirmar el email (registro autoservicio)

**Origen:** observación del dueño del proyecto viendo la pantalla de acceso, 2026-11-06: «¿cómo se registra alguien la primera vez si solo hay "olvidé mi contraseña"?». **Estado:** **la siguiente spec** (número provisional **021**; el 019 sigue reservado a «cambiar la contraseña estando dentro»). No espera a ningún prerrequisito para empezar su parte sin correo; la confirmación del email sí necesita el correo real.

### El hueco (comprobado el 2026-11-06)

- **API:** `POST /api/v1/auth/register` ya existe (spec 002). Recibe el nombre de la empresa, el de la persona, el email y la contraseña, y crea de una vez, en una sola confirmación, la empresa y su primer usuario como `Owner`. Límite de 3 por hora y por IP. **No devuelve tokens**: después hay que iniciar sesión.
- **Frontend:** las rutas públicas de `/auth` son `login`, `accept-invitation`, `forgot-password`, `reset-password`, `access` y `error`. **No hay pantalla de registro, ni enlace a ella, ni llamada a `register`** en ningún servicio.
- **Hoy se entra así:** una persona de una empresa que ya existe, por invitación (spec 017, tiene pantalla). La **primera persona de una empresa nueva**, solo llamando a la API a mano: no lo puede hacer un cliente.

### Qué problema resuelve

Un cliente piloto no puede empezar solo. Es el primer paso del flujo de valor y está sin construir, por delante de agenda, presupuestos o facturas: sin cuenta no hay nada de lo demás.

### Alcance que se propone (a decidir al hacer la spec)

1. **Pantalla `/auth/register`** pública, con enlace desde el acceso («¿No tienes cuenta? Crea una») y desde la de recuperar contraseña si procede. Empresa, nombre, email y contraseña (las reglas de siempre, de 8 a 128). Tras el éxito, al acceso con un aviso. Sin cambios en la API si solo se hace esto.
2. **Confirmar el email**, que cierra el límite conocido de que el registro **revela si un email ya está en uso** (capítulo 6 del manual, límite 3). Un enlace de un solo uso, como las invitaciones y la recuperación. Aquí sí hay API nueva.
3. **Aceptar las condiciones y la política de privacidad** al registrarse (RGPD), con la fecha y la versión aceptadas guardadas.

### Decisiones abiertas

- **¿La confirmación del email va en la misma spec o en otra?** Recomendado: la **misma**, como segundo bloque, porque es lo que hace seguro el registro abierto. El primer bloque (la pantalla) se puede entregar solo.
- **¿Se puede iniciar sesión sin haber confirmado el email?** Recomendado: no, con un mensaje que lo diga y un reenvío del enlace (limitado). Decisión con impacto en la experiencia del piloto.
- **Textos legales:** quién los redacta y dónde viven. **No es una decisión de código**: hay que tenerlos antes de abrir el registro a clientes reales.
- **Abuso:** 3 por hora y por IP puede frenar a una oficina entera que comparta salida a internet, y no frena a quien rota IP. Valorar una comprobación anti-robots más adelante, no ahora.
- **Alternativa: piloto cerrado.** Las empresas las da de alta el equipo de OfiFlow (con una herramienta de administración) y entrega las credenciales. Más rápido, pero no escala ni sirve de demo autoservicio. Se descarta salvo que se quiera retrasar el registro abierto.

### Riesgos que ya se ven

- **Enumeración de cuentas:** mientras no haya confirmación, el registro dice «ese email ya está registrado». Es el límite que esta spec cierra.
- **Empresas falsas o basura:** un registro abierto llena la base de empresas que nadie usa; hace falta saber purgarlas (empresas sin confirmar a los N días).
- **El primer usuario es `Owner` y por ahora sin segundo factor:** coincide con el límite 3b de ADR-014 («MFA obligatorio para `Owner` y `Admin`»), que sigue pendiente y gana importancia con el registro abierto.
- **Datos personales:** nombre y email de quien se registra, y la aceptación de las condiciones (con fecha), nunca en el registro de eventos.
- **Correo saliente:** la confirmación depende del proveedor SMTP con dominio propio y SPF/DKIM/DMARC (ya pendiente antes del piloto).

### Lo que ya tenemos y ayudará

- El registro de la API, atómico, con su límite por IP y probado (spec 002, capítulo 6 del manual).
- Las pantallas de **invitación** y de **recuperar la contraseña** como modelo: el token que se lee del fragmento y se borra de la barra de direcciones, los mensajes que no afirman nada, y la utilidad `readFragmentToken`.
- El puerto de correo, la cola en memoria y las plantillas de correo (ADR-020 y ADR-021).
- La purga de datos caducados (ADR-019) para las empresas sin confirmar.

### Qué NO hacer todavía

No se escribe la spec hasta cerrar la 020 (queda el bloque 4: ADR-022 y manual). Orden previsto: **020 → 021 (crear cuenta y confirmar email)**, y después, ya con el registro abierto, MFA obligatorio para `Owner`/`Admin` y «cambiar la contraseña estando dentro» (019).
