# dgi-simulador

Simulador de los servicios web de facturación electrónica de la DGI (Uruguay) para desarrollar y probar en CI sin depender del ambiente de prueba de la DGI.

> **No reemplaza la homologación.** La DGI aprueba la homologación con lo que recibe en su ambiente de prueba (`ePrueba`) y revisa a mano la representación impresa y el intercambio. El simulador sirve para llegar a esa etapa con todo funcionando y para probar casos que contra la DGI no se pueden provocar: rechazos, demoras, caídas y CAE vencidos.

## Qué simula

| Servicio | Rutas | Operaciones |
|---|---|---|
| Recepción (`WS_eFactura`) | `/ePrueba/ws_eprueba`, `/efactura/ws_efactura` | `EFACRECEPCIONSOBRE`, `EFACCONSULTARESTADOENVIO`, `EFACRECEPCIONREPORTE` |
| Consultas (`WS_eFactura_Consultas`) | `/ePrueba/ws_consultasPrueba`, `/efactura/ws_consultas` | estado de CFE, padrón (normal, transición y comprimidos), CFE recibidos, envíos de sobres, CFE y reportes, respuesta a un reporte |
| Intercambio (`WS_eFactura_Intercambio`) | `/intercambio/{rut}` de una empresa simulada | `SOBRE`, `RESPUESTASOBRE`, `RESPUESTACFES`, `NOTIFICACIONANULACIONCFE` |

- **Mensajes:** SOAP 1.1 con WS-Security, como la DGI. Los WSDL oficiales se sirven con `?wsdl`, con la dirección del simulador.
- **Acuses:** `ACKSobre`, `ACKCFE` y `ACKRepDiario` firmados por "la DGI", con la CA de prueba del simulador.
- **Validación:** los acuses y los documentos se validan contra los XSD oficiales, que vienen en `src/DgiSimulador/Esquemas` y `Wsdl`.

Los controles se escribieron a partir de los documentos de la DGI, sin código de los productos que se prueban, para que el simulador no acepte un error que comparte con ellos. Cuánto se parece cada servicio a la DGI, y qué falta confirmar, está en [FIDELIDAD.md](FIDELIDAD.md).

## Arranque

```bash
docker run -p 8080:8080 ghcr.io/mathiasgonzalez/dgi-simulador   # http://localhost:8080
# o, desde este repositorio:
docker compose up          # con --build compila la imagen local
dotnet run --project src/DgiSimulador
```

La imagen se publica en cada cambio de `main` (`latest` y `sha-<commit>`) y con cada tag `vX.Y.Z`, para amd64 y arm64.

La página de inspección está en `http://localhost:8080/admin`. Muestra:
- el padrón, los CAE, los sobres, los CFE con sus motivos y advertencias, los reportes y el intercambio;
- las fallas programadas;
- el avance de homologación de cada emisor.

La API de administración (`/admin/*`) está descripta en OpenAPI en `http://localhost:8080/openapi/v1.json`, para generar un cliente o explorarla con cualquier visor. Los servicios SOAP se describen con sus WSDL (`?wsdl`).

## Configurar un producto

1. **Dar de alta el emisor, con su certificado de prueba y un CAE por cada tipo de CFE que emite:**

   ```bash
   curl -X POST localhost:8080/admin/emisores -H 'Content-Type: application/json' \
     -d '{"rut":"211234560019","denominacion":"Mi Empresa S.A.","mail":"facturas@miempresa.uy","tipos":[101,102,103,111,112,113]}'
   ```

   La respuesta trae:
   - `certificado`: lo emite la CA del simulador, con `SERIALNUMBER=RUC<rut>`, en dos formas: `pfx` (en base64, sin contraseña) y `certificadoPem`/`clavePem`. También se baja con `GET /admin/emisores/{rut}/certificado.pfx`.
   - `caes`: uno por tipo, serie A del 1 al 100000. Cada uno trae `constancia`, el XML con el mismo formato que baja el portal de la DGI, para los productos que importan ese archivo, y `cae`, los datos con los nombres de `UruFactura.Cae`.

   Repetir el alta no autoriza otro CAE para un tipo que ya tiene. Si querés usar un certificado propio, importalo con `POST /admin/certificados` (`{"pem": "..."}`, o con `"raiz": true` para confiar en su CA).

2. **Otros CAE** (otra serie, otro rango, vencidos o especiales), uno por llamada:

   ```bash
   curl -X POST localhost:8080/admin/cae -H 'Content-Type: application/json' \
     -d '{"rut":"211234560019","tipo":101,"serie":"B","desde":1,"hasta":500}'
   ```

3. **Apuntar el producto al simulador:**
   - **UruFactura:** `UrlEnvio = "http://localhost:8080/ePrueba/ws_eprueba"` y `UrlConsultas = "http://localhost:8080/ePrueba/ws_consultasPrueba"`, con el certificado del paso 1.
   - **Cualquier otro producto:** configurá las URL de los servicios de recepción y de consultas con esas mismas direcciones, y registrá el emisor con los PEM del paso 1. En Cloudflare Workers, los tests de `vitest-pool-workers` pueden llamar a `localhost`.

## En CI

En GitHub Actions, el simulador corre como servicio del job y el alta se hace con un `curl`:

```yaml
jobs:
  pruebas:
    runs-on: ubuntu-latest
    services:
      dgi:
        image: ghcr.io/mathiasgonzalez/dgi-simulador:latest
        ports: ["8080:8080"]
    steps:
      - uses: actions/checkout@v4
      - name: Emisor de prueba en el simulador
        run: |
          until curl -sf localhost:8080/openapi/v1.json > /dev/null; do sleep 1; done
          curl -sf -X POST localhost:8080/admin/emisores -H 'Content-Type: application/json' \
            -d '{"rut":"211234560019","tipos":[101,111]}' > emisor.json
          jq -r .certificado.pfx emisor.json | base64 -d > emisor.pfx
      - run: dotnet test   # con las URL de http://localhost:8080 y emisor.pfx
```

Después de las pruebas, `GET /admin/cfes?rut=...` muestra las advertencias, que no viajan en la respuesta SOAP. Con `Simulador__Estricto=true` (en `env:` del servicio) rechazan.

Fuera de GitHub Actions también se puede cargar todo al arrancar con una semilla: [semilla.ejemplo.json](semilla.ejemplo.json), que se activa con `Simulador__Semilla=/ruta/semilla.json`. Acepta emisores con `tipos`, CAE sueltos y certificados propios (`"certificados": [{"archivo": "certs/mio.pem", "rut": "..."}]`, con la ruta relativa a la semilla). Lo que no se puede cargar se informa en el log y no impide arrancar.

## Controles

Los controles siguen los documentos vigentes de la DGI: Formato CFE v25.2, Formato del Sobre v05, Formato del Reporte v13.2 y Formato de los Mensajes de Respuesta v19. Los códigos y las glosas son los de las tablas de ese último documento, que coinciden con las enumeraciones de `Glosa` en `DGITypes.xsd`.

| Etapa | Control | Código |
|---|---|---|
| Mensaje | WS-Security: token, firma del Body y algoritmo (RSA-SHA1 o SHA256) | SOAP Fault |
| Sobre | No es XML o la raíz no es `EnvioCFE` | S01 |
| | RUC de la carátula, del certificado, del envío (WS-Security) y de los CFE | S02 |
| | Certificado vigente y de confianza | S03 |
| | XSD de la carátula y `RutReceptor` | S04 |
| | `CantCFE` contra el contenido | S05 |
| | Certificado de la carátula contra el de las firmas de los CFE, o contra el número de serie que indican | S06 |
| | Más de 2 MB (`TamanoMaximoKb`) | S07 |
| | Sobre ya recibido (mismo RUT e `Idemisor`) | S08 |
| CFE | Número informado como anulado en un reporte | E01 |
| | Número ya recibido, o repetido en el sobre | E02 |
| | Serie, número, rango o vencimiento distintos de los del CAE | E03 |
| | Firma XMLDSig y certificado del emisor. Si la firma trae solo `X509IssuerSerial`, como en el sobre de ejemplo de la DGI, se verifica con el certificado de la carátula | E04 |
| | XSD y reglas del formato. Ítems: monto, precio y entrega gratuita. Totales: por indicador con descuentos y recargos globales, IVA, `MntTotal`, `MntPagar`, retenciones y créditos fiscales. Receptor: datos obligatorios de cada tipo, país y número del documento, secreto profesional, e-Ticket por encima de 5.000 UI o con retenciones. También cobranza propia, referencias, fechas hasta 60 días adelante, firma desde el 2011-10-01 hasta mañana y dígitos verificadores | E05 |
| | Total en pesos mayor que `MontoHabitualMaximo` | E06 |
| | Firmado fuera de la vigencia del CAE | E07 |
| | RUC del complemento fiscal | E08 |
| | CAE inexistente, de otro RUT o de otro tipo | E09 |
| | Tipo de cambio que falta o fuera de rango | E10 |
| | CAE especial y causal | E11, E13 |
| | Referencia a un CFE que la DGI no tiene aceptado, o con fecha, moneda o tipo de cambio distintos, o con un monto mayor que su total a pagar | E12 |
| Contingencia (2xx) | Con problemas que tienen observación equivalente: CE con `ObsCFC` (C03…C15) | CE |
| Reporte | No es XML o la raíz no es `Reporte` | R01 |
| | RUC del reporte, de la firma y del envío | R02 |
| | Firma y certificado | R03 |
| | XSD, `CantDocsUtil` = emitidos + anulados, `CantComprobantes` = utilizados + CFC emitidos | R04 |
| | `SecEnvio` correlativo por fecha | R05 |
| | `FechaResumen` posterior a la recepción | R06 |

**Rechazos y advertencias.** Las reglas que la normativa no deja claras se anotan como **advertencias** y no rechazan. Son estas:
- el redondeo con precios con IVA incluido (`MntBruto=1`);
- los porcentajes globales;
- los totales con ítems a rebajar del e-Remito de Exportación (indicador 8) y con anulaciones del e-Resguardo (indicador 9). El formato escribe "∑B-C24 − ∑B-C24 (si B-C4=8)" y no aclara si la primera suma incluye esos ítems;
- la consistencia del reporte con los CFE del día: cantidades, rangos y montos;
- la firma de un CFE sin `X509IssuerSerial`. La zona I del formato pide el número de serie y el emisor del certificado, y los ejemplos de la DGI siempre lo traen.

Las advertencias se ven en `/admin` y en el checklist. Con `Simulador__Estricto=true` rechazan como las demás.

## Fallas programadas

`POST /admin/fallas` con `{"operacion": "EFACRECEPCIONSOBRE" | "*", "tipo": ..., "veces": 1}`:

| Tipo | Efecto |
|---|---|
| `timeout` | No responde durante `segundos` (por defecto 120) y después devuelve 504 |
| `http500`, `http503` | Error HTTP sin SOAP |
| `html502` | 502 con la página HTML de un proxy |
| `soapfault` | SOAP Fault con `detalle` |
| `rechazo` | `codigo` S.. rechaza el sobre; E.. rechaza cada CFE del sobre; R.. rechaza el reporte. Con la operación `SOBRE`, un rechazo comercial (E20–E26) del receptor simulado |
| `demora` | El resultado del sobre recién se puede consultar después de `segundos` |

## Intercambio entre empresas

Una empresa con `"simulado": true` en el padrón la maneja el simulador.

**Como receptor**, recibe los sobres `EnvioCFE_entreEmpresas` del producto de dos formas:
- **Por su servicio web** (`/intercambio/{rut}`): responde el código 1000 y después manda `ACKSobre` y `ACKCFE` al `urlServicio` del emisor, si está en el padrón.
- **Por correo** (`POST /admin/intercambio/{rut}/correo?nombre=Sob_...xml`, con el XML en el cuerpo): devuelve los archivos `M_` y `ME_`. Si se configura `Simulador:UrlEntregaCorreo`, los entrega como MIME, por ejemplo al handler de correo de `wrangler dev`.

En los dos casos controla el lado receptor (S01–S07, S20 y E02–E07). También recibe y valida las respuestas del producto y sus comunicaciones de anulación.

**Como emisor**, genera una e-Factura para el producto:

```bash
curl -X POST localhost:8080/admin/intercambio/emitir -H 'Content-Type: application/json' \
  -d '{"rutEmisor":"217654330015","rutReceptor":"211234560019","variante":"valido","canal":"descarga"}'
```

- **A la DGI:** la manda a "la DGI", así que aparece en `EFACCONSULTARCFERECIBIDOS` del producto.
- **Al producto:** le entrega el sobre por `ws`, `correo` o `descarga`.
- **Variantes:** `valido`, `firma`, `cae-vencido`, `fuera-de-rango`, `otro-receptor` y `totales`. Cada una prueba los controles del producto como receptor.

## Checklist de homologación

`GET /admin/homologacion/{rut}` (JSON) o `GET /admin/homologacion/{rut}.md` (Markdown) muestra qué casos cumplió el emisor:
- CFE aceptados por tipo y condición: receptor, nota con referencia, moneda extranjera, contingencia;
- reportes;
- intercambio como emisor y como receptor;
- casos manuales, que se marcan con `POST /admin/homologacion/{rut}/manual`.

El set incluido ([set-pruebas.json](src/DgiSimulador/Homologacion/set-pruebas.json)) es **provisorio**: el instructivo oficial no es público. Se baja de DGI en línea → Gestión CFE → Homologación y se pasa con `Simulador__SetDePruebas=/ruta/set.json`.

## Configuración (`Simulador__*`)

| Opción | Por defecto | Para qué |
|---|---|---|
| `Datos` | `datos` | Carpeta de la base SQLite y de las claves de la CA |
| `DemoraConsultaSegundos` | 2 | Desde cuándo se puede consultar el resultado de un sobre (`Fechahora`) |
| `Estricto` | false | Las advertencias rechazan |
| `Tolerancia` | 0.01 | Diferencia admitida en los montos |
| `TamanoMaximoKb` | 2048 | Máximo de un sobre (S07): 2 MB según el Formato del Sobre v05 |
| `TamanoMaximoReporteKb` | 10240 | Máximo de un reporte (R01). La DGI no publica uno |
| `Confianza` | `Registrados` | `Cualquiera` acepta cualquier certificado vigente con RUT |
| `RutsDgi` | 214844360018 y 219999830019 | `RutReceptor` aceptados en el sobre |
| `UnidadIndexada__{año}` | — | UI al 31/12, para el tope de 5.000 UI del año siguiente |
| `TiposDeCambio__{moneda}__Minimo/Maximo` | USD y EUR entre 20 y 80/90 | Rango del tipo de cambio (E10) |
| `MontoHabitualMaximo` | — | Umbral del E06 |
| `ExigirReferenciasConocidas` | true | Si la referencia a un CFE desconocido rechaza (E12) o se advierte |
| `UrlPublica` | la del pedido | Dirección que se publica en los WSDL |
| `UrlEntregaCorreo` | — | Dónde entrega correos el receptor simulado |
| `Semilla`, `SetDePruebas` | — | Archivos JSON que se cargan al arrancar |

## Supuestos a confirmar contra la DGI

Están en [FIDELIDAD.md](FIDELIDAD.md#supuestos-a-confirmar-contra-la-dgi), junto con la estimación de fidelidad de cada servicio y las validaciones que faltan.

## Desarrollo

```bash
dotnet test DgiSimulador.slnx
```

Las pruebas levantan el simulador en memoria, cada clase con su propia base temporal. [EjemplosDgi](tests/DgiSimulador.Tests/EjemplosDgi) tiene el sobre y el reporte de ejemplo que publica la DGI, sin modificar: el XSD, las firmas y las reglas del formato del simulador tienen que aceptarlos. Para probar un producto de punta a punta, levantá el simulador con `dotnet run --project src/DgiSimulador`, que escucha en `http://localhost:8080` (se cambia con `Urls`), y apuntá el producto ahí.

## Normativa DGI

`normativa-dgi.yml` corre los lunes (y a mano con *Run workflow*). Ejecuta `scripts/dgi-normativa.mjs`, que compara las fuentes de [.github/dgi-normativa/fuentes.json](.github/dgi-normativa/fuentes.json) con sus snapshots:
- la página de documentos de e-Factura, donde aparecen las versiones nuevas;
- los WSDL y sus XSD;
- el hash de los documentos que implementa el simulador: formatos, paquete de XSD y servicios web.

Si algo cambió, abre un issue `dgi-normativa` con el diff, asignado a Copilot, que actualiza el simulador y abre la PR. Mientras haya uno abierto no abre otro.

Para que funcione:
- **Secret `COPILOT_ASSIGN_TOKEN`:** PAT fine-grained de un usuario con licencia de Copilot, sobre este repo, con Metadata (read) y Actions, Contents, Issues y Pull requests (read/write). Sin él, el issue se crea sin asignar.
- **Firewall del agente:** en Settings → Copilot → Coding agent, permitir `www.efactura.dgi.gub.uy`, `efactura.dgi.gub.uy` y `www.impo.com.uy`.
- **Entorno del agente:** lo arma `copilot-setup-steps.yml`, con .NET 10 y Node.

Para agregar una fuente, sumala al JSON y corré `node scripts/dgi-normativa.mjs --actualizar`.
