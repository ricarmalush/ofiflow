# ADR-017 — CORS para el frontend web

**Estado:** Propuesto (2026-10-25) — pasa a Aceptado cuando se revise
**Fecha:** 2026-10-25
**Proyecto:** OfiFlow

---

## Contexto

El frontend de OfiFlow será un proyecto aparte (una aplicación web que llamará a esta API desde otro origen, por ejemplo `http://localhost:4200` en desarrollo). Un navegador aplica la regla del *mismo origen*: una página no puede leer las respuestas de una API de otro origen salvo que la propia API lo autorice con CORS. Hoy la API no lo autoriza (ADR-009 lo dejó como NEXT), así que un frontend web recibiría sus llamadas bloqueadas.

## Problema

Abrir CORS mal es una de las formas más habituales de convertir una API segura en una insegura, y tiene opciones cómodas con trampa:

- `AllowAnyOrigin` (`*`) hace que funcione todo a la primera, pero cualquier web podría leer las respuestas desde el navegador de un usuario.
- Permitir credenciales de navegador (cookies) junto a orígenes amplios es el caso clásico de robo de sesión entre sitios. OfiFlow no usa cookies: la sesión va en `Authorization`.
- Un origen mal escrito (con barra final, `http` en producción, un comodín) puede dejar la protección distinta de lo que se cree, y sin que nadie lo note.

## Decisión

CORS se configura en [`Program.cs`](../../src/OfiFlow.Api/Program.cs) con una política cerrada:

1. **Orígenes:** lista cerrada en la configuración, `Cors:AllowedOrigins`. **Sin lista, nadie queda autorizado.** El repositorio solo contiene el origen de desarrollo (`http://localhost:4200`, en `appsettings.Development.json`); el de producción se pasa por configuración de entorno.
2. **Validación al arrancar:** cada origen debe ser exactamente esquema, host y puerto (lo que envía el navegador en `Origin`), con `https` salvo en `localhost`/loopback, sin ruta, sin barra final, sin credenciales en la URL y sin comodines. Si no, la API no arranca (como una clave que falta, ADR-009 R8).
3. **Verbos y cabeceras cerrados:** `GET`, `POST`, `PUT`, `DELETE`; `Authorization`, `Content-Type`, `Accept-Language`.
4. **Sin credenciales de navegador** (`AllowCredentials`): la sesión viaja en la cabecera `Authorization`.
5. **Expone `Retry-After`** para que el frontend pueda leer cuánto esperar tras un `429`.
6. **Orden en el pipeline:** `UseCors` antes del rate limiter y de la autenticación, para que el *preflight* (`OPTIONS`, sin token) se conteste sin pasar por ellos y para que `429` y `500` también lleven las cabeceras.

## Alternativas descartadas

- **Un proxy inverso que sirva frontend y API desde el mismo origen:** evitaría CORS, pero exige infraestructura que no existe y no ayuda en desarrollo (dos servidores en puertos distintos).
- **Reflejar cualquier `Origin` recibido o `SetIsOriginAllowed(_ => true)`:** equivale a `*` con credenciales; descartado.
- **Una clase o extensión aparte para la configuración:** se prefirió dejarla visible en `Program.cs`, donde se ve el orden de los middlewares, que es parte de la decisión de seguridad.

## Consecuencias

- El frontend web puede llamar a la API desde los orígenes configurados y desde ningún otro.
- CORS **solo protege frente a navegadores**: un script, `curl` o un servidor lo ignoran. La API sigue defendiéndose con autenticación, autorización, rate limiting y el resto de ADR-009.
- Un origen de producción mal configurado falla cerrado (el frontend no puede leer) o, si se pone uno de más, abre esa web ajena: es un riesgo de **configuración**, no de código, y queda como límite documentado en el manual (capítulo 8).
- `Cross-Origin-Resource-Policy: same-origin` (ADR-010) se mantiene: solo afecta a cargas que no son CORS, no a las peticiones `fetch` del frontend.
- Si en el futuro el refresh token pasa a una cookie `HttpOnly` (decisión pendiente del frontend), habrá que **reabrir este ADR**: ahí sí haría falta `AllowCredentials` con orígenes exactos, y la protección frente a CSRF pasaría a ser necesaria.

## Cómo se comprueba

[`CorsTests`](../../tests/OfiFlow.Api.Tests/Security/CorsTests.cs): origen permitido y ajeno, *preflight* con verbo y cabecera no autorizados, ausencia de credenciales, `429` y `500` con cabeceras, ausencia de configuración, y orígenes inválidos que impiden el arranque. Se comprobó con 12 mutaciones manuales (comodín, credenciales, cualquier verbo o cabecera, quitar `UseCors`, moverlo después del rate limiter, `http` fuera de `localhost`, quitar cada validación de origen y alargar la caché del *preflight*); todas fueron detectadas.
