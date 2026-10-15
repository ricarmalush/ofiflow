# Capítulo 11 — Cómo añadir una funcionalidad

## Qué vas a aprender

- El método de trabajo del proyecto (*Spec-Driven Development*) y por qué se usa.
- Un ejemplo real de principio a fin: cómo se construyó la **gestión de usuarios** (spec 009) en siete bloques.
- Una receta paso a paso para añadir una operación nueva, con el código real de una operación pequeña (`RevocarInvitación`).
- Qué guardarraíles saltan si te olvidas de algo, y qué te están diciendo cuando lo hacen.
- La lista de comprobación final y el camino hasta `master`.

> Este capítulo junta todo lo anterior: asume que has leído al menos los capítulos 3 (capas), 4 (una petición), 7 (errores) y 9 (tests).

## El método: especificar antes de programar

En OfiFlow no se empieza escribiendo código. Cada funcionalidad nueva sigue cuatro pasos (*Specify → Plan → Tasks → Implement*) y deja **papeles** en el repositorio:

| Paso | Qué es | Dónde queda |
|---|---|---|
| **1. Specify** (especificar) | Qué problema resuelve, qué incluye y **qué no**, las reglas de negocio, los criterios de aceptación y las preguntas de seguridad | `docs/specs/NNN-nombre/spec.md` (plantilla en `TEMPLATE-spec.md`) |
| **2. Plan** | Se apoya en las decisiones ya tomadas (ADR). Si hace falta una decisión técnica nueva, se escribe una ADR | `docs/adr/ADR-0NN-...md` |
| **3. Tasks** (tareas) | Una lista de pasos pequeños, por bloques, cada uno verificable | `docs/specs/NNN-nombre/tasks.md` |
| **4. Implement** | El código, marcando cada tarea al terminarla | Ramas y *Pull Requests* |

**¿Por qué tanto papel para un proyecto de una persona?** Tres razones, y las tres son prácticas:

1. **Obligan a pensar antes.** Muchas decisiones difíciles (por ejemplo, "¿un `Admin` puede quitar a otro `Admin`?") se descubren al escribir la spec, cuando cambiarlas cuesta un párrafo, no una semana de código.
2. **Dejan constancia del porqué.** Dentro de un año nadie recordará por qué el token de invitación se devuelve al invitante. La ADR-013 lo explica, con la alternativa descartada.
3. **Se pueden defender.** Ante un tribunal o en una entrevista, "esta regla está en la spec, esta decisión en la ADR y esta prueba la vigila" es una respuesta completa.

### La spec no se da por buena sola

Un detalle que aprendimos en la spec 009: la spec **no se da por buena sola**. Quien la escribe propone, y quien decide (el dueño del proyecto) la revisa. Por eso la spec 009 trae una tabla de **decisiones a confirmar** (D1 a D7), cada una con una recomendación y la alternativa descartada, y no se escribió una línea de código hasta que se aprobaron.

## Un ejemplo real: la gestión de usuarios (spec 009)

Es la funcionalidad más grande hasta ahora y sirve de modelo. El objetivo: que el dueño de una empresa pueda **invitar a su equipo con un rol, cambiarlo y quitarlo**. Se hizo en **siete bloques**, cada uno un incremento pequeño, probado y commiteado antes de pasar al siguiente:

| Bloque | Qué se hizo | Capa |
|---|---|---|
| 0 | Dejar el manual al día antes de empezar | — |
| 1 | Los permisos nuevos, la tabla de "quién puede gestionar a quién" (`ManageableRoles`) y la invitación (`Invitation`) | Domain |
| 2 | Guardar las invitaciones (tabla, índices, migración) y borrar credenciales y sesiones | Infrastructure |
| 3 | Invitar, ver, revocar y aceptar | Application |
| 4 | Listar miembros, cambiar el rol y quitar | Application |
| 5 | Revalidar el rol y la pertenencia en **cada** petición | Application + Infrastructure |
| 6 | Las rutas HTTP y el límite de peticiones de aceptar | Api |
| 7 | Probar cada criterio de aceptación de extremo a extremo, y cerrar ADR y manual | Tests y documentación |

Fíjate en el **orden**: de dentro hacia fuera (Domain → Infrastructure → Application → Api → Tests). Es la dirección de las dependencias del capítulo 2: lo de dentro no conoce lo de fuera, así que se puede construir y **probar** sin tener lo de fuera. Cuando llegó el bloque 6, las reglas ya estaban probadas desde hacía cinco bloques y el endpoint era casi un trámite.

Tres hábitos que se repitieron en **cada** bloque y que conviene copiar:

- **El manual avanza a la vez que el código.** Cada bloque terminaba con su tarea `[manual]`: los capítulos afectados se actualizaban en ese mismo bloque. Un manual que va por detrás enseña cosas que ya no son verdad.
- **Cada guardarraíl se comprueba rompiéndolo.** Se modificaba el código real a propósito y se veía el test en rojo (capítulo 9). En la spec 009 hubo unas noventa de estas pruebas de mutación, y cuatro hallazgos reales salieron de ahí.
- **Nada se commitea sin que lo hayas leído.** Cada bloque termina con un resumen, y el siguiente empieza cuando dices "hazle el commit".

## La receta: añadir una operación

Vamos a seguir una operación pequeña y real, **revocar una invitación**, para ver qué se toca y en qué orden. Una "operación" es un *Command* (cambia algo) o una *Query* (solo lee), como en el capítulo 3.

### Paso 1 — Decide si necesita una regla en Domain

¿Hay una regla de negocio que dependa solo de la propia entidad? Entonces va **dentro** de la entidad, no en el handler. Revocar solo se puede si la invitación sigue pendiente, y eso lo sabe la propia invitación ([`Invitation.Revoke`](../../src/OfiFlow.Domain/Tenancy/Invitation.cs)):

```csharp
public void Revoke(DateTime utcNow)
{
    EnsurePending(utcNow);
    RevokedAt = utcNow;
}
```

Si no está pendiente, lanza una `DomainException` con un **código** (`invitation.invalid`), no un texto (capítulo 7). Fíjate en que recibe la hora por parámetro: así la regla de caducidad se prueba sin esperar siete días.

### Paso 2 — Declara el comando, con su permiso

En Application, una carpeta por operación, con el comando y su handler ([`RevokeInvitationCommand`](../../src/OfiFlow.Application/Tenancy/Commands/RevokeInvitation/RevokeInvitationCommand.cs)):

```csharp
[RequiresPermission(Permission.UsersManage)]
public sealed record RevokeInvitationCommand(Guid Id) : IRequest;
```

La línea de arriba es la **autorización**: ¿qué permiso exige? Si no pones nada, el sistema **deniega la operación** (*fail-closed*, capítulo 8) y un test de arquitectura falla. Fíjate también en lo que el comando **no tiene**: ningún `TenantId`. La empresa sale del token, nunca de lo que mande el cliente (capítulo 5).

### Paso 3 — Escribe el handler

El [handler](../../src/OfiFlow.Application/Tenancy/Commands/RevokeInvitation/RevokeInvitationCommandHandler.cs) hace, en este orden:

1. **Busca** la invitación. El filtro de empresa (capítulo 5) hace que una invitación de otra empresa dé el mismo "no encontrada" que una inexistente: `NotFoundException`, con código.
2. **Comprueba la jerarquía**: un `Admin` no puede revocar la invitación de otro `Admin`. Es una regla que mira a varias cosas a la vez (quién actúa y qué rol tiene la invitación), así que va en Application.
3. **Llama a la regla de Domain** (`invitation.Revoke(...)`), sin capturar su excepción: así nunca se confunde con un bug.
4. **Guarda** y **registra el evento de seguridad** `1404`, con Ids y sin datos personales (capítulo 8).

### Paso 4 — Añade el endpoint

Es lo más corto. Recibir, enviar a MediatR, devolver ([`InvitationEndpoints`](../../src/OfiFlow.Api/Endpoints/InvitationEndpoints.cs)):

```csharp
group.MapDelete("/{id:guid}", async (Guid id, ISender sender, CancellationToken cancellationToken) =>
    {
        await sender.Send(new RevokeInvitationCommand(id), cancellationToken);
        return Results.NoContent();
    })
    .WithName("RevokeInvitation")
    .WithSummary("Revoca una invitación pendiente.");
```

No hay ninguna comprobación de permisos aquí. El grupo entero exige sesión (`RequireAuthorization()`) y **el permiso lo comprueba el `AuthorizationBehavior`**, para que valga igual venga la petición de HTTP o de otro sitio.

### Paso 5 — Códigos de error y mensajes

Si la operación puede fallar de una forma nueva, hace falta un **código** estable (`invitation.not_found`) en la clase `*Errors` de Domain y su **mensaje** en `Resources/ErrorMessages.resx`. Los dos tests del diccionario (capítulo 7) fallan si falta uno de los dos.

### Paso 6 — Tests, proporcionales al riesgo

Según el nivel de riesgo (capítulo 9): invitar y revocar son de **nivel alto** (autorización, datos personales), así que llevan la batería completa. Para esta operación, cada capa tiene su test:

| Capa | Qué comprueba | Test |
|---|---|---|
| Domain | Que solo se revoca lo pendiente | `Revoke_FailsIfAlreadyAccepted_RevokedOrExpired` |
| Application | La jerarquía y que el dato no cambia al denegar | `AnAdmin_CannotRevokeAnAdminInvitation_AndItStaysPending` |
| Api (HTTP) | Los códigos de estado y que no se puede revocar dos veces | `RevokingAnUnknownInvitation_Is404_AndRevokingTwiceIs400` |
| Api (extremo a extremo) | Que una revocada ya no sirve para aceptar | `ARevokedInvitation_DisappearsFromTheList_AndItsTokenNoLongerWorks` |

Y las **pruebas de mutación**: se rompe a propósito la jerarquía del handler y se comprueba que algún test se pone en rojo.

### Paso 7 — Registra la operación en la tabla de permisos

[`OperationPermissionsTests`](../../tests/OfiFlow.Application.Tests/Authorization/OperationPermissionsTests.cs) lleva una tabla "operación → permiso" y **falla si existe una operación que no está en ella**. Es una molestia buscada: te obliga a decir explícitamente qué permiso le corresponde a cada operación nueva.

### Paso 8 — Ajusta lo que cambie alrededor

Según lo que hayas tocado:

- **Una entidad nueva de una empresa** → implementa `ITenantOwned`, y añádela a la lista de entidades esperadas de los tests (capítulo 5).
- **Un cambio en el modelo de datos** → genera una **migración** (`dotnet ef migrations add`) y **revisa el SQL** que produce, no solo el nombre.
- **Un permiso nuevo** → decide qué roles lo reciben en [`RolePermissions`](../../src/OfiFlow.Domain/Tenancy/RolePermissions.cs): hasta entonces, nadie lo tiene, ni el `Owner`.
- **Una consulta que se salta el filtro de empresa** (`IgnoreQueryFilters()`) → solo con un motivo escrito, y añadiendo el fichero a la lista blanca.

### Paso 9 — El manual y la colección de pruebas

Actualiza los capítulos que describan lo que cambió (¡y las referencias a líneas, que se desplazan solas!). Añade las peticiones a la colección [`OfiFlow.Api.http`](../../src/OfiFlow.Api/OfiFlow.Api.http) y compruébalas contra una API real.

## Qué te dice cada guardarraíl cuando saltan

Te vas a olvidar de cosas. Para eso están los guardarraíles: un test en rojo **con un mensaje que dice qué hacer**. Los que más vas a ver:

| Si te olvidas de... | Falla | Qué hacer |
|---|---|---|
| Declarar el permiso de una operación | `EveryOperationOfTheApplication_DeclaresItsAuthorization` | Añade `[RequiresPermission(...)]` (o `[AllowAnonymousRequest]` si de verdad es pública) |
| Poner la operación en la tabla de permisos | `TheTableCoversEveryOperationOfTheApplication` | Añádela con su permiso |
| El mensaje de un código de error | `EveryErrorCode_HasAMessageInTheDictionary` | Añade la entrada a `ErrorMessages.resx` |
| Que un código siga el formato `entidad.error` | `EveryErrorCode_FollowsTheNamingConvention` | Renómbralo en minúsculas y `snake_case` |
| Lanzar una excepción genérica en Domain | `Domain_DoesNotThrowGenericExceptionsForBusinessRules` | Usa `DomainException` con un código |
| Marcar una entidad de empresa con `ITenantOwned` | `EveryMappedEntity_IsTenantOwnedOrDeclaredGlobal` | Implementa la interfaz (o decláralo global con su motivo) |
| Añadirla a la lista de entidades esperadas | `TheTenantOwnedEntitiesAreTheExpectedOnes` | Añádela a propósito: el centinela existe para que lo decidas tú |
| Generar la migración | `TheMigrations_MatchTheModel` | `dotnet ef migrations add <Nombre>` |
| Un `TenantId` que llega del cliente | `NoCommandOrQuery_AcceptsTenantIdFromTheClient` | Quítalo del comando: la empresa sale del token |
| SQL escrito a mano | `SourceCode_DoesNotUseRawSqlApis` | Usa LINQ, o SQL parametrizado en una *Query* con tests |
| Usar `IgnoreQueryFilters()` donde no toca | `IgnoreQueryFilters_IsOnlyUsedInTheAllowList` | Justifícalo y añade el fichero a la lista blanca |
| Leer una tabla global (`Users`, `Tenants`...) | `GlobalEntities_AreOnlyReadInTheAllowList` | Léela solo desde un fichero de la lista blanca |

La idea de fondo: **un guardarraíl no te prohíbe hacerlo, te obliga a hacerlo a propósito.** Todo lo que está en una lista blanca está ahí con su motivo escrito.

## Errores típicos de quien empieza

Todos los hemos cometido en este proyecto, y todos están en el manual en algún sitio:

- **Fiarte del rol del token.** Desde la spec 009 el rol sale de la base de datos y `currentUser.Role` es nulo hasta que se verifica. No leas el claim del token para decidir nada.
- **Usar `DateTime.UtcNow` en una regla.** Inyecta `TimeProvider`: así puedes probar la caducidad sin esperar.
- **Dejar un dato personal en un log.** Los eventos de seguridad llevan Ids, nunca emails, nombres, contraseñas ni tokens. Y se comprueba con tests que buscan esos valores en el registro.
- **Restaurar un cambio con `git checkout` cuando aún no está commiteado.** Pierdes el trabajo. Usa copias de seguridad, o commitea antes (capítulo 9).
- **Editar números de línea a mano** en el manual tras insertar texto. Se desplazan; se comprueban contra el código.
- **Dar por bueno un test que no ha fallado nunca.** Rompe el código y míralo en rojo.
- **Repetir un test intermitente hasta que pase.** Casi nunca es mala suerte: investígalo. Uno de ellos destapó un defecto real.

## La lista de comprobación final

Antes de dar una funcionalidad por terminada (lo que el proyecto llama la *Definición de "Done"*):

- [ ] Código, reglas de negocio y validaciones implementados.
- [ ] **Autorización:** cada operación declara su permiso, y hay un test de quién puede y quién no.
- [ ] **Multi-tenancy comprobado:** una empresa nunca ve ni toca los datos de otra, con un test contra SQL real.
- [ ] Tests al nivel de riesgo, y los guardarraíles comprobados rompiéndolos.
- [ ] **Sin datos personales en los logs**, comprobado con un test.
- [ ] Probado a mano (Swagger o la colección `.http`).
- [ ] ADR cerrada (si hubo decisión técnica) y `tasks.md` al día.
- [ ] **Manual actualizado**, con las cifras y las líneas verificadas.

## Camino hasta `master`

`master` está protegida: **no se puede subir nada directamente**, ni siquiera el dueño del repositorio (capítulo 10). El camino es siempre:

1. Una **rama** (`git switch -c feature/lo-que-sea`) desde `master` actualizada.
2. Commits pequeños, uno por bloque, con un mensaje que explica el porqué.
3. `git push` y un **Pull Request** con una descripción que dice qué cambia, **qué conviene revisar con atención** y cómo se verificó.
4. Los **cuatro comprobantes** en verde (`build-and-test`, `gitleaks`, `analyze`, `zap-scan`) y la rama al día con `master`.
5. **Merge commit**, no *squash*, para que la historia conserve cada paso.

Subir la rama, abrir el PR y fusionar son acciones que **se confirman en el momento**: una aprobación para una no vale para la siguiente.

## Para comprobar que lo has entendido

1. ¿Qué cuatro pasos tiene el método, y qué papel deja cada uno en el repositorio?
2. ¿Por qué se construye de dentro hacia fuera (Domain → Infrastructure → Application → Api)?
3. Quieres añadir "reabrir un trabajo cancelado". ¿Dónde va la regla "solo se reabre lo cancelado" y dónde la comprobación de quién puede hacerlo?
4. Añades un código de error `job.cannot_reopen` y olvidas su mensaje. ¿Qué pasa y qué test lo detecta?
5. Creas una entidad nueva con su propio `TenantId` pero sin `ITenantOwned`. ¿Qué la protegía antes de los guardarraíles y qué la protege ahora?
6. ¿Por qué el endpoint no comprueba el permiso?
7. ¿Para qué sirve la tabla de decisiones a confirmar de una spec, y por qué no se programa hasta que se aprueba?
8. ¿Por qué se actualiza el manual en el mismo bloque que el código, y no al final?

*(Respuestas: 1 = *Specify* (spec.md: qué, reglas y criterios), *Plan* (apoyarse en las ADR o escribir una nueva), *Tasks* (tasks.md: bloques pequeños) e *Implement* (código en ramas y PRs). 2 = porque las dependencias del capítulo 2 apuntan hacia dentro: lo de dentro no conoce lo de fuera y se puede probar sin ello; cuando llegas a la API las reglas ya están probadas. 3 = la regla de la propia entidad en Domain (`Job.Reopen`, que lanza una `DomainException` con código si no está cancelado); la comprobación de quién puede, con `[RequiresPermission(...)]` en el comando y la tabla de permisos, no en el endpoint. 4 = el usuario vería el código en bruto; lo detecta `EveryErrorCode_HasAMessageInTheDictionary`. 5 = antes, nada: no tenía filtro de empresa y todo funcionaba hasta que alguien veía datos ajenos; ahora fallan los tests de clasificación del modelo (`EveryMappedEntity_IsTenantOwnedOrDeclaredGlobal`). 6 = porque el `AuthorizationBehavior` lo comprueba en Application, para que valga igual venga la petición de HTTP o de cualquier otro sitio, y no se pueda olvidar en un endpoint. 7 = recoge las decisiones que más condicionan (con recomendación y alternativa descartada) y cambiarlas cuesta un párrafo antes de programar y una semana después. 8 = porque un manual que va por detrás enseña cosas que ya no son verdad, y porque al actualizarlo se descubren errores (cifras, líneas, afirmaciones que dejaron de cumplirse).)*

## Siguiente capítulo

El capítulo 12 es un **glosario** de todos los términos del manual y un índice de las **decisiones** (ADR) y especificaciones del proyecto.
