# Fidelidad del simulador

Cuánto se parece cada servicio del simulador a la DGI, en qué se basa esa estimación y qué falta confirmar. Sirve para saber cuánto confiar en un resultado del simulador antes de mandar lo mismo a `ePrueba`.

> **Son estimaciones, no mediciones.** No hay capturas públicas de respuestas reales de `ePrueba`, ni el set de homologación es público. El porcentaje compara el simulador con los documentos oficiales, con los ejemplos que publica la DGI y con productos reales (UruFactura). Usalo como orden de magnitud: lo que vale es la columna "Qué falta".

Última revisión: 2026-10-27, con Formato CFE v25.2, Formato del Sobre v05, Formato del Reporte v13.2, Mensajes de Respuesta v19, XSD 1.44.2, el documento de servicios web de consultas v1.10 y el estándar de intercambio v1.3.

## Criterio

El porcentaje de cada servicio pondera cuatro cosas:

| Qué se mide | Peso |
|---|---|
| Contrato: WSDL, XSD y estructura del pedido y de la respuesta | 30 % |
| Reglas: controles y motivos según los formatos oficiales | 40 % |
| Comportamiento: estados, tiempos y errores | 15 % |
| Evidencia: ejemplos de la DGI y pruebas con productos reales | 15 % |

## Por servicio

| Servicio | Fidelidad | Confianza | Qué la respalda | Qué falta |
|---|---|---|---|---|
| WSDL y XSD (`?wsdl`) | **100 %** | alta | Idénticos byte a byte a los que publica la DGI | Nada |
| Transporte y WS-Security (todos los servicios) | **75 %** | media | Firma del Body con RSA-SHA1, como el ejemplo de invocación de la DGI | No controla `SOAPAction` (la DGI publica uno por operación). Corre en HTTP, no HTTPS. Los textos de los SOAP Fault de la DGI no se conocen |
| `EFACRECEPCIONSOBRE` | **85 %** | media-alta | Motivos y glosas de v19. Límites de 2 MB y 250 CFE. El sobre de ejemplo de la DGI pasa XSD, firmas y reglas. UruFactura: 14 CFE de 8 tipos aceptados | Parte de las validaciones del Formato CFE (ver abajo). E14 y E15 solo se provocan con fallas programadas. El `RutReceptor` de `ePrueba` no está confirmado |
| `EFACCONSULTARESTADOENVIO` | **80 %** | media | `ACKCFE` con AE, BE o CE y sus motivos. `Fechahora` al segundo | Qué responde la DGI antes de `Fechahora`: el simulador devuelve una carátula sin detalle, que no valida contra el XSD |
| `EFACRECEPCIONREPORTE` | **75 %** | media | R01 a R06 según v19. El reporte de ejemplo de la DGI pasa XSD, firma y aritmética interna. El de UruFactura se recibe sin advertencias | Falta revisar campo por campo el Formato del Reporte v13.2. La consistencia con los CFE del día solo advierte |
| `EFACCONSULTARESTADOCFE` | **85 %** | media | Coincide con el ejemplo del documento de consultas: `EstadoCFE`, `IdEmisor`, `IdReceptor` y `ParamConsulta` | Qué responde si el CFE no existe |
| `ENVIOSCFE`, `ENVIOSSOBRE`, `ENVIOSREPORTE` y `RESPUESTAREPORTE` | **80–85 %** | media | Estructuras del XSD y ejemplos del documento | Filtros y casos vacíos que la DGI no documenta |
| `EFACCONSULTARCFERECIBIDOS` | **75 %** | baja | Contrato del XSD | Solo devuelve CFE de las empresas simuladas |
| Padrón (normal, de transición y comprimidos) | **90 %** | alta | gzip y base64 sobre UTF-8, como el anexo de la DGI | Los datos son sintéticos, a propósito |
| Intercambio: `Sobre` | **80 %** | media | Códigos 1000 y 1001. Controles del receptor (S01–S07, S20, E02–E07). Acuses asincrónicos. `Codigo`, `Descripcion` y `Detalle` como el XSD oficial | Nunca responde 1002 ("Error al recibir el Sobre. Intente luego") |
| Intercambio: `RespuestaSobre`, `RespuestaCFEs` y `NotificacionAnulacionCFE` | **80 %** | media | Códigos x000 y x001. Valida XSD y firma | Responde "Recibido." donde el estándar dice "ACK Sobre Recibido.", "ACKCFE Recibido." y "Anulación CFEs Recibido.". Nunca devuelve x002 |

**En conjunto: cerca de 80 %.** Cada servicio pesa según su lugar en la homologación: sobre 40 %, consulta del envío 20 %, reporte 20 %, consultas 10 % e intercambio 10 %.

## Validaciones del Formato CFE que faltan

En estos casos el simulador acepta CFE que la DGI podría rechazar:
- dígito verificador del GTIN;
- indicadores de facturación permitidos según el tipo de CFE y el CAE especial;
- datos del propietario de la mercadería (A-C6.x);
- indicador de agente responsable cuando hay retenciones;
- detalle de la zona K (mandante);
- si los códigos de retención están registrados en la DGI.

## Lo que el simulador advierte y no rechaza

Las reglas que la normativa no deja claras rechazan solo con `Simulador__Estricto=true` (lista en el [README](README.md#controles)). **Las advertencias no viajan en la respuesta SOAP**, porque la DGI no tiene dónde ponerlas: se ven en `GET /admin/cfes`, `GET /admin/reportes`, la página `/admin` y el checklist de homologación. En CI conviene consultarlas después de cada prueba, o correr en modo estricto.

## Supuestos a confirmar contra la DGI

Cuando la DGI responda distinto que el simulador para el mismo XML, agregá el caso como prueba de regresión, con la respuesta real anonimizada, corregí el simulador y actualizá la tabla.

- **`RutReceptor` del sobre en el ambiente de prueba.** Hay productos que usan 214844360018 y otros que usan 219999830019, el "RUT de DGI de pruebas". El simulador acepta los dos. El ejemplo de invocación a `ePrueba` del documento "Servicios Web Externos" usa 214844360018. El sobre de ejemplo de la DGI usa 219999830019, pero como emisor y receptor a la vez, así que no lo resuelve.
- **`EFACCONSULTARESTADOENVIO` antes de `Fechahora`.** Se responde la carátula sin `ACKCFE_Det`. El XSD exige al menos un detalle, así que la DGI debe responder otra cosa.
- **Contingencia.** Con problemas que tienen equivalente en `ObsCFC` queda CE; si no, BE.
- **Diferencias con el CFE referenciado.** El formato exige que fecha, moneda y tipo de cambio de la referencia (F-C7, F-C9 y F-C10) sean los del CFE referenciado, y que el monto (F-C8) no supere su total a pagar. No dice con qué motivo se rechaza; el simulador usa E12.
- **Ejemplos de cuenta ajena de la DGI.** Los dos sobres de ejemplo con complemento fiscal (2011, XSD v1.11) informan todo en `MntNoGrv`, aunque dos ítems son a tasa básica, y no informan IVA. El simulador sigue las fórmulas del formato vigente (A-C112 y A-C117) y los rechaza con E05. Se tomaron como ejemplos de cómo va el complemento fiscal, no de los totales.
- **Tamaño máximo.** "2 Mega" se toma como 2048 KB; si la DGI cuenta 2.000.000 bytes, el margen es de 48 KB. El reporte no tiene un máximo publicado.
- **Validador de .NET.** No admite enteros de más de 29 dígitos en `xs:integer`. El simulador ignora ese error en `ds:X509SerialNumber`, que en los certificados reales tiene 38 dígitos.

## Cómo pasar de estimación a medición

Mandar el mismo XML a `ePrueba` y al simulador, y comparar las respuestas. Hace falta un certificado de prueba de un RUT habilitado en `ePrueba`. Cada diferencia es un supuesto que se confirma o se corrige.

## Mantenimiento

El issue semanal de `normativa-dgi.yml` pide actualizar este documento cuando cambia la normativa. También hay que actualizarlo cuando un cambio en el simulador agrega o corrige un control.

## Fuentes

- [DGI: Servicios Web Externos (recepción)](https://www.efactura.dgi.gub.uy/files/DocumentoServiciosWebExternos?es)
- [DGI: Servicios Web Externos (consultas)](https://www.efactura.dgi.gub.uy/files/web-services-externos-consultas-pdf?es)
- [DGI: Estándar de intercambio entre emisores](https://www.efactura.dgi.gub.uy/files/estandar-de-intercambio-entre-emisores-a-traves-de-servicios-web?es)
- [DGI: Formato de los Mensajes de Respuesta v19](https://www.efactura.dgi.gub.uy/files/formato_mensajes_respuesta_v19-pdf?es)
- [DGI: Formato del Sobre v05](https://www.efactura.dgi.gub.uy/files/Formato_Sobre_v05_pdf?es)
- [DGI: Formato CFE v25.2](https://www.efactura.dgi.gub.uy/files/formato_cfe_v25-2-pdf?es)
- [DGI: Formato del Reporte v13.2](https://www.efactura.dgi.gub.uy/files/formato_reporte_cfe_v13_2-pdf?es)
- [DGI: Ejemplo de Sobre](https://www.efactura.dgi.gub.uy/files/ejemplo-de-sobre?es)
- [Microsoft Q&A: verificar la firma de un XML de la DGI](https://learn.microsoft.com/en-gb/answers/questions/1786451/error-verify-signature-xml-dgi-uy-always-return-fa) (el certificado sale de la carátula; RSA-SHA1)
