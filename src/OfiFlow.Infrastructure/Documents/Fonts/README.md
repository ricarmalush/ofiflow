# Fuente del PDF

`LiberationSans-Regular.ttf` y `LiberationSans-Bold.ttf`, versión **2.1.5** (30-09-2021), de
<https://github.com/liberationfonts/liberation-fonts> (fichero `liberation-fonts-ttf-2.1.5.tar.gz` de la versión 2.1.5).
Licencia: **SIL Open Font License 1.1** (ver `LICENSE-LiberationSans.txt`): se puede incluir y redistribuir con el programa;
no se puede vender la fuente sola ni usar el nombre reservado «Liberation» para una versión modificada. **No se ha modificado.**

SHA-256:

- `LiberationSans-Regular.ttf` — `76d04c18ea243f426b7de1f3ad208e927008f961dc5945e5aad352d0dfde8ee8`
- `LiberationSans-Bold.ttf` — `788abee4c806d660e8aee46689dd8540cd4bb98da03dcc9d171ce3efd99a9173`

Se incluye en el repositorio (y como recurso embebido en `OfiFlow.Infrastructure.dll`) porque un servidor Linux no trae fuentes del
sistema que se puedan dar por supuestas (spec 025 D8). Un test comprueba estas huellas: cambiar un byte de la fuente exige cambiar también este fichero.
