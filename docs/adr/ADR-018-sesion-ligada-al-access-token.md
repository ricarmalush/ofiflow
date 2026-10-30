# ADR-018 — El access token queda ligado a su sesión

**Estado:** Aceptado (2026-10-30). Las decisiones D1–D9 de la [spec 015](../specs/015-access-token-session/spec.md) se aprobaron tal como estaban escritas.
**Fecha:** 2026-10-30
**Proyecto:** OfiFlow

---

## Contexto

Hasta ahora el access token era un JWT **sin estado**: la API solo comprobaba su firma, su emisor, su audiencia y su caducidad (ADR-007, ADR-009). Es rápido y no necesita base de datos, pero tiene un coste que ADR-007 aceptó expresamente: **un token válido no se puede anular antes de que caduque** (30 minutos).

Ese coste fue creciendo en importancia a medida que se añadieron formas de cerrar una sesión:

- La spec 013 permitió cerrar **una** sesión (`POST /auth/logout`).
- La spec 014 permitió cerrar **todas** (`DELETE /account/sessions`), pensada para el caso de «perdí el móvil».
- Activar o desactivar el segundo factor, quitar a un miembro y la detección de reutilización de un refresh token ya cerraban sesiones desde antes.

Todas esas operaciones **revocan los refresh tokens**: nadie puede *renovar*. Pero el access token ya emitido seguía valiendo hasta 30 minutos. En el caso del móvil perdido, el ladrón conservaba media hora de acceso a los datos de los clientes después de que su dueño hubiera pulsado «Cerrar todas mis sesiones». Las dos specs dejaron ese límite escrito como NEXT.

## Problema

¿Cómo hacer que cerrar una sesión corte también el access token ya emitido, sin perder lo que el modelo actual tiene de bueno (una API sin sesiones de servidor tradicionales, un token que identifica a la persona y su empresa)?

El token es un JWT firmado: **no se puede modificar ni revocar desde el servidor**. Cualquier solución pasa por hacer que la API **consulte algo** además de la firma.

## Opciones consideradas

**A. Un identificador de sesión (`sid`) en el token, comprobado contra los refresh tokens activos.** *(elegida)*

Cada sesión (un inicio de sesión y todas sus renovaciones) tiene un Id estable. El token lo lleva como claim. La API acepta el token solo si existe un refresh token **activo** (no revocado y no caducado) de esa sesión y de esa cuenta.

**B. Un instante «sesiones válidas desde» por cuenta.** Una columna por cuenta, comparada con la fecha de emisión del token. Es más simple, pero solo sirve para «cerrar todas»: **cerrar una sola sesión** (spec 013) no se podría invalidar sin cerrar las demás, y no es una base para listar y cerrar sesiones concretas (spec 016).

**C. Acortar la vida del access token** (por ejemplo, 5 minutos). No elimina la ventana, solo la reduce, y multiplica las renovaciones (más carga y más superficie en `/auth/refresh`).

**D. Una lista negra de tokens revocados** hasta su caducidad. Obliga a conocer todos los tokens emitidos y a guardar una fila por cada uno que se revoque; crece con el uso y hay que purgarla.

**E. Comprobar la sesión en el `AuthorizationBehavior`**, junto a la pertenencia. Fundiría las dos comprobaciones, pero un fallo allí se convierte en `403` (`ForbiddenException`), y el interceptor del frontend **solo renueva la sesión o vuelve al login ante un `401`**: el usuario se quedaría ante errores sueltos en cada pantalla.

**F. Una tabla `Sessions` propia**, con su ciclo de vida. Es más limpia para listar sesiones (dispositivo, fecha, última actividad), pero obliga a mantener dos tablas coherentes ahora. Puede nacer en la spec 016 sin deshacer esto.

## Decisión

**Opción A**, con estas piezas:

1. **`RefreshToken.SessionId`** (columna `NOT NULL`, con índice). Iniciar sesión crea un `SessionId` **nuevo**; renovar (`/auth/refresh`) **conserva** el del token anterior. El refresh token anterior se revoca y el nuevo se inserta en la **misma** confirmación a la base de datos: la sesión nunca se queda sin un refresh token activo.
2. **El access token lleva el claim `sid`** (el estándar, `OfiFlowClaimTypes.SessionId`).
3. **La comprobación va en la autenticación**, no en la autorización: el evento `OnTokenValidated` del esquema de las empresas llama a `ITokenService.IsSessionActiveAsync(userId, sessionId)`. Un fallo es un **`401`** sin cuerpo (E descartada).
4. **«Activa»** significa: existe un refresh token con ese `SessionId` **y ese `UserId`** (el `sub` del token), sin revocar y sin caducar. El `UserId` es parte de la pregunta: sin él, el Id de sesión de una cuenta valdría en el token de otra.
5. **Fail-closed:** un token sin `sid`, o con un `sid` que no es un Guid, se rechaza. En la práctica son los emitidos antes de desplegar esto: caducan en 30 minutos y, al recibir el `401`, el frontend los renueva solo con su refresh token.
6. **Migración:** la columna nace anulable, cada fila existente pasa a ser su propia sesión (`SessionId = Id`) y solo entonces se vuelve `NOT NULL`, **sin valor por defecto** (con un Guid vacío por defecto, todas las filas de hoy serían una sola sesión). Una sesión que estaba activa antes de migrar sigue pudiendo renovar.
7. **Índice** en `SessionId` con `UserId`, `RevokedAt` y `ExpiresAt` incluidas: la consulta se responde entera desde el índice.
8. **Un evento de seguridad `1009`** (nivel `Information`) con el motivo (`MissingSessionId` o `SessionNotActive`) y el `UserId` si se conoce. Nunca el token ni el `sid`.
9. **La lectura de `RefreshTokens` sigue en `TokenService.cs`**, la única ruta de la lista blanca del test de arquitectura: el evento llama al servicio, no consulta la tabla.

## Consecuencias

### Positivas

- **Todos los cierres existentes pasan a ser inmediatos, sin tocarlos:** cerrar una sesión (013), todas (014), la detección de reutilización, activar o desactivar el segundo factor, quitar a un miembro y reiniciarle el segundo factor. Todos dejan a la sesión sin refresh token activo, y su access token cae en la petición siguiente.
- **Es por sesión, no por cuenta:** cerrar el portátil no echa al móvil.
- **Es la base de la spec 016** (listar mis sesiones y cerrar una concreta): ya existe el Id de sesión.
- **No cambia ninguna ruta ni permiso:** es una condición previa a todas las de empresa.

### Negativas / riesgos aceptados

- **El access token deja de ser puramente sin estado.** Depende de una fila de la base de datos. Es el precio de poder revocarlo; ADR-007 ya había aceptado lo contrario (que no se pudiera) y esta ADR lo revierte conscientemente.
- **Una consulta más por petición autenticada** (la segunda, tras la de pertenencia de ADR-013). Medido en local con la API real, 300 peticiones a `GET /account/me` contra la versión anterior:

  | | mediana | p95 | media |
  |---|---|---|---|
  | Antes (sin comprobar la sesión) | 12,17 ms | 30,06 ms | 13,77 ms |
  | Después | 13,66 ms | 32,35 ms | 15,38 ms |

  Unos **1,5 ms más de mediana** (en localhost, con ruido: es una cifra orientativa, no un contrato). El plan de ejecución es un `Index Seek` sobre `IX_RefreshTokens_SessionId`, **2 lecturas lógicas**, sin `Key Lookup` ni `Scan`. **Sin caché**: una caché de unos segundos reintroduciría una ventana, justo lo que esta ADR quiere cerrar; se reconsidera solo si una medición en producción lo justifica.
- **Dependencia de la base de datos:** si no responde, la petición falla. No se acepta un token «por si acaso» cuando no se puede comprobar. (Ya era así con la comprobación de pertenencia.)
- **La tabla `RefreshTokens` crece** (se revoca, no se borra) y ahora se consulta en cada petición. El índice la mantiene barata, pero **la purga de filas caducadas o revocadas pasa de deseable a necesaria a medio plazo** (spec 016).
- **Petición en vuelo:** una petición que ya pasó la comprobación y se está procesando cuando se cierra la sesión se completa. La siguiente es la que falla.
- **Un token robado mientras nadie cierra la sesión sigue valiendo.** Esto cierra el robo *después* de cerrar sesión (el caso del móvil perdido), no detecta que lo use otra persona.
- **Las sesiones de operador de la plataforma no se tocan** (spec 011): no tienen refresh token (solo un access token corto de 15 minutos) y no hay una «sesión» que consultar. Se cierran por otra vía: desactivar al operador, que ya se comprueba en cada petición.
- **El `403` por pertenencia casi no se ve ya desde fuera:** quitar a un miembro por la API borra sus refresh tokens, así que su token se rechaza antes (`401`). La revalidación de ADR-013 sigue siendo la segunda barrera (sesión activa pero persona que ya no es miembro) y tiene su test.

### Qué queda cerrado (no se debe reabrir sin una razón nueva y explícita)

- La comprobación va en la **autenticación** y da **`401`**. Moverla a la autorización rompería el flujo de renovación del frontend.
- El `UserId` forma parte de la pregunta «¿está activa esta sesión?».
- Un token sin `sid` **se rechaza** (fail-closed).
- La columna `SessionId` **no tiene valor por defecto**.

### Qué queda abierto para revisar más adelante

- **Purga** de refresh tokens caducados y revocados, y listar y cerrar sesiones concretas (spec 016).
- Una tabla `Sessions` propia, si la spec 016 necesita datos de dispositivo.
- Una caché corta de la comprobación, si una medición real lo justifica.
- Un modelo de sesión para los operadores de la plataforma.

---

## Relación con otros ADR

- Depende de: ADR-007 (refresh token con rotación: la sesión es la cadena de renovaciones de un inicio de sesión).
- Cambia: ADR-007 (el access token deja de ser sin estado; ya no existe el «límite de 30 minutos» tras cerrar una sesión).
- Amplía: ADR-013 (la pertenencia se sigue revalidando en cada petición; ahora hay una consulta más por petición, hecha antes, en la autenticación).
- Amplía: ADR-009 (un evento de seguridad nuevo, `1009`; la lectura de `RefreshTokens` sigue dentro de la lista blanca).
- Resuelve: el límite conocido de las specs 010 (R14), 013 y 014, y el de ADR-014.
