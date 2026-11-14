# Tasks — 026-presupuesto-por-correo

**Spec relacionada:** [./spec.md](./spec.md)
**ADR relacionadas:** [ADR-020](../../adr/ADR-020-correo-saliente.md) (el correo saliente: se enmienda), [ADR-028](../../adr/ADR-028-pdf-presupuesto.md) (el PDF que se adjunta), [ADR-026](../../adr/ADR-026-presupuestos.md) (los presupuestos), [ADR-012](../../adr/ADR-012-autorizacion-rbac.md) (`Quotes.Issue`), [ADR-009](../../adr/ADR-009-seguridad-base.md) (datos personales y registro), [ADR-011](../../adr/ADR-011-errores-codigos-diccionario.md) (códigos de error)
**Estado general:** Spec aprobada (2026-11-14). Bloque 1 en curso.

---

## Convención

- Cada tarea completable y verificable en una sesión.
- Se marca `[x]` solo cuando cumple la Definición de «Done»: compila, tests en verde, mutaciones detectadas, comprobado en vivo y manual al día.
- **Nivel de riesgo de las pruebas: alto.** Es el primer correo a un tercero, el primero con adjunto y la operación más abusable hasta ahora. Se aplica la batería completa: el **mensaje recibido leído de vuelta** del servidor SMTP de pruebas (cabeceras, partes, adjunto), el PDF adjunto comparado con el de la descarga, entrada hostil en cada campo, y la comprobación de que **ningún dato personal llega a un registro, un evento o una respuesta**.
- Las mutaciones se hacen a mano sobre el código real, con copia de seguridad y **`touch` al restaurar**; solo cuenta como «detectada» una con tests en rojo (`[FAIL]`); un test colgado o un error de compilación no cuentan. **Una mutación debe quitar de verdad lo que dice quitar.** Los guiones van **en ficheros**. Un solo bucle de mutaciones a la vez.
- Las suites se lanzan **por proyecto, una detrás de otra**. Si `dotnet build` choca con una DLL bloqueada, se compila a una carpeta aparte con `-p:OutDir` y no se mata el proceso que la tiene.
- Verificación en vivo con la receta de siempre (base desechable, API real, servidor SMTP de pruebas real leyendo lo recibido, y en el bloque 3 `ng serve` y el navegador integrado). Los valores de prueba se generan en ejecución.
- **Dependencias nuevas:** ninguna prevista (MailKit/MimeKit ya están y soportan adjuntos). Si alguna aparece, ADR-010.
- **Orden de entrega:** el backend **puede fusionarse solo**. La rama parte de `spec/025-quote-pdf`: **si el PR #70 sigue abierto, el PR de esta spec lo apila encima** (base `spec/025-quote-pdf`) hasta que se fusione, y así sucesivamente con #66.
- **Nada de commit, push ni fusión sin orden expresa para ese paso.**

---

## Bloques

### Bloque 1 — El puerto con adjunto y la plantilla (Application + Infrastructure)
- [ ] **`EmailAttachment`** (Application/Common/Mail): `(FileName, ContentType, Content)`; al construirse valida el **tipo** (lista cerrada: `application/pdf`), el **nombre** (`^[A-Za-z0-9._-]{1,100}$`, sin `..`) y el **tamaño** (1 byte a 5 MB). Un valor fuera de cotas es un error de argumento. Su `ToString()` **no imprime** nombre ni contenido.
- [ ] **`EmailMessage`** gana `Attachment` (opcional) y `ReplyTo` (`Email?`, opcional) **sin romper** a los llamadores existentes (invitación, recuperar contraseña, confirmar email): un test fija que construyen exactamente lo mismo.
- [ ] **`SmtpEmailSender`**: con adjunto construye `multipart/mixed` (texto + PDF, codificación base64) y `Reply-To`; sin adjunto, el mensaje sale **igual que hoy** (test que compara la estructura). Revalida el nombre y el tipo del adjunto y rechaza un `ReplyTo` con caracteres de control **antes de tocar la red** (defensa en profundidad: el puerto es el último sitio).
- [ ] **`QuoteEmail`** (Application/Quotes/Email, función pura): asunto «Presupuesto P-0012» y cuerpo fijo con nombre de la empresa **en una sola línea**, número, total y validez; sin ningún texto de la persona. Casos con nombre hostil (saltos de línea, control, bidi) y con y sin email de la empresa (R11, R13).
- [ ] **Tests:** unidad de `EmailAttachment` (cada cota, cada rechazo), de la plantilla (importes de los 15 casos fijos de totales, texto hostil) y de integración con el servidor SMTP de pruebas leyendo el mensaje recibido (partes, tipo, nombre, `Reply-To`, bytes del adjunto idénticos). Arquitectura: MailKit/MimeKit siguen solo en `Infrastructure/Mail`.
- [ ] **Mutaciones a mano** del bloque (cada regla del adjunto, el `Reply-To`, la comprobación de control, la plantilla fija) y las equivalentes documentadas.
- [ ] **[manual]** Anotar en el capítulo del correo y en el borrador de la ADR-029 lo aprendido.
- [ ] **[externo] Commit del bloque 1, con orden expresa.**

### Bloque 2 — La operación y la API (Domain + Application + Infrastructure + Api)
- [ ] **Domain:** `Quote.EmailedAt` y `Quote.EmailedByUserId` y `RecordEmailSent(nowUtc, userId)`: permitido en **cualquier estado**, no sube `Revision`, no toca líneas ni totales. Invariante de `RolePermissions` (`Quotes.Issue` ⇒ `Quotes.Read` y `Company.Read`). Errores `quote.email_disabled`, `quote.email_requires_items` y `quote.email_not_sent`.
- [ ] **Infrastructure:** configuración EF de las dos columnas y **migración** (`AddQuoteEmailedAt`) sin tocar datos; el test de modelo y el de migración al día.
- [ ] **`QuotePdfBuilder`** (Application): extraer de `GetQuotePdfQueryHandler` la lectura de presupuesto + cliente + perfil y la llamada al renderer; `GetQuotePdf` lo usa **sin cambiar** su comportamiento ni sus tests (R15).
- [ ] **`SendQuoteEmailCommand` + Validator + Handler** (`Quotes.Issue`): orden de R7; `Email` validado y normalizado (R5); correo desactivado (`IsEnabled`); líneas; perfil; PDF; **enviar antes de anotar**; reintento único si la anotación choca; `EmailDeliveryException` → `ExternalServiceException(quote.email_not_sent)` con evento `1411` (solo categoría); éxito → evento `1410`. `ToString()` del comando **sin la dirección**.
- [ ] **`SecurityEventIds`:** `QuoteEmailSent = 1410` y `QuoteEmailFailed = 1411`; el test que fija la lista de eventos al día (54 → 56).
- [ ] **Api:** `POST /api/v1/quotes/{id}/email`; política `quote-email` (30/min/IP) y `QuoteEmailThrottle` (20/h/persona, en memoria, como `InvitationThrottle`); `QuoteDto` gana `emailedAt`; OpenAPI; ejemplo en el `.http`.
- [ ] **Tests de Application:** cada regla y cada error (con el doble del puerto: se comprueba qué mensaje se construye y que **no** se envía en cada rechazo), el orden de comprobaciones, el reintento de concurrencia, `EmailedAt` actualizado en un segundo envío, estado sin cambiar, y **ningún dato personal en los registros** (dirección y nombre de empresa «marcados»).
- [ ] **Tests de Api (extremo a extremo con SQL Server real y el SMTP de pruebas):** roles (Owner/Admin/Manager 200; Employee/Technician 403; sin sesión 401; sin MFA 403), aislamiento entre empresas (presupuesto ajeno = 404 y **nada enviado**), borrador con marca, 15 casos de totales, entrada hostil (cabeceras inyectadas en destinatario, título, notas, cliente y empresa), fallos del servidor de correo (no contesta, rechaza, credenciales, caído → 502 y `emailedAt` sin cambiar), `Provider = None`, límites (21.ª del usuario y 31.ª de la IP → 429 con `Retry-After`), CORS (no cambia), y que **la dirección no está** en logs, eventos ni respuestas.
- [ ] **Arquitectura:** la operación sin `IgnoreQueryFilters`; la lista de endpoints y la tabla de operaciones (58 → 59); las cifras de permisos no cambian.
- [ ] **Mutaciones a mano** del bloque y **comprobación en vivo** (API real + SMTP de pruebas real, leyendo el mensaje recibido y el PDF adjunto contra la descarga).
- [ ] **[manual]** Anotar en el borrador de la ADR-029 y en los capítulos afectados.
- [ ] **[externo] Commit del bloque 2, con orden expresa.**

### Bloque 3 — Frontend (`ofiflow-frontend`)
- [ ] **`QuoteService.sendEmail(id, recipient)`** y el modelo (`emailedAt` en el presupuesto); traducción de cada error (`quote.email_disabled`, `quote.email_requires_items`, `quote.email_not_sent`, `quote.company_profile_incomplete`, `429` con `Retry-After`).
- [ ] **Diálogo «Enviar por correo»** en el presupuesto: dirección precargada con la del cliente (vacía si no la tiene) y editable, validación de email en el formulario, botón desactivado mientras envía (sin doble clic), éxito con aviso y «Enviado por correo el…» en el detalle.
- [ ] **Permisos de pantalla:** el botón solo con `Quotes.Issue`; el resto de la pantalla no cambia.
- [ ] **Tests (specs):** servicio (cada error), componente (precarga, edición, validación, estados de envío, avisos con el enlace a «Datos de la empresa»), y que **la dirección no se guarda** en almacenamiento ni en la URL.
- [ ] **Mutaciones a mano** y **comprobación en el navegador** contra la API real y el SMTP de pruebas.
- [ ] **[manual]** Capítulo del frontend.
- [ ] **[externo] Commit del bloque 3 en `ofiflow-frontend`, con orden expresa.**

### Bloque 4 — Documentación
- [ ] **ADR-029:** opciones y decisión (D1 a D4, D9, D10, D12), el modelo de amenazas del abuso del dominio, riesgos aceptados, lo que cambió al implementar, hallazgos para el tribunal, cifras y recomendación de despliegue (SMTP con dominio propio y SPF/DKIM/DMARC antes de clientes reales). Anotaciones «Ampliada por» en ADR-020 (adjunto controlado; las dos reglas que se enmiendan), ADR-028, ADR-026 y ADR-012.
- [ ] **Manual:** capítulos 0, 3, 7 (70 códigos), 8 (seguridad: correo a terceros, STRIDE, preguntas del tribunal), 9 (cifras y lecciones), 12 (glosario, ADR-029, spec 026), anexo del tribunal y los `README`.
- [ ] **Spec:** estado a «Implementada» con la referencia a la ADR-029; las tareas al día.
- [ ] **Comprobación final:** las cifras del manual contra el código y una ejecución real; enlaces sin romper; cada ruta y nombre de test citados existen; `gitleaks` sin hallazgos sobre los commits de la spec.
- [ ] **[externo] Commit del bloque 4, push de las dos ramas y PR (backend y frontend), cada paso con orden expresa.**
