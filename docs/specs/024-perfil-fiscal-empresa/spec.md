`# Spec — 024-perfil-fiscal-empresa
`
`**Estado:** Borrador (2026-11-09), pendiente de aprobación. Las tres decisiones de forma (alcance, datos y permisos) están **confirmadas con la persona dueña del proyecto** (D1, D2 y D3); el resto (D4–D12) son recomendaciones por revisar
`**Fecha:** 2026-11-09
`**Bounded Context:** Tenancy
`**Depende de:** ADR-002 (aislamiento por empresa), ADR-012 y spec 008 (permisos por rol), spec 023 y ADR-026 (los presupuestos, que son los primeros documentos que necesitan estos datos), ADR-009 (datos personales y registro), ADR-011 (códigos de error), ADR-024 (la puerta del segundo factor: se aplica sola)
`
`> **Numeración:** 024 es la siguiente spec; su ADR será la **027** (la 026 ya existe). La ADR-026 anunciaba el PDF como spec 024 y el correo como 025: al descubrir que **antes del PDF hacen falta los datos de la empresa**, esta spec es la que cierra ese prerrequisito y el **PDF pasa a ser la 025** y el correo la 026.
`
`---
`
`## Objetivo
`
`Que **una empresa pueda guardar sus datos fiscales y de contacto** (nombre fiscal, NIF o CIF, dirección, teléfono y email) para que los documentos que genere OfiFlow salgan **a su nombre**.
`
`Hoy la empresa solo tiene un nombre (el comercial, el que se eligió al registrarse). Un presupuesto o una factura necesitan **quién lo emite**: nombre fiscal, NIF y dirección. Sin ellos no se puede generar un PDF que un cliente se tome en serio, y una factura (fase 4) **no es válida** sin esos datos. Esta spec **no genera ningún documento**: crea el modelo de datos, su validación (un NIF con su dígito de control) y la pantalla para mantenerlo.
`
`## Alcance — Qué SÍ incluye
`
`Cuatro bloques:
`
`1. **El dominio y la persistencia (backend):** `CompanyProfile` (uno por empresa), el NIF/CIF/NIE **validado** (con su letra o dígito de control), el código postal y la migración.
`2. **La API:** leer y modificar el perfil, dos permisos nuevos (`Company.Read` y `Company.Manage`), un evento de seguridad, y los tests de extremo a extremo y las mutaciones de lo que decide.
`3. **Frontend:** una pantalla «Datos de la empresa» (solo lectura para quien solo puede ver, editable para quien puede gestionar), con su entrada de menú.
`4. **Documentación:** ADR-027, el manual y la spec cerrada.
`
`## Alcance — Qué NO incluye
`
`- **NEXT (spec 025):** el **PDF** del presupuesto, que usará estos datos. Y un aviso en la pantalla de presupuestos cuando falten («completa los datos de tu empresa para poder generar el PDF»).
`- **NEXT (spec 026):** mandar el presupuesto por correo.
`- **LATER:** el **logotipo** (D2): exige guardar imágenes (almacenamiento, tamaño y seguridad del fichero) y es una spec propia. Otros países (un NIF-IVA europeo, otros formatos de dirección), varias direcciones o sedes, una cuenta bancaria o IBAN para las facturas, el régimen fiscal y la serie de numeración de las facturas.
`- **NEVER:** que **otra empresa** (ni un operador de la plataforma) lea o modifique estos datos desde esta API. Y repetir el NIF entre empresas como clave de nada (D6).
`- **No cambia:** `Tenant` (su nombre comercial sigue siendo el de siempre; el nombre fiscal es otro dato), el registro de una cuenta (la empresa nace **sin** perfil) ni el panel de la plataforma.
`
`## Reglas de negocio / invariantes
`
`**El perfil**
`
`- **R1. Una empresa tiene, como mucho, un perfil.** Nace **al guardarlo por primera vez** (no al registrarse): hasta entonces la empresa «no tiene datos fiscales» y la lectura devuelve un perfil vacío con `isComplete = false`, no un error.
`- **R2. Campos y límites.** Obligatorios: `LegalName` (nombre o razón social, hasta 200), `TaxId` (R3), `AddressLine` (calle y número, hasta 200), `PostalCode` (R4) y `City` (hasta 100). Opcionales: `Province` (hasta 100), `Phone` (la misma validación que el teléfono de un cliente) y `Email` (la misma que el email de un cliente). Todo texto se guarda **sin espacios sobrantes**; un opcional vacío es `null`.
`- **R3. El NIF, CIF o NIE es válido de verdad.** Se **normaliza** (mayúsculas, sin espacios, guiones ni puntos) y se comprueba su **dígito o letra de control** con el algoritmo oficial: NIF (8 cifras y letra), NIE (X, Y o Z, 7 cifras y letra) y CIF (letra de tipo de entidad, 7 cifras y dígito o letra de control). Uno que no cuadra da `400 company.tax_id_invalid` (un error de **campo**). Solo se admiten identificadores **españoles** (D6).
`- **R4. El código postal** son **cinco cifras** y sus dos primeras son una provincia que existe (01 a 52). Si no, `400 company.postal_code_invalid` (de campo).
`- **R5. `isComplete`** es verdadero cuando están los cinco obligatorios (los guarda así el `PUT`: no se puede guardar un perfil incompleto, así que «completo» equivale a «existe»). Es lo que mirará el PDF para decidir si puede generarse.
`- **R6. Modificar sustituye el perfil entero** (D5): la petición lleva todos los campos; un opcional ausente borra el anterior.
`
`**Quién puede y cuánto**
`
`- **R7. Dos permisos nuevos** (D3): `Company.Read` (ver el perfil) y `Company.Manage` (crearlo y modificarlo). `Company.Read`: `Owner`, `Admin`, `Manager` y `Employee` (quien prepara presupuestos necesita ver qué datos llevará el documento); `Technician`, no. `Company.Manage`: solo `Owner` y `Admin` (son datos legales de la empresa). La matriz pasa de 18 a **20 permisos** y de 90 a **100 celdas**.
`- **R8. Todo es de la empresa del token.** La empresa sale **solo del token**; ni un `tenantId` en la petición se lee, y el perfil de otra empresa **no existe** para quien pregunta: el filtro global lo oculta.
`- **R9. La puerta del segundo factor se aplica sola** (ADR-024): ninguna operación del perfil está en la lista cerrada.
`- **R10. Un evento `1409`** («perfil fiscal modificado»: `UserId` y `TenantId`, y si lo **creó** o lo **modificó**). **Nunca** ningún dato del perfil: ni el NIF, ni la dirección, ni el teléfono, ni el email, ni los valores anteriores.
`
`## Modelo conceptual
`
````
`CompanyProfile (ITenantOwned, IAuditable)
`- Id, TenantId                                  uno por empresa: índice único (TenantId)
`- LegalName, TaxId (normalizado), AddressLine, PostalCode, City
`- Province?, Phone?, Email?
`- CreatedAt, UpdatedAt
`
`SpanishTaxId (value object)                     Normalize + IsValid (NIF, NIE y CIF con su control)
`SpanishPostalCode (value object)                cinco cifras, provincia 01..52
`CompanyProfile.Create / Update                   (las invariantes viven en el dominio)
`Permission + CompanyRead, CompanyManage          (los roles: tabla de la R7)
`SecurityEventIds + CompanyProfileChanged = 1409
`
`Endpoints (bajo /api/v1, con sesión):
`GET /company/profile        ver (Company.Read): el perfil, o uno vacío con isComplete=false
`PUT /company/profile        crear o sustituir (Company.Manage): 204
`
`Migración: tabla CompanyProfiles; índice único (TenantId).
````
`
`## Criterios de aceptación
`
`- Dada una empresa **sin** perfil, cuando alguien con `Company.Read` lo pide, entonces `200` con los campos nulos e `isComplete: false` (no un `404`).
`- Dado un `Owner` o `Admin`, cuando guarda un perfil con los cinco obligatorios válidos, entonces `204`, y al leerlo vuelve **normalizado** (NIF en mayúsculas y sin separadores, textos recortados) con `isComplete: true`.
`- Dado un NIF, NIE o CIF **con el control equivocado**, entonces `400` de campo con `company.tax_id_invalid` y **no cambia nada**; y con uno bien formado y **válido** (de cada tipo), se acepta aunque se escriba con espacios, guiones o minúsculas.
`- Dado un código postal de **cuatro o seis cifras, con letras o de provincia 00 o 53**, entonces `400` de campo con `company.postal_code_invalid`.
`- Dado un `PUT` sin un obligatorio, o con un texto pasado de su límite, entonces `400` de campo; y con un opcional ausente, el valor anterior se **borra**.
`- Dado un `Manager` o `Employee`, entonces **puede ver** el perfil pero un `PUT` da `403`; dado un `Technician`, entonces `403` también al leer.
`- Dado un `Owner` o `Admin` **sin** segundo factor, entonces `403 mfa.enrollment_required`.
`- Dada una petición **sin sesión**, entonces `401`.
`- Dada **otra empresa**, entonces **no ve ni modifica** el perfil de la primera (cada empresa ve el suyo, o uno vacío), aunque el `PUT` lleve un `tenantId` ajeno (se ignora).
`- Dado un cambio correcto, entonces queda **un** evento `1409` con `UserId`, `TenantId` y si fue alta o modificación, y **ningún dato del perfil** en ningún registro.
`- Dados **dos `PUT` simultáneos** de una empresa **sin** perfil, entonces queda **un** perfil (nunca dos ni un `500`).
`
`Caso multi-tenant obligatorio: la empresa sale **solo del token**; el perfil de otra empresa no se lee, no se modifica y no se cuenta; el índice único por `TenantId` impide dos perfiles de la misma.
`
`## Seguridad (ADR-009)
`
`- **Datos personales:** **sí, y conviene decirlo.** Para un **autónomo, el NIF es su DNI** y la dirección y el teléfono pueden ser los suyos. Se guardan en la base de datos (como el resto de datos de la empresa), **solo los ven quienes tienen `Company.Read` en esa empresa**, y **nunca** llegan a un registro, a un evento ni a un mensaje de error (el código de error dice «NIF no válido», no el NIF). El `ToString()` del comando **no imprime sus propiedades**.
`- **Autorización:** dos permisos comprobados en Application antes de leer nada; el perfil de otra empresa no existe para quien pregunta.
`- **Entrada:** longitudes máximas desde una fuente única (dominio, validador y EF); NIF y código postal validados por **algoritmo**, no solo por forma; teléfono y email con las reglas que ya existen.
`- **Aislamiento:** `ITenantOwned` + filtro global; índice único por `TenantId` primero; la lista blanca de entidades globales **no** cambia.
`- **SQL crudo:** ninguno.
`- **Abuso:** endpoints con sesión y sin hash costoso; el límite global ya aplica.
`- **Enumeración:** el NIF **no es único entre empresas** (D6): un índice único global dejaría **averiguar si otra empresa ya usa un NIF** probándolo.
`- **Eventos de seguridad:** `1409` (Information) sin ningún dato.
`- **Modelo de amenazas (lo importante):**
`  - **Mejora:** un perfil fiscal **válido y controlado por quien debe** (solo `Owner` y `Admin`) antes de que existan documentos que lo muestren a terceros.
`  - **Riesgo aceptado:** quien tenga `Company.Manage` puede **cambiar el NIF o el nombre fiscal** con los que se emitirán los documentos (el evento `1409` deja constancia de **quién y cuándo**, no de qué valor). No hay historial de valores anteriores; con las facturas (fase 4) esto pedirá una auditoría persistente.
`  - **Riesgo aceptado:** un NIF **bien formado** no es un NIF **que exista**: se valida el dígito de control, no que Hacienda lo conozca ni que sea de esa empresa.
`  - **Riesgo aceptado:** sin control de concurrencia (gana la última escritura, D9).
`
`## Decisiones técnicas relevantes
`
`- **D1. Alcance: solo el perfil fiscal, sin PDF ni correo.** *Confirmada con la persona dueña del proyecto.* Es el prerrequisito del PDF y lo bastante pequeña para verificarse del todo; mezclarla con una librería de PDF haría una spec grande. El PDF pasa a ser la 025 y el correo la 026.
`- **D2. Sin logotipo por ahora.** *Confirmada con la persona dueña del proyecto.* Guardar imágenes añade una decisión de almacenamiento y un riesgo (tipo, tamaño y contenido del fichero) que hoy el proyecto no tiene.
`- **D3. Dos permisos: `Company.Read` (cuatro roles) y `Company.Manage` (`Owner` y `Admin`).** *Confirmada con la persona dueña del proyecto.* Quien prepara presupuestos necesita ver qué datos llevará el documento; modificarlos es una decisión legal de la empresa.
`- **D4. Una entidad propia, `CompanyProfile`, y no columnas nuevas en `Tenant`.** *Recomendado.* `Tenant` es la entidad **global** (la propia empresa) y solo se lee en sitios de una lista blanca; `CompanyProfile` es **de la empresa** (`ITenantOwned`) y hereda el filtro global sin ninguna excepción nueva. Además no toca la tabla ni el panel de la plataforma.
`- **D5. `GET` devuelve un perfil vacío si no existe y `PUT` sustituye todo (crea o modifica).** *Recomendado.* La pantalla trabaja siempre con el mismo formulario; no hay un «crear» y un «modificar» que decidir, y una lectura sin perfil no es un error (es lo normal tras registrarse).
`- **D6. Solo identificadores españoles, validados con su control, y sin unicidad entre empresas.** *Recomendado.* Es el IVA español (D10 de la spec 023). La validación por algoritmo cierra los errores de tecleo; la unicidad global **filtraría** si otra empresa usa un NIF.
`- **D7. El teléfono y el email reutilizan los objetos de valor y las reglas de los clientes** (`PhoneNumber`, `Email`, `ValidPhone`, `ValidEmail`). *Recomendado.* No se duplican reglas ni códigos.
`- **D8. Un evento `1409` sin datos.** *Recomendado.* Deja **quién y cuándo** (suficiente para sospechar de un cambio) sin guardar un dato personal en un registro.
`- **D9. Sin control de concurrencia (gana la última escritura).** *Recomendado para esta spec.* Dos administradores editando a la vez los datos legales es raro; añadirlo es una spec pequeña si hace falta (y las facturas lo pedirán).
`- **D10. Una fecha de alta y de modificación, y nada más de historial.** *Recomendado.* Un historial de valores es la auditoría persistente que pediría la fase 4.
`- **D11. El nombre comercial (`Tenant.Name`) y el nombre fiscal (`LegalName`) son datos distintos.** *Recomendado.* Un fontanero se llama «Fontanería Marta» y factura como «Marta Gil Pérez» o «Fontanería Marta S.L.».
`- **D12. Orden de trabajo:** dominio, validación y persistencia (**bloque 1**), API y pruebas de extremo a extremo (**2**), frontend (**3**) y documentación (**4**). *Recomendado.* Los bloques 1 y 2 se entregan juntos y **el backend puede fusionarse solo** (operaciones nuevas, sin tocar nada existente).
`- **¿Requiere ADR nueva? Sí: ADR-027**, «Perfil fiscal de la empresa: una entidad de la empresa, el NIF validado por algoritmo y datos personales que no se registran», con las decisiones D4 a D8 y anotaciones «Ampliada por» en ADR-012 (los dos permisos y las 100 celdas) y ADR-002 (una entidad más de la empresa).
`
`## Fuera de alcance / Backlog relacionado
`
`- El PDF (spec 025) y su aviso de «datos incompletos»; el correo (spec 026).
`- El logotipo; otros países; varias sedes; el IBAN; el régimen fiscal; la serie de las facturas.
`- Una auditoría persistente de los cambios del perfil (fase 4).
`- Que el panel de la plataforma vea o exija el perfil.
`
`---
`
`## Validación antes de aprobar
`
`> ¿Esta funcionalidad ayuda a conseguir o mantener al primer cliente?
`
`De forma **indirecta pero necesaria**: sin los datos de quien emite, ni el PDF del presupuesto ni, sobre todo, una **factura** (fase 4) son válidos, y el primer autónomo que pruebe el producto lo echará en falta el primer día («¿dónde pongo mi NIF?»). Es **barata y segura de hacer ahora**: poco modelo, una validación con algoritmo bien conocida y una pantalla; y toca un dato personal (el NIF de un autónomo es su DNI), así que conviene decidir **antes** de que existan documentos quién lo ve, quién lo cambia y que nunca llegue a un registro.
