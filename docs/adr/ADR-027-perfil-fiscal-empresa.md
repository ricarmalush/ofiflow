# ADR-027 — Perfil fiscal de la empresa: una entidad de la empresa, el NIF validado por algoritmo y datos personales que no se registran

**Estado:** Aceptado (2026-11-09). Las decisiones D1–D12 de la [spec 024](../specs/024-perfil-fiscal-empresa/spec.md) se aprobaron tal como estaban escritas (D1, D2 y D3 confirmadas expresamente con la persona dueña del proyecto: **solo el perfil fiscal, sin PDF ni correo**, **sin logotipo por ahora** y **dos permisos, `Company.Read` y `Company.Manage`**); los cambios que se hicieron al implementar están en [«Lo que cambió al implementar»](#lo-que-cambió-al-implementar).
**Fecha:** 2026-11-09
**Proyecto:** OfiFlow

---

## Contexto

Hasta ahora la empresa solo tenía un nombre: el comercial, el que se eligió al registrarse. Pero un presupuesto, y sobre todo una **factura** (fase 4), necesitan decir **quién emite el documento**: nombre fiscal, NIF y dirección. Sin ellos no se puede generar un PDF que un cliente se tome en serio, y una factura sin esos datos **no es válida**. La [ADR-026](ADR-026-presupuestos.md) anunciaba el PDF como la spec 024; al diseñarlo apareció el prerrequisito, y esta spec lo cierra: **el PDF pasa a ser la spec 025 y el correo la 026**.

Esta spec **no genera ningún documento**. Crea el modelo de los datos, su validación (un NIF con su letra o dígito de control) y la pantalla para mantenerlos. Y toca algo delicado que conviene decidir **antes** de que existan documentos que lo muestren a terceros: **para un autónomo, el NIF es su DNI**, y la dirección y el teléfono pueden ser los suyos. Son datos personales.

## Problema

- ¿Dónde viven estos datos: **columnas nuevas en `Tenant`** o una **entidad propia**? ¿Y cómo hereda el aislamiento por empresa sin abrir ninguna excepción?
- ¿Cómo se **valida un NIF, NIE o CIF**: solo su forma, o su dígito de control? ¿Y quién garantiza que lo guardado esté normalizado?
- ¿Cómo se **crea** el perfil: al registrarse la empresa, con un «crear» y un «modificar» separados, o de otra manera? ¿Qué devuelve la lectura si todavía no existe?
- ¿Cómo se evita que **un dato personal llegue a un registro, a un evento o a un mensaje de error**?
- ¿Quién puede **ver** el perfil y quién **cambiarlo**?
- ¿Cómo se evitan **dos perfiles** de una misma empresa cuando dos personas guardan a la vez por primera vez?
- ¿Cómo se prueba una regla de validación que **se repite en el frontend** como ayuda al teclear?

## Opciones consideradas

### A. Dónde viven los datos

**A1. Una entidad propia, `CompanyProfile`, de la empresa (`ITenantOwned`).** *(elegida; D4)* Hereda el filtro global sin ninguna excepción nueva, no toca la tabla `Tenant` ni el panel de la plataforma, y la lista blanca de entidades globales **no cambia**. Un índice único por `TenantId` garantiza uno por empresa.
**A2. Columnas nuevas en `Tenant`.** `Tenant` es la entidad **global** (la propia empresa) y solo se lee en sitios de una lista blanca: poner aquí datos fiscales los dejaría accesibles desde ellos y mezclaría los datos que ve la plataforma con los que solo debe ver la empresa. Se descarta.

### B. La validación del NIF

**B1. Por algoritmo, con los objetos de valor `SpanishTaxId` y `SpanishPostalCode`.** *(elegida; D6)* Un identificador que no existe **sin ser válido** y que se guarda **normalizado** (mayúsculas, sin espacios, guiones ni puntos). NIF (8 cifras y letra: el resto de dividir entre 23 elige la letra), NIE (X, Y o Z valen 0, 1 y 2 y se calcula como un NIF) y CIF (letra de entidad, 7 cifras y un control que es un dígito, una letra o cualquiera de las dos según la entidad). El código postal son cinco cifras cuyas dos primeras son una provincia que existe (01 a 52).
**B2. Solo la forma (una expresión regular).** Deja pasar cualquier tecleo mal hecho con la forma correcta; el dígito de control existe precisamente para cerrar esos errores. Se descarta.
**B3. Consultar a Hacienda.** Fuera de alcance: exige una integración y datos que no tenemos. Un NIF bien formado **no es** un NIF que exista.

### C. Crear y modificar

**C1. `GET` devuelve un perfil **vacío** si no existe, y `PUT` crea o **sustituye entero**.** *(elegida; D5)* La pantalla trabaja siempre con el mismo formulario; no hay un «crear» y un «modificar» que decidir, y una lectura sin perfil no es un error (es lo normal tras registrarse). Un opcional ausente **borra** el anterior.
**C2. Crear el perfil vacío al registrarse la empresa.** Obligaría a migrar los datos de las empresas existentes y a decidir qué significa un perfil «a medias». El perfil **nace al guardarlo**. Se descarta.
**C3. `POST` para crear y `PATCH` para modificar campos sueltos.** Más superficie, más casos (¿qué pasa con un `PATCH` sin perfil?) y ninguna ventaja aquí. Se descarta.

### D. Datos personales y registro

**D1. Un evento `1409` sin ningún dato, y nada más.** *(elegida; D8)* Deja **quién y cuándo** (suficiente para sospechar de un cambio) y si lo **creó** o lo **modificó**; **nunca** el NIF, la dirección, el teléfono, el email ni los valores anteriores.
**D2. Registrar los valores antes y después** para tener un historial. Guardaría datos personales en un registro (que no tiene el control de acceso ni la retención de la base de datos). Un historial de valores es la **auditoría persistente** que pedirá la factura (fase 4), no un registro de texto. Se descarta.

### E. Permisos

**E1. Dos: `Company.Read` (`Owner`, `Admin`, `Manager` y `Employee`) y `Company.Manage` (solo `Owner` y `Admin`).** *(elegida; D3)* Quien prepara presupuestos necesita ver qué datos llevará el documento; modificarlos es una decisión **legal** de la empresa. `Technician`, ninguno.
**E2. Un solo permiso.** O todos ven y cambian, o solo los administradores ven; ninguna de las dos sirve. Se descarta.

### F. Concurrencia

**F1. Sin control (gana la última escritura).** *(elegida; D9)* Dos administradores editando a la vez los datos legales es raro. Añadirlo es una spec pequeña si hace falta, y las facturas lo pedirán.
**F2. Versión de fila como en los presupuestos (ADR-026).** Más correcto, pero cada `PUT` lleva la versión leída y la pantalla debe resolver el conflicto: una complejidad que hoy no compra nada. Se descarta por ahora.

## Decisión

### 1. El dominio

- **`CompanyProfile`** (`ITenantOwned`, `IAuditable`), **uno por empresa** (índice único por `TenantId`), con `LegalName` (hasta 200), `TaxId` (normalizado, nueve caracteres), `AddressLine` (200), `PostalCode`, `City` (100) y, opcionales, `Province` (100), `Phone` y `Email`. `Create` y `Update` reciben los **objetos de valor ya construidos**: el dominio no puede guardar un NIF o un código postal que no lo sea.
- **`SpanishTaxId.Normalize`** pasa a mayúscula **solo las letras ASCII** y quita el espacio en blanco, los guiones y los puntos. Hay un tope de **veinte caracteres antes de normalizar** (para que nada recorra una entrada sin límite y para rechazar por longitud antes que por forma).
- **`LegalName` y `Tenant.Name` son datos distintos** (D11): un fontanero se llama «Fontanería Marta» y factura como «Marta Gil Pérez» o «Fontanería Marta S.L.».
- **`IsComplete` solo existe en el DTO.** Como el `PUT` exige los cinco obligatorios, «completo» equivale a «existe»: el único DTO incompleto es el vacío. Es lo que mirará el PDF (spec 025) para saber si puede generarse.
- **Los NIF especiales K, L y M** (menores y extranjeros sin NIE) **no se admiten por ahora** (D6).

### 2. La persistencia y la carrera del alta

- **Migración `AddCompanyProfile`:** una tabla y un índice único. Ninguna otra tabla cambia y **no hay migración de datos**: las empresas existentes no tienen perfil hasta que lo guardan.
- **El primer guardado es un `Add` con su `SaveChanges`.** Si dos personas guardan a la vez por primera vez, el índice único rechaza a la segunda con un `DbUpdateException`; el handler **saca la entidad fallida del seguimiento**, vuelve a leer, **modifica** el perfil que ganó y confirma. Si tras el fallo no hay nada que leer, **relanza** (no esconde un error distinto). Se probó con SQL Server real: ocho rondas de cuatro altas simultáneas dejan **un** perfil y ningún error, y en vivo, ocho `PUT` simultáneos dieron ocho `204` y una fila.
- **El NIF no es único entre empresas.** Un índice único global dejaría **averiguar si otra empresa ya usa un NIF** probándolo. Se probó que dos empresas con el mismo NIF no notan ninguna diferencia.

### 3. Quién, cuánto y qué se registra

- **Permisos:** `Company.Read` y `Company.Manage`. La matriz pasa a **20 permisos y 100 celdas** y la tabla de operaciones, a **57**; los tests que fijan esas cifras fallaron a propósito.
- **La autorización va antes que la validación** (`[RequiresPermission]` en el comando y en la consulta, comprobado por un comportamiento de MediatR): un `Manager` con un cuerpo inválido recibe `403`, no los errores del NIF, así que **quien no puede escribir no averigua nada**.
- **La puerta del segundo factor** ([ADR-024](ADR-024-segundo-factor-obligatorio.md)) se aplica sola: ninguna operación del perfil está en su lista cerrada, así que un `Owner` o `Admin` sin factor recibe `403 mfa.enrollment_required`.
- **Un único evento nuevo, `1409`** («perfil fiscal guardado»): `UserId`, `TenantId` y si fue alta o modificación. Ya hay **54 eventos**. **Ningún dato del perfil llega a un registro:**
  - el comando y el DTO tienen un `ToString()` que imprime solo el nombre del tipo (el de un `record` imprimiría todo);
  - los errores de dominio llevan **solo el código** («NIF no válido», no el NIF);
  - un test de arquitectura fija que **el único fichero que escribe en el registro es el handler de guardar, con la firma exacta `(ILogger, Guid? userId, Guid tenantId, bool created)`**: añadir un parámetro con un dato lo rompe;
  - se comprobó en el registro real de la API que el `1409` no contiene el NIF, la dirección, el teléfono ni el nombre.
- **Códigos de error:** dos nuevos, de **campo**: `company.tax_id_invalid` y `company.postal_code_invalid`. Ya hay **66**. El teléfono y el email reutilizan los de los clientes (`customer.phone_invalid`, `common.email_invalid`) y sus reglas (D7).

### 4. Los endpoints

Dos bajo `/api/v1/company/profile`, siempre con sesión: `GET` (devuelve el perfil o uno vacío con `isComplete = false`) y `PUT` (crea o sustituye, `204`). **La empresa sale solo del token:** ni el cuerpo ni la ruta llevan ninguna, y un `tenantId`, un `id` o un `isComplete` de más se **ignoran**. **La lectura lleva `Cache-Control: no-store`** (son datos personales: ni caché intermedia ni la del navegador). El perfil de otra empresa no existe para quien pregunta.

### 5. El frontend

Una pantalla **«Datos de la empresa»** (`/company`, entrada de menú en «Administración» con `Company.Read`) en el mismo formulario para todos: **editable solo con `Company.Manage`**, y sin él, deshabilitado y sin botón (y `save()` no envía nada aunque se llame a mano). Los errores de la API se pintan **bajo su campo**; mientras el perfil esté vacío sale un aviso para completarlo (con otro texto para quien no puede); tras guardar se **vuelve a leer** para mostrar el NIF tal como quedó (normalizado). La **validación del NIF y del código postal en pantalla es una ayuda al teclear**: copia fiel del algoritmo y probada contra **los mismos 89 casos fijos** que el backend (`TaxIdVectors.json`, copiado como datos). Manda siempre la API. Los datos **no se guardan en ningún sitio del navegador**, solo viven en el formulario.

## Consecuencias

### Positivas

- **Un perfil fiscal válido y controlado** por quien debe (solo `Owner` y `Admin`) antes de que existan documentos que lo muestren a terceros.
- **Un NIF que no cuadra no entra:** el dígito de control cierra los errores de tecleo, y lo guardado está siempre normalizado y es de nueve caracteres.
- **El aislamiento sale gratis:** una entidad de la empresa hereda el filtro global sin excepciones; el perfil de otra empresa no existe para quien pregunta y el NIF repetido entre empresas no se nota.
- **Los datos personales no se filtran por el lado que no se mira:** ni al registro, ni a un evento, ni a un error, ni a una caché; y una prueba de arquitectura lo vigila.
- **Una validación, probada contra una tabla fija compartida,** incluidos identificadores reales y públicos (Telefónica, Santander, Inditex, Iberdrola, Repsol y organismos): si el frontend y el backend discrepan, un test lo dice.
- **El backend puede desplegarse solo:** operaciones nuevas y una tabla nueva, sin tocar nada existente.

### Negativas / riesgos aceptados

- **Quien tenga `Company.Manage` puede cambiar el NIF o el nombre fiscal** con los que se emitirán los documentos. El evento `1409` deja constancia de **quién y cuándo**, no de **qué valor**; no hay historial de valores anteriores (D10). Con las facturas (fase 4) esto pedirá una **auditoría persistente**.
- **Un NIF bien formado no es un NIF que exista:** se valida el dígito de control, no que Hacienda lo conozca ni que sea de esa empresa.
- **Sin control de concurrencia (D9):** gana la última escritura. Dos administradores editando a la vez pueden pisarse sin saberlo.
- **Solo identificadores españoles** (D6): un NIF-IVA europeo, otro país u otro formato de dirección es una ampliación. Los K, L y M no se admiten.
- **El NIF, la dirección y el teléfono son datos personales de un autónomo** y se guardan **en claro** en la base de datos, como el resto de datos de la empresa. Los protegen el aislamiento por empresa y el permiso `Company.Read`; **no hay cifrado de columna** (si el proveedor de la base de datos exige cifrar en reposo, es una decisión de infraestructura, no de esta spec).
- **El cálculo existe en dos sitios.** Los 89 casos fijos protegen la copia del frontend, pero **se mantiene a mano** (no hay un test que compare los dos ficheros entre repositorios).
- **El email lo valida Angular en la pantalla y la API con su regla:** `a@b` pasa la ayuda y lo rechaza la API. Manda la API y su error se pinta bajo el campo.
- **«Completo» equivale a «existe» solo mientras el `PUT` exija los cinco obligatorios:** si algún día un perfil puede guardarse a medias (por ejemplo, al importar), `isComplete` dejaría de ser equivalente a «existe» y habría que calcularlo.

### Lo que cambió al implementar

1. **`IsComplete` solo en el DTO**, no en la entidad (R5 de la spec): la entidad no puede estar incompleta, así que no tiene nada que calcular.
2. **Los objetos de valor son los que garantizan la forma:** `SpanishTaxId` y `SpanishPostalCode` no existen sin ser válidos y `CompanyProfile` solo recibe los ya construidos.
3. **Solo se pasan a mayúscula las letras ASCII.** La primera idea, `ToUpperInvariant`, habría convertido la «ſ» (s larga) en una «S» válida: lo destapó una mutación.
4. **El espacio en blanco del frontend se escribió a mano.** El `\s` de JavaScript difiere del `char.IsWhiteSpace` de .NET (incluye U+FEFF y no incluye U+0085); un carácter así daría un veredicto distinto en la pantalla y en la API.
5. **Un fallo real, encontrado por un test:** con el NIF ausente en el JSON (`null`), el validador lanzaba `NullReferenceException` (un `500`). Ahora cada campo da **un único error** y un valor ausente nunca llega a la regla de forma.
6. **Otro fallo real, en el frontend:** `Validators.required` da por bueno un campo con solo espacios, y la pantalla enviaba un `PUT` con un obligatorio vacío tras recortar. Ahora el «obligatorio» recorta.
7. **Los fixtures de Infrastructure no llevan el interceptor de auditoría:** `CreatedAt` y `UpdatedAt` salían por defecto hasta que el test lo añadió a propósito.
8. **Los tests que cuentan permisos, celdas y operaciones fallaron a propósito** (18 → 20, 90 → 100 y 55 → 57), igual que la lista de entidades de una empresa (ahora `CompanyProfile`).

### Hallazgos de la implementación (útiles para el tribunal)

- **Las mutaciones destapan los tests flojos, no el código:** del algoritmo del NIF sobrevivían la tabla de las 23 letras (ningún test recorría todos los restos), los tipos `N`, `R` y `S` del CIF (se añadieron vectores con dígito), el carácter «ſ» y el dígito no ASCII **después** de uno ASCII. En el frontend, el recorte del NIF, la dirección, la población y el código postal, y un opcional de solo espacios. Cada uno se cerró con un test.
- **Los mutantes equivalentes se documentan, no se esconden:** `IsDigit` en lugar de `IsAsciiDigit` en la primera cifra del NIF (la comprobación posterior de cifras ASCII ya lo rechaza) y en el código postal (el rango de provincias ya rechaza cualquier cifra de otro alfabeto); la longitud normalizada que solo rechaza las largas; el texto vacío que cuenta como cifras; el teléfono de 21 caracteres (el patrón ya lo rechaza); y «nunca se busca el perfil existente» (a nivel API, el índice único hace fallar el segundo `INSERT` y el reintento acaba sustituyendo igual).
- **Un test contra SQL Server real vale más que uno con dobles** para la carrera del alta: solo con contextos distintos se ve que dos altas no dejan dos perfiles y que el que pierde no recibe un error.
- **Un identificador real vale más que uno calculado por mí:** el algoritmo se contrastó con los de grandes empresas y organismos públicos, no solo con los que yo mismo calculé con la misma fórmula.
- **Mis propias comprobaciones también se equivocan:** la primera pasada en vivo falló por **dos errores de mi guion** (una consulta a la columna equivocada, que dejó sin probar la carrera, y una comparación de un `GUID` en mayúsculas contra uno en minúsculas); se corrigió y se repitió **entera** con la base y la API reiniciadas. Y una pasada de mutaciones del frontend **se descartó** por lanzar dos copias del bucle a la vez sobre los mismos ficheros: se comprobó que las fuentes estaban restauradas y se repitió limpia.
- **En el navegador:** un `Employee` ve la pantalla en solo lectura y sin botón; un `Technician` no tiene entrada de menú y al escribir la URL llega a «Acceso denegado»; un NIF con la letra equivocada da su error bajo el campo **sin enviar nada**; `12.345.678-z` se guarda y se ve como `12345678Z`; y `a@b` lo rechaza la API con su mensaje bajo el campo.

### Cifras

- **Backend:** Domain 615, Application 1215, Infrastructure 879 y Api 1043 = **3752 casos** (+305 respecto a la spec 023), todo en verde. **138 de 141 mutaciones detectadas** (109 de 111 del dominio y la persistencia y 29 de 30 de la API; las tres que sobreviven son equivalentes). Comprobado en vivo con la API real, SQL Server real, Mailpit y TOTP calculado: **76/76**, con la base de datos coherente (NIF normalizado de nueve caracteres, ninguna empresa con dos perfiles, `CreatedAt` y `UpdatedAt` rellenos).
- **Frontend:** **849 specs** en verde (+156), **66 de 69 mutaciones** detectadas (las tres restantes son equivalentes) y comprobado en el navegador integrado contra la API real.

### Recomendación de despliegue (no es código)

1. **Aplicar la migración `AddCompanyProfile`** (una tabla nueva, nada más). Las migraciones pendientes de otras specs (016, 018, 020, 021 y `AddQuotes`) siguen su propio ritmo.
2. **El backend puede desplegarse solo:** añade operaciones nuevas y una tabla. El frontend va después (sin él el perfil existe pero nadie lo rellena).
3. **Los permisos se reparten solos** (la tabla es parte del código): `Owner` y `Admin` gestionan, `Manager` y `Employee` ven, `Technician` no ve nada. Avisar a quien lo vaya a notar.
4. **Ninguna empresa tiene perfil** al desplegar: la pantalla les pedirá completarlo la primera vez. No hay datos que migrar.
5. **Un `Owner` o `Admin` sin segundo factor** recibe `403 mfa.enrollment_required` también aquí (spec 022).
6. **Probar de punta a punta con una cuenta real** antes de abrir a clientes: guardar un NIF, un NIE y un CIF reales de prueba, comprobar que el registro de la API no los contiene y que un `Employee` no puede modificarlos.

### Qué queda cerrado (no se debe reabrir sin una razón nueva y explícita)

- **Una entidad propia de la empresa**, no columnas en `Tenant`.
- **El NIF, NIE y CIF validados por algoritmo** y guardados normalizados, y **no únicos entre empresas**.
- **`GET` vacío y `PUT` que sustituye entero:** la pantalla trabaja siempre con el mismo formulario.
- **Ningún dato del perfil en un registro, un evento, un error o una caché:** el evento `1409` lleva solo quién, la empresa y si fue alta o modificación.
- **Ver y gestionar son permisos distintos,** y un `Technician` no ve el perfil.
- **La empresa sale solo del token.**
- **La autorización antes que la validación.**

### Qué queda abierto para revisar más adelante

- **El PDF del presupuesto: hecho en la spec 025 ([ADR-028](ADR-028-pdf-presupuesto.md))**, con el aviso en la pantalla del presupuesto cuando faltan estos datos (`400 quote.company_profile_incomplete`). Queda **el envío por correo** (spec 026).
- **El logotipo** (imágenes: almacenamiento, tamaño y seguridad del fichero), otros países y un NIF-IVA europeo, **varias sedes**, el **IBAN**, el régimen fiscal y la serie de numeración de las facturas.
- **Una auditoría persistente de los cambios del perfil** (con los valores anteriores), que pedirá la factura (fase 4), y quizá **control de concurrencia**.
- **Los NIF K, L y M.**
- **Que el panel de la plataforma vea o exija el perfil.**
- **Un test que compare los casos fijos entre los dos repositorios.**

---

## Relación con otros ADR

- Amplía: **[ADR-012](ADR-012-autorizacion-rbac.md)** (dos permisos nuevos y la matriz de 100 celdas), **[ADR-002](ADR-002-multi-tenancy.md)** (una entidad más de la empresa, sin ninguna excepción al filtro), **[ADR-011](ADR-011-errores-codigos-diccionario.md)** (dos códigos nuevos; ya hay 66).
- Relacionada con: **[ADR-009](ADR-009-seguridad-base.md)** (datos personales: ni el NIF ni la dirección llegan a un registro), **[ADR-024](ADR-024-segundo-factor-obligatorio.md)** (la puerta se aplica sola), **[ADR-026](ADR-026-presupuestos.md)** (los presupuestos son los primeros documentos que necesitan estos datos), **[ADR-008](ADR-008-testing-integracion.md)** (el SQL Server real que prueba la carrera del alta).
- Ampliada por: **[ADR-028](ADR-028-pdf-presupuesto.md)** (spec 025): estos datos son lo que imprime el emisor del PDF del presupuesto; sin ellos no hay PDF.
- Prepara: el PDF (spec 025, ya hecho) y el correo del presupuesto (spec 026) y la factura (fase 4), que exigirá una auditoría persistente de estos datos.
