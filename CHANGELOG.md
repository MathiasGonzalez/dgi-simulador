# Cambios

Qué cambia en cada versión del simulador, para los productos que lo usan. La política de versiones está en el [README](README.md#versiones).

## 1.0.0 — 2026-11-03

Primera versión publicada con tags fijos. Simula la recepción (`WS_eFactura`), las consultas (`WS_eFactura_Consultas`) y el intercambio entre emisores (`WS_eFactura_Intercambio`) según el Formato CFE v25.2, el Formato del Sobre v05, el Formato del Reporte v13.2, los Mensajes de Respuesta v19 y los XSD 1.44.2, con la API de administración, las fallas programadas y el checklist de homologación. Lo prueban en su CI UruFactura y cf-factura.

Novedades respecto de `latest` anterior:
- `GET /salud`, con la versión y el commit, y `HEALTHCHECK` en la imagen: los servicios de GitHub Actions y `docker compose up --wait` esperan a que esté listo sin sondear el puerto.
- La versión se ve en `/salud`, en la página `/admin` y en `/openapi/v1.json`.
- La imagen se publica también con el tag del major (`1`).
- Falla programada `ocupado`: la empresa simulada responde x002 a las operaciones de intercambio.
- Advertencia E05 por el dígito verificador de un código de ítem `GTIN` o `EAN`.
