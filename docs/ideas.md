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

**Hecha (2026-11-06):** pasó a la [spec 021](./specs/021-crear-cuenta/spec.md) y la decisión está en la [ADR-023](./adr/ADR-023-crear-una-cuenta.md). Ya no se mantiene el texto de la idea. Lo que el origen pedía —pantalla de registro, confirmar el email (aquí, «sin cuenta hasta abrir el enlace»), aceptar las condiciones con versión y fecha— está hecho; lo que **no** se hizo y sigue abierto (textos legales, comprobación anti-robots, segundo factor obligatorio para `Owner`, que las cuentas ya existentes acepten las condiciones, una cuenta en varias empresas) está en «Qué queda abierto» de la ADR-023.
