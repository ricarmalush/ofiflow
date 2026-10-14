# Capítulo 10 — CI/CD y el pipeline de seguridad

## Qué vas a aprender

- Qué es la integración continua (CI) y la entrega continua (CD), y cuál de las dos tiene OfiFlow.
- El recorrido completo de un cambio, desde que lo escribes hasta que entra en `master`.
- Qué ejecuta cada uno de los tres *workflows* y qué comprueba.
- Cómo se protege `master` y por qué ni el dueño del repositorio puede saltarse las reglas.
- Qué hace Dependabot y cómo se evita que te inunde de avisos.
- Qué problemas encontró de verdad el pipeline, y qué le falta todavía.
- Cómo leer un check en rojo y reproducirlo en tu máquina.

## CI y CD

Son dos ideas distintas que suelen ir juntas:

| | Qué es | En OfiFlow |
|---|---|---|
| **CI** (*Continuous Integration*, integración continua) | Cada cambio se **compila y se prueba automáticamente**, en una máquina limpia, antes de aceptarlo | ✅ Existe, y es lo que explica este capítulo |
| **CD** (*Continuous Delivery/Deployment*, entrega continua) | Cada cambio aprobado se **empaqueta y se despliega** a un entorno | ❌ **No existe todavía.** Nada se despliega: no hay servidor de producción, ni artefacto publicado, ni entornos |

Así que el título es más ambicioso que la realidad: hoy tenemos una **CI muy cuidada, con mucha seguridad**, y la entrega llegará cuando haya algo que entregar. Es coherente con el principio del proyecto de "construir para la realidad actual".

## El recorrido de un cambio

Nada entra en `master` directamente. Siempre es así:

```
 ① Escribes el cambio en una rama          feature/lo-que-sea
        │
        ▼
 ② Lo subes y abres un Pull Request (PR)   GitHub lanza los workflows solo
        │
        ▼
 ③ Tres workflows corren EN PARALELO
        │      ci.yml      → build-and-test  →  gitleaks
        │      codeql.yml  → analyze
        │      dast.yml    → zap-scan
        ▼
 ④ Cuatro comprobantes obligatorios en verde
        │      build-and-test · gitleaks · analyze · zap-scan
        ▼
 ⑤ Se fusiona (merge commit)               solo entonces cambia master
```

Si uno de los cuatro falla, **el botón de fusionar se bloquea**. Ese es el sentido de todo el pipeline: que un error no pueda llegar a `master` por descuido.

---

## Los tres workflows

Un *workflow* es un fichero YAML en `.github/workflows/` que le dice a GitHub qué ejecutar y cuándo. Cada uno es un conjunto de *jobs*, y cada job son pasos que corren en una máquina virtual limpia (`ubuntu-latest`).

| Workflow | Cuándo corre | Jobs | Tarda |
|---|---|---|---|
| [`ci.yml`](../../.github/workflows/ci.yml) | Al fusionar en `master` y en cada PR | `build-and-test`, `gitleaks` | ~1 min y ~10 s |
| [`codeql.yml`](../../.github/workflows/codeql.yml) | Igual, **y cada lunes a las 06:00 UTC** | `analyze` | ~2 min |
| [`dast.yml`](../../.github/workflows/dast.yml) | **Solo en PR** (y a mano) | `zap-scan` | ~4 min |

### `ci.yml`: compilar, probar y buscar secretos

**`build-and-test`** hace tres cosas: `dotnet restore`, `dotnet build` y `dotnet test` sobre toda la solución. Dos detalles importantes:

- **El build ya es un control de seguridad.** Los analizadores de seguridad de .NET están configurados como *error* (capítulo 8), así que una regresión rompe aquí, no se queda en un aviso.
- **`dotnet test` ejecuta todo**, incluidos los tests con SQL Server real: el runner de Ubuntu trae Docker preinstalado (capítulo 9). El test de aislamiento entre empresas corre aquí igual que en tu máquina.

**`gitleaks`** busca secretos (contraseñas, claves) escritos por error en el código. No mira solo el cambio: escanea **todo el historial** de commits (`fetch-depth: 0` y `--log-opts="--all"`). Esto tiene una consecuencia práctica que nos pasó de verdad: aunque borres un secreto del código, **sigue en el historial**, y el escáner lo vuelve a encontrar. La excepción se documenta con su huella exacta en [`.gitleaksignore`](../../.gitleaksignore), con la justificación al lado. Solo se ejecuta si `build-and-test` pasó (`needs: build-and-test`).

### `codeql.yml`: análisis del código

**CodeQL** es el analizador de GitHub. A diferencia de los analizadores del compilador, que miran el código línea a línea, CodeQL **sigue el flujo de los datos**: de dónde entra un dato (una petición) y si llega, sin limpiar, a un sitio peligroso (una consulta SQL, un fichero). Detecta cosas como inyecciones que no se ven mirando una sola línea.

Tiene su propio *build* manual (`dotnet build`, no el automático), porque el automático falla a veces con soluciones de varios proyectos. Y corre **cada lunes** aunque no haya cambios: así se enteran de vulnerabilidades nuevas descubiertas en el propio motor.

### `dast.yml`: atacar la API de verdad

**DAST** (*Dynamic Application Security Testing*) es probar la aplicación **en ejecución**, desde fuera, como lo haría un atacante. Es distinto de CodeQL, que lee código sin ejecutarlo. El job hace esto:

1. Levanta un **SQL Server efímero** como *service container* (existe solo mientras dura el job).
2. Aplica las **migraciones** reales.
3. **Arranca la API** en segundo plano, con un secreto JWT generado al azar en ese momento (`openssl rand`).
4. Espera a que responda.
5. Lanza **OWASP ZAP**, una herramienta de ataque automático, contra la descripción OpenAPI de la API (`/openapi/v1.json`).
6. Sube el informe como **artefacto** (`zap-report`), aunque el job falle.

ZAP prueba cosas como cabeceras ausentes, errores que filtran información o rutas que responden de más. De hecho, **su primer informe real encontró un fallo auténtico**: faltaba la cabecera `Cross-Origin-Resource-Policy`, que se añadió después (capítulo 8).

Las reglas que ZAP marca y que **no son vulnerabilidades** (ruido esperado al escanear una API JSON sin frontend) están en [`.zap/rules.tsv`](../../.zap/rules.tsv), cada una ignorada **con su motivo escrito**. Hoy son cuatro, todas informativas. La regla de oro del fichero: *no se escribe una excepción sin un informe real que la justifique*.

---

## Tres ideas de seguridad del propio pipeline

El pipeline también es código, y también se puede atacar. Estas son sus defensas:

### 1. Permisos mínimos

Cada workflow recibe un *token* temporal de GitHub (`GITHUB_TOKEN`) con permisos que **tú eliges**. Aquí es `contents: read` (solo leer el repositorio) en todos, y únicamente `analyze` añade `security-events: write` (para publicar los hallazgos de CodeQL). ZAP intenta por defecto abrir un *Issue* con su informe, pero eso requeriría `issues: write`; en vez de ampliar permisos, se **desactivó esa función** (`allow_issue_writing: false`). Un ejemplo de resolver un fallo sin dar más poder del necesario.

### 2. Acciones fijadas por el commit exacto

Los workflows usan acciones de terceros (`actions/checkout`, `setup-dotnet`...). Si se referencian por etiqueta (`@v4`), **quien controle esa acción puede mover la etiqueta** a código malicioso y tu pipeline lo ejecutaría. Es un ataque a la *cadena de suministro* (*supply chain*). Por eso cada acción va fijada por el **hash completo del commit**:

```yaml
uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1
```

El comentario con la versión es solo para humanos. Una lección que aprendimos: en GitHub, una etiqueta puede apuntar a un *objeto de etiqueta* o directamente al commit; para fijar de verdad hay que usar el **commit** (se obtiene con `git ls-remote ... etiqueta^{}`). Uno de nuestros pines iniciales (`github/codeql-action`) era el objeto de la etiqueta, no el commit, y se corrigió.

### 3. Secretos efímeros, y una excepción razonada

El secreto JWT del job de ZAP se genera nuevo en cada ejecución: **nunca un valor fijo** en el repositorio. Pero hay una contraseña fija en `dast.yml` (`MSSQL_SA_PASSWORD`) para el SQL Server del job. Es una excepción razonada: ese contenedor existe unos minutos, no tiene datos reales y no es accesible desde fuera del runner, así que esa contraseña no protege nada. Aun así, es una **incoherencia con la norma** de "ninguna credencial fija" que seguimos en los tests, y conviene saber explicarla.

---

## La protección de `master`

La rama `master` tiene reglas que GitHub **hace cumplir**, comprobadas en la configuración real:

| Regla | Qué impide |
|---|---|
| Hay que pasar por un **Pull Request** | Subir cambios directamente |
| Los **4 comprobantes** obligatorios en verde: `build-and-test`, `gitleaks`, `analyze`, `zap-scan` | Fusionar con algo roto |
| La rama debe estar **al día con `master`** (*strict*) | Fusionar algo que se probó contra una versión vieja |
| **Sin `force-push`** ni borrado de la rama | Reescribir o perder la historia |
| **Se aplica también a los administradores** (`enforce_admins`) | Que el dueño se salte un check en rojo con un botón |

Hay **0 aprobaciones requeridas**, porque solo hay un mantenedor y no puede aprobarse a sí mismo. Se usa **merge commit** (y no *squash*), para que el historial conserve cada paso, incluidos los arreglos que hicieron falta para pasar los checks.

El efecto de `strict`: si dos PRs están abiertos y fusionas uno, el otro queda "desfasado" y hay que actualizarlo y volver a pasar los checks. Es un coste real (unos 4 minutos de ZAP) a cambio de seguridad.

## Dependabot

**Dependabot** es un robot de GitHub que vigila las dependencias (paquetes NuGet y acciones del pipeline) y **abre un PR cuando sale una versión nueva**. Cada PR pasa por los mismos 4 checks, así que sabes si la actualización rompe algo antes de aceptarla.

Su problema, que nos pasó: sin configurar, abría **un PR por paquete y por proyecto**. Con 3 paquetes de test y 4 proyectos salieron **20 PRs** para 9 actualizaciones reales, y con `strict` cada fusión obligaba a repetir los checks en todos los demás. La solución fue **agrupar**: [`dependabot.yml`](../../.github/dependabot.yml) pide un solo PR por semana y ecosistema. Funcionó de inmediato para las acciones.

Además de los PRs, el repositorio tiene activadas las **alertas de dependencias vulnerables** de GitHub.

## La política del repositorio

El repositorio es **público** (así las herramientas de seguridad de GitHub son gratuitas) y tiene:

- **Secret scanning** y **push protection** activados: GitHub detecta secretos con formato conocido y **bloquea el `push` antes** de que lleguen. Es la primera línea; gitleaks es la segunda.
- **Reporte privado de vulnerabilidades:** quien encuentre un fallo no abre un *issue* público (que lo dejaría visible antes de arreglarlo), sino que lo notifica en privado desde la pestaña Security. Lo explica [`SECURITY.md`](../../SECURITY.md).

---

## Lo que el pipeline encontró de verdad

Un pipeline se justifica por lo que atrapa. Esto es lo que nos atrapó a nosotros:

| Qué | Quién lo cazó | Qué pasó |
|---|---|---|
| Faltaba `dotnet restore` antes de las migraciones | El primer `dast.yml` real | Se añadió el paso |
| La fábrica de contexto de EF tenía la cadena de conexión **de mi LocalDB escrita a mano** | El primer `dast.yml` real, en Linux | Ahora lee la variable de entorno, con LocalDB solo como alternativa |
| ZAP rechazaba el fichero de reglas por tener 2 columnas en vez de 3 | El primer `dast.yml` real | Se corrigió el formato |
| ZAP intentaba abrir un *Issue* y fallaba por falta de permiso | El primer `dast.yml` real | Se desactivó, no se amplió el permiso |
| Faltaba la cabecera `Cross-Origin-Resource-Policy` | El primer informe de ZAP | Se añadió al middleware |
| Una contraseña de ejemplo escrita en un test | gitleaks en el PR de permisos por rol | Se generó al azar y se documentó la huella |
| El pin de `codeql-action` apuntaba al objeto de la etiqueta, no al commit | La revisión manual al actualizar las acciones (`git ls-remote` con la etiqueta pelada) | Se fijó el commit correcto |

Fíjate en lo que tienen en común: **ninguno se habría descubierto sin ejecutar el pipeline de verdad** en la máquina limpia de GitHub. Funcionaba en la nuestra.

---

## Dónde están hoy los límites

**1. No hay entrega continua.** El pipeline comprueba pero no despliega nada: no hay entornos, ni artefactos publicados, ni *rollback*.

**2. El escaneo ZAP no está autenticado.** Ataca solo la superficie anónima (registro, login y renovar) y comprueba que el resto devuelve 401. No recorre las operaciones protegidas; lo compensan los tests de extremo a extremo.

**3. ZAP solo corre en PR, no al fusionar en `master`**, a diferencia de `ci.yml` y `codeql.yml`. Corre además con el entorno de desarrollo, así que no ejercita HSTS.

**4. La política de CodeQL no está hecha cumplir.** El ADR-010 dice que las alertas de CodeQL de severidad alta o crítica deben romper el build. Pero `codeql.yml` no tiene ninguna lógica de fallo por alertas (solo analiza y publica los resultados), no hay *rulesets* en el repositorio que lo exijan, y el único comprobante obligatorio de CodeQL es `analyze`, que comprueba que el análisis *se ejecuta*. Hoy hay 0 alertas abiertas, así que no ha hecho falta, pero una alerta alta **podría fusionarse** si nadie la mira. Es una diferencia entre lo que dice el ADR y lo que hace el pipeline.

**5. Las imágenes de Docker no se vigilan.** El SQL Server del job de ZAP usa la etiqueta móvil `2022-latest` (los tests usan una versión fija distinta), y la imagen de gitleaks va fijada por etiqueta, no por huella. Dependabot cubre NuGet y las acciones, pero no Docker.

**6. Ajustes de GitHub sin activar:** las actualizaciones de seguridad automáticas de Dependabot, los patrones adicionales de *secret scanning* y la validación de si un secreto detectado sigue activo.

**7. No se mide la cobertura de los tests** (capítulo 9) y no se aplica ninguna política de calidad más allá de pasar los tests.

**8. No hay aprobación humana** en los PRs: solo hay un mantenedor. Es una decisión de tamaño, pero significa que ningún segundo par de ojos revisa los cambios.

## Cómo leer un check en rojo

Una receta para cuando un PR falla:

1. **Abre el PR** y baja hasta la lista de checks. El que está en rojo tiene un enlace *Details*.
2. **Entra en el log** y busca la primera línea de error: casi siempre está unas líneas antes del final, no en la última.
3. **Reprodúcelo en tu máquina** con el mismo comando que ejecuta el job:

| Check rojo | Para reproducirlo |
|---|---|
| `build-and-test` | `dotnet build OfiFlow.slnx` y `dotnet test OfiFlow.slnx` |
| `gitleaks` | `docker run --rm -v "$PWD:/repo" -w /repo ghcr.io/gitleaks/gitleaks:v8.30.1 detect --source . --log-opts="--all" --redact -v` (con `-v` ves el fichero, la línea y la regla; el valor sigue oculto) |
| `analyze` | `dotnet build OfiFlow.slnx` (los avisos de seguridad ya son errores) |
| `zap-scan` | Descargar el artefacto `zap-report` del job y leer el informe |

4. **Arréglalo y haz un commit nuevo** en la misma rama: el PR vuelve a ejecutar los checks solo. No hace falta cerrarlo ni abrir otro.
5. **Desconfía de un arreglo que "silencia" el check** (ignorar una regla, ampliar un permiso). Primero pregunta si el hallazgo es real.

## Para comprobar que lo has entendido

1. ¿Qué diferencia hay entre CI y CD, y cuál tiene OfiFlow?
2. Describe el recorrido de un cambio desde la rama hasta `master`.
3. ¿Qué diferencia hay entre lo que comprueba CodeQL y lo que comprueba ZAP?
4. ¿Por qué gitleaks vuelve a encontrar un secreto que ya has borrado del código?
5. ¿Por qué las acciones de terceros se fijan por el hash del commit y no por una etiqueta de versión?
6. ¿Qué impide la regla `enforce_admins` y por qué importa en un proyecto de una sola persona?
7. Dependabot abrió 20 PRs para 9 actualizaciones. ¿Qué pasaba y cómo se arregló?
8. Un check está en rojo. ¿Qué haces, en orden?

*(Respuestas: 1 = CI compila y prueba cada cambio automáticamente; CD lo despliega; OfiFlow solo tiene CI. 2 = rama, push, PR; corren en paralelo `ci.yml`, `codeql.yml` y `dast.yml`; hacen falta los 4 comprobantes en verde (`build-and-test`, `gitleaks`, `analyze`, `zap-scan`) y la rama al día; se fusiona con merge commit. 3 = CodeQL analiza el código sin ejecutarlo, siguiendo el flujo de los datos; ZAP ataca la aplicación en ejecución desde fuera. 4 = porque escanea todo el historial de commits, y el secreto sigue en los commits antiguos; la excepción se registra con su huella exacta en `.gitleaksignore`. 5 = porque una etiqueta puede moverse a código malicioso (ataque a la cadena de suministro); el hash identifica un commit inmutable. 6 = que el dueño del repositorio se salte las reglas (un check en rojo) con un botón; importa porque, siendo la única persona, sin esa regla nada le frenaría en un día de prisa. 7 = sin configurar, abría un PR por paquete y por proyecto, y con `strict` cada fusión obligaba a repetir los checks en todos; se arregló agrupando en `dependabot.yml`. 8 = abrir el log y encontrar el primer error, reproducirlo en local con el mismo comando, arreglarlo con un commit nuevo en la misma rama, y no silenciar el check sin comprobar que el hallazgo no es real.)*

## Siguiente capítulo

El capítulo 11 explica **cómo añadir una funcionalidad** de principio a fin, siguiendo la metodología del proyecto (especificación, tareas, código y tests), con un ejemplo guiado.
