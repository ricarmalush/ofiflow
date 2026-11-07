# Spec — 022-mfa-obligatorio

**Estado:** Aprobada (2026-11-07). Decisiones D1–D11 aprobadas tal como estaban escritas (D1, D3 y D4 confirmadas expresamente: la restricción es por petición en la API, Owner y Admin, efecto inmediato sin periodo de gracia)
**Fecha:** 2026-11-07
**Bounded Context:** Identity (el segundo factor) y Tenancy (los roles)
**Depende de:** spec 010 y ADR-014 (el segundo factor TOTP, que hoy es opcional; el límite 3b es esta spec), spec 009 y ADR-013 (el rol efectivo se revalida en cada petición: el mismo patrón), ADR-012 (la autorización por permisos y su `AuthorizationBehavior`), spec 011 y ADR-016 (el operador de la plataforma ya tiene el segundo factor obligatorio: esto lo extiende a las empresas), spec 021 y ADR-023 (crear una cuenta: la cuenta que nace de la web **nace sin segundo factor**)

> **Numeración:** 022 provisional. La 019 sigue reservada a «cambiar la contraseña estando dentro» (ADR-021).

---

## Objetivo

Que **un `Owner` o un `Admin` no pueda trabajar con solo una contraseña**: mientras no tenga el segundo factor activado, su sesión sirve **únicamente para activarlo**; todo lo demás se deniega.

Hoy el segundo factor existe (spec 010) pero es **opcional**, y con el registro abierto (spec 021) la cuenta que nace desde la web **no lo tiene**. Es justo la cuenta más valiosa de cada empresa (el `Owner` crea usuarios, gestiona roles y ve todos los datos) protegida solo por una contraseña, y es el remedio de fondo que ya habían señalado la ADR-014 (límite 3b), la ADR-021 («quien controla el buzón de una cuenta sin segundo factor controla la cuenta») y la ADR-023.

## Alcance — Qué SÍ incluye

Cuatro bloques:

1. **La puerta en la API:** una comprobación en `AuthorizationBehavior`, por petición y sin cambiar el token: si el **rol efectivo** exige segundo factor y la cuenta no lo tiene activo, solo pasan las operaciones marcadas expresamente como permitidas (`GET /account/me` y las de activarlo); el resto da `403 mfa.enrollment_required`. Con su evento de seguridad y la marca explícita de las operaciones permitidas, vigilada por un test de arquitectura.
2. **Que no se pueda deshacer:** un `Owner` o `Admin` **no puede desactivar** su segundo factor, y `GET /account/me` dice si la cuenta **debe** activarlo, para que la interfaz no tenga que provocar un `403` para enterarse. Se prueba además lo que ocurre al **promover** (alguien pasa a `Admin`: su siguiente petición ya lo exige) y al **reiniciar** el factor de un `Admin`.
3. **Frontend:** quien debe activarlo y no lo tiene llega **directamente al asistente de activación** (la pantalla de seguridad que ya existe), con un aviso que dice por qué, sin poder ir a ningún otro sitio; y quien tiene un rol que lo exige ya no ve «Desactivar».
4. **Documentación:** ADR-024, anotaciones en ADR-014 (cierra el límite 3b), ADR-012 y ADR-023, y el manual.

## Alcance — Qué NO incluye

- **NEXT:** **passkeys / WebAuthn** (resistentes al *phishing*; el TOTP no lo es).
- **NEXT:** que el segundo factor sea obligatorio también para `Manager`, `Technician` y `Employee`. Se decide cuando haya una razón (datos que vean o acciones que puedan hacer). Hoy siguen siendo opcionales.
- **NEXT:** que la pantalla de **Equipo** enseñe qué miembros todavía no lo han activado (hoy no hay forma de saberlo desde fuera; es un dato de otra persona y pide un permiso).
- **NEXT:** que un `Owner` que pierde el móvil **y** los códigos de recuperación se recupere sin tocar la base de datos (límite 3d de ADR-014: lo resolverá el operador de la plataforma). Esta spec **no lo empeora** para quien ya tiene el factor activo, pero sí hace que un `Owner` **sin** factor y con la cuenta robada no pueda usarla de otra forma que activándolo (ver «Riesgos aceptados»).
- **LATER:** un periodo de gracia por cuenta, «recordar este dispositivo», avisos por correo al promover a alguien.
- **NEVER:** una excepción por rol «porque es el dueño» (el factor se exige **más** a quien más puede), SMS como segundo factor (spec 010), y quitar el factor a quien lo exige.

## Reglas de negocio / invariantes

**La puerta**

- **R1. Quién lo exige.** El rol **efectivo** (el de la base de datos en esta petición, no el del token) es `Owner` o `Admin`. La lista vive **en un solo sitio** (una política con su test) para que ampliarla a otro rol sea cambiar una línea.
- **R2. Qué se deniega.** Con un rol que lo exige y **sin** segundo factor **activo** (sin secreto, o con uno solo pendiente de confirmar), toda operación con permiso de empresa responde `403 mfa.enrollment_required`, **salvo** las marcadas como permitidas. La comprobación va **después** de la del permiso: quien no tiene el permiso recibe `auth.forbidden` como siempre, tenga o no el factor (ADR-012 R3: no se informa sobre lo que no se puede hacer).
- **R3. Qué se permite, y es una lista cerrada.** `GET /account/me` (para saber qué pasa) y las tres operaciones para activarlo: pedir el secreto, confirmarlo y ver el estado. **Nada más.** Cerrar sesión y renovarla son anónimas y no pasan por la puerta. La marca es un atributo **explícito** en cada operación permitida (*fail-closed*: una operación nueva se deniega hasta que alguien decida lo contrario) y un test de arquitectura fija la lista exacta.
- **R4. Efecto inmediato.** Se comprueba **en cada petición**, como el rol: promover a alguien a `Admin`, o reiniciarle el factor, surte efecto en su **siguiente** petición, sin esperar a que caduque nada ni cambiar el token. **No hay un token «limitado»** ni un tipo de sesión nuevo.
- **R5. Ni una consulta más.** El estado del factor viaja **en la misma consulta** que la pertenencia y el rol (una subconsulta sobre la clave de la tabla del factor), como ya viaja la suspensión de la empresa. Un test cuenta las consultas por petición y no pueden aumentar.
- **R6. Lo que no cambia.** El operador de la plataforma ya lo tiene obligatorio y su mundo es otro (no pasa por esta puerta). Las operaciones anónimas tampoco. Quien tiene un rol que **no** lo exige sigue como hoy.

**Que no se pueda deshacer**

- **R7. No se puede desactivar con un rol que lo exige.** `POST /account/mfa/disable` responde `400 mfa.required_for_role` a un `Owner` o `Admin`. Sin esto, activarlo y desactivarlo en el acto anularía toda la spec. Si la persona deja de tener un rol que lo exige (la degradan), puede desactivarlo como cualquiera.
- **R8. Reiniciar el factor de un `Admin` sigue permitido** (la jerarquía de la spec 009 manda quién puede): perdió el móvil. Al reiniciarlo, la cuenta queda **sin factor** y, **en su siguiente inicio de sesión, restringida** hasta que lo vuelva a activar (R2). Ya cierra sus sesiones (spec 010).
- **R9. Activarlo cierra las sesiones de la cuenta** (spec 010 R9, ya existente): quien lo activa vuelve a entrar **con el segundo paso**. Esta spec no lo cambia.

**La respuesta**

- **R10. Un código nuevo y estable: `mfa.enrollment_required`** (`403`, formato de error de siempre). **No** es `auth.forbidden`: la persona **está autenticada**, sabe quién es y puede saber por qué se le deniega, y el frontend decide por el código (nunca por el texto) qué hacer. El texto dice qué hacer («Tu rol exige el segundo factor. Actívalo para continuar»). No distingue nada que un tercero no conozca ya (es la propia sesión de la persona).
- **R11. `GET /account/me` lo dice:** un campo `secondFactorEnrollmentRequired` (`true` si el rol lo exige y el factor no está activo). Los permisos que devuelve siguen siendo los del rol (informativos, como siempre); la interfaz se guía por este campo.

**Los eventos de seguridad** (familia `13xx`, el número se fija al implementar)

- **R12.** Una denegación por falta de segundo factor deja un evento con la operación, el `UserId`, el `TenantId` y el rol; y la desactivación denegada, otro. **Nunca** secretos, códigos ni emails.

## Modelo conceptual

```
SecondFactorPolicy (Domain)   RequiredFor(TenantRole) → bool        Owner y Admin: sí; el resto: no
[AllowedWithoutSecondFactor]  atributo en las operaciones de la lista cerrada de R3
ICurrentUser                  + SecondFactorEnrollmentRequired      (se rellena en VerifyMembershipAsync, en la misma consulta)
AuthorizationBehavior         tras verificar la pertenencia y el rol:
                                si Policy.RequiredFor(rol) y no hay factor activo y la operación no está permitida → 403 mfa.enrollment_required
CurrentAccountDto             + SecondFactorEnrollmentRequired
DisableMfaCommandHandler      + rechaza con mfa.required_for_role si el rol lo exige

Sin cambios de esquema ni migración: el estado del factor ya está en la base de datos (MfaCredentials.ConfirmedAt).
```

## Criterios de aceptación

- Dado un `Owner` o un `Admin` **sin** segundo factor, cuando pide cualquier operación de clientes, trabajos, equipo, sesiones o agenda, entonces recibe `403 mfa.enrollment_required` (y nada de lo que pedía llega a su handler).
- Dado ese mismo `Owner`, cuando pide `GET /account/me`, pedir el secreto o confirmarlo, entonces **funciona**, y `me` dice `secondFactorEnrollmentRequired: true`.
- Dado un `Owner` que **activa** el segundo factor, entonces sus sesiones se cierran, vuelve a entrar con el código y **todo** funciona; `me` dice `false`.
- Dado un `Owner` con un secreto **pedido pero sin confirmar**, entonces sigue restringido (un secreto pendiente no cuenta).
- Dado un `Manager`, `Technician` o `Employee` sin factor, entonces **todo sigue como hoy**.
- Dado un `Technician` **promovido** a `Admin` sin factor, entonces en su **siguiente** petición (con el mismo token) recibe `mfa.enrollment_required`.
- Dado un `Admin` **degradado** a `Manager`, entonces recupera el acceso completo en su siguiente petición.
- Dado un `Admin` con el factor activo, cuando intenta **desactivarlo**, entonces `400 mfa.required_for_role` y el factor sigue activo; y un `Manager` con factor sí puede desactivarlo.
- Dado un `Admin` al que un gestor **reinicia** el factor, entonces en su siguiente inicio de sesión (sin segundo paso) está restringido hasta activarlo.
- Dado un operador de la plataforma, entonces no le afecta nada de esto.
- Dada cualquier operación nueva con permiso de empresa, cuando no está marcada como permitida, entonces se deniega a un `Owner` sin factor (test de arquitectura sobre la lista exacta).
- Dada una petición de un `Owner` o `Admin` **con** factor activo, entonces el número de consultas a la base de datos **no aumenta** respecto a hoy.
- Dados los registros de todo el flujo, cuando se revisan, entonces no aparece ningún secreto, código ni email.

Caso multi-tenant obligatorio: la comprobación solo mira **la cuenta de quien pide** (su rol en **su** empresa y **su** factor); nunca el de otra persona ni el de otra empresa, y un `Admin` de una empresa sin factor no afecta a nadie de otra.

## Seguridad (ADR-009)

- **Datos personales:** ninguno nuevo. El estado del factor es un booleano derivado de una tabla que ya existe; los eventos llevan Ids, rol y operación.
- **Autorización:** no hay permisos nuevos. Es una **segunda condición** sobre el permiso de siempre, en el mismo sitio donde ya se revalida el rol, y *fail-closed* (lo no marcado se deniega).
- **Entrada:** sin campos nuevos de entrada; un campo nuevo de salida en `me`.
- **Aislamiento:** ninguna entidad nueva ni lectura nueva fuera de las existentes; la subconsulta del factor sale por la clave de su tabla (global, por cuenta) y solo para la cuenta del token.
- **SQL crudo:** ninguno.
- **Abuso:** no abre ninguna ruta nueva. La denegación no cuesta más que cualquier otra; el evento puede repetirse (cada petición de una sesión restringida) y se trata como información, no como alarma, salvo una ráfaga desde una IP.
- **Enumeración:** nada que enumerar: quien recibe el `403` es la propia persona.
- **Modelo de amenazas (lo importante):**
  - **Mejora:** una contraseña robada (*phishing*, filtrada en otro sitio, adivinada) de un `Owner` o `Admin` **ya no basta para actuar como él una vez activado el factor**, que es lo que el bloqueo de cuenta y el límite por IP no podían garantizar.
  - **No mejora, y hay que decirlo:** **una cuenta que todavía no ha activado el factor** tiene una **ventana** en la que quien conozca su contraseña puede entrar con la sesión restringida y **activar el factor él mismo**, quedándose con la cuenta y dejando fuera a su dueño. Es la confianza en el primer uso (*trust on first use*) de cualquier enrolamiento. Mitigaciones: activarlo **vuelve a pedir la contraseña** (spec 010), **cierra todas las sesiones**, y para la cuenta que **nace de la web la ventana es mínima** (la persona acaba de elegir su contraseña y la primera pantalla que ve es la de activar el factor). Para cuentas ya existentes, la ventana es el tiempo hasta su primer inicio de sesión tras el despliegue. Riesgo aceptado, a documentar en la ADR.
  - **Un `Owner` con el factor activo que pierde el móvil y los códigos** sigue necesitando la herramienta de plataforma (límite 3d de ADR-014, sin cambios).

## Decisiones técnicas relevantes

- **D1. La restricción es por petición, en la API, y no en el token.** *Recomendado, y la decisión que más condiciona la spec.* La alternativa (emitir una **sesión limitada** al iniciar sesión: un token con un claim «solo para activar el factor») añade un tipo de token, un esquema de validación y un estado que viaja con el token y **no cubre la promoción**: quien pasa a `Admin` con una sesión ya abierta seguiría con acceso completo hasta renovarla. Con la comprobación por petición es el **mismo patrón** que ya revalida el rol (ADR-013): efecto inmediato, sin token nuevo, y *fail-closed*.
- **D2. Una lista cerrada de operaciones permitidas, marcadas con un atributo.** *Recomendado.* La alternativa (una lista de operaciones **denegadas**) olvidaría la próxima que se añada y la dejaría abierta. Con la lista de las permitidas, lo nuevo se deniega solo.
- **D3. Owner y Admin.** *Recomendado, y la decisión de alcance a revisar.* Es lo que dice ADR-014. Alternativas: **solo `Owner`** (más corto, pero un `Admin` también gestiona usuarios y roles) o **todos los roles** (obligaría a cada técnico a instalar una app para consultar su agenda; coste de adopción alto sin un riesgo equivalente). La lista está en un solo sitio para ampliarla.
- **D4. Sin periodo de gracia: efecto inmediato tras el despliegue.** *Recomendado.* Hoy no hay clientes reales, solo cuentas de prueba; un periodo de gracia añadiría una fecha límite por cuenta, una pantalla de aviso y casos de borde, para proteger a nadie. Cuando haya clientes, una política de gracia sería otra spec.
- **D5. No se puede desactivar con un rol que lo exige.** *Recomendado.* Es la única forma de que «obligatorio» sea cierto. Se deja a quien baja de rol poder quitarlo.
- **D6. Un código propio, `mfa.enrollment_required`, no `auth.forbidden`.** *Recomendado.* El frontend decide por el código y no tiene que adivinar; no revela nada ajeno.
- **D7. `me` lleva el campo `secondFactorEnrollmentRequired`.** *Recomendado.* Evita provocar un `403` para enterarse y permite pintar la interfaz bien desde el primer momento.
- **D8. El estado del factor viaja en la consulta de la pertenencia** (subconsulta), sin una ida más a la base de datos. *Recomendado.* Se mide con un test que cuenta las consultas, como en la spec 015.
- **D9. Frontend: un guardián lleva a la pantalla de seguridad que ya existe**, no se escribe un asistente nuevo. *Recomendado.* La pantalla ya sabe pedir el secreto, enseñar el código QR, confirmar y entregar los códigos de recuperación; solo hay que llevar allí a quien debe y esconder «Desactivar» a quien no puede.
- **D10. Eventos nuevos en la familia `13xx`**, uno por denegación y otro por desactivación denegada; los números se fijan al implementar. *Recomendado.*
- **D11. Orden de trabajo:** la puerta (**bloque 1**), que no se pueda deshacer y `me` (**2**), frontend (**3**) y documentación (**4**). Los bloques 1 y 2 se entregan juntos al backend; **entre el 1 y el 3 la API ya deniega y el frontend actual no sabe qué hacer** (un `Owner` sin factor vería errores `403` en todas las pantallas): por eso el frontend va **en el mismo PR-par** y no se fusiona el backend solo.
- **¿Requiere ADR nueva? Sí: ADR-024**, «Segundo factor obligatorio para `Owner` y `Admin`», con la comparación D1 (por petición frente a sesión limitada), la ventana de enrolamiento como riesgo aceptado y qué queda abierto. Con anotaciones «Ampliada por» en ADR-014 (cierra el límite 3b), ADR-012 y ADR-023.

## Fuera de alcance / Backlog relacionado

- Passkeys / WebAuthn.
- Obligatoriedad para más roles.
- Mostrar en Equipo quién no lo ha activado.
- Recuperar un `Owner` que lo pierde todo, desde la herramienta de plataforma (límite 3d).
- Un periodo de gracia, cuando haya clientes reales.
- Cambiar la contraseña estando dentro (spec 019).

---

## Validación antes de aprobar

> ¿Esta funcionalidad ayuda a conseguir o mantener al primer cliente?

Sí, y es un **requisito de abrir el registro**: el primer cliente real crea su cuenta con la spec 021 y esa cuenta, la del `Owner`, nace sin segundo factor. Sin esta spec, lo único que separa los datos de sus clientes de quien robe una contraseña es el bloqueo de cuenta y el límite por IP. Con ella, la cuenta más valiosa de cada empresa queda protegida desde su **primera** sesión. Y el coste de adopción es bajo precisamente porque la pantalla que lo guía ya existe.
