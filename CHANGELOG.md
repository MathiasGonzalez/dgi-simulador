# Cambios

Qué cambia en cada versión del simulador, para los productos que lo usan. La política de versiones está en el [README](README.md#versiones).

## 1.1.0 — 2026-11-13

Controles nuevos del Formato CFE v25.2, que pueden rechazar (E05) un CFE que la 1.0 aceptaba:
- **Indicadores de facturación por régimen:** `MntBruto` 3 y el CAE especial de IVA mínimo o Monotributo se exigen entre sí y no admiten ítems gravados; el CAE de exonerado lleva causal; `IVAalDia` 2 o 3 y `SecProf` 2 solo en cuenta ajena; en la e-Boleta de entrada, los indicadores 13, 14, 15 y 17 según el vendedor y `IndCompraMEReventa`.
- **Propietario de la mercadería** de un remito con `IndPropiedad` 1: tipo, país, documento y nombre.
- **Agente o responsable:** `IndAgenteResp` y la tabla de retenciones del ítem van juntos.
- **Códigos de retención:** `CodRet` es un número mayor que cero. Con `Simulador__CodigosDeRetencion`, además tiene que estar en esa lista.
- **Mandante del complemento fiscal:** obligatorio en la cuenta ajena salvo con `SecProf` 2 (antes se exigía siempre), con país, documento y nombre válidos, y distinto del emisor.
- **GTIN:** con `TpoCod` `GTIN8`, `GTIN12`, `GTIN13` o `GTIN14`, el dígito verificador inválido rechaza. Con `GTIN` o `EAN` sigue siendo advertencia.

Seguridad:
- `Simulador__ClaveAdmin` protege `/admin` con `Authorization: Bearer` o HTTP Basic. Sin definirla todo sigue como antes.
- `POST /admin/certificados` ya no acepta `archivo`: leer del disco queda solo para la semilla.
- `dotnet run` escucha en `localhost` y `compose.yaml` publica en `127.0.0.1`. La imagen no cambia: sigue escuchando en todas las interfaces del contenedor.
- `SECURITY.md` y la sección Seguridad del README.

Los CI de cf-factura (21 tipos y reporte) y de UruFactura (14 pruebas, 39 CFE) pasan contra esta versión sin advertencias.

## 1.0.0 — 2026-11-03

Primera versión publicada con tags fijos. Simula la recepción (`WS_eFactura`), las consultas (`WS_eFactura_Consultas`) y el intercambio entre emisores (`WS_eFactura_Intercambio`) según el Formato CFE v25.2, el Formato del Sobre v05, el Formato del Reporte v13.2, los Mensajes de Respuesta v19 y los XSD 1.44.2, con la API de administración, las fallas programadas y el checklist de homologación. Lo prueban en su CI UruFactura y cf-factura.

Novedades respecto de `latest` anterior:
- `GET /salud`, con la versión y el commit, y `HEALTHCHECK` en la imagen: los servicios de GitHub Actions y `docker compose up --wait` esperan a que esté listo sin sondear el puerto.
- La versión se ve en `/salud`, en la página `/admin` y en `/openapi/v1.json`.
- La imagen se publica también con el tag del major (`1`).
- Falla programada `ocupado`: la empresa simulada responde x002 a las operaciones de intercambio.
- Advertencia E05 por el dígito verificador de un código de ítem `GTIN` o `EAN`.
