using System.Net;
using System.Text;
using DgiSimulador.Estado;
using DgiSimulador.Homologacion;
using DgiSimulador.Recepcion;
using Microsoft.Extensions.Options;

namespace DgiSimulador.Admin;

/// <summary>La página de inspección (<c>GET /admin</c>): configuración, padrón, CAE, lo recibido, fallas y homologación.</summary>
internal static class Pagina
{
    internal static IResult Inicio(HttpContext contexto, Almacen almacen, SetDePruebas set, IOptions<OpcionesSimulador> opciones)
    {
        var o = opciones.Value;
        var baseUrl = (o.UrlPublica ?? $"{contexto.Request.Scheme}://{contexto.Request.Host}").TrimEnd('/');
        var emisores = almacen.Emisores();
        var sb = new StringBuilder();
        sb.Append($$"""
            <!doctype html>
            <html lang="es"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Simulador DGI</title>
            <style>
            :root { --fondo:#fafafa; --texto:#1d1d1f; --suave:#6b6b70; --borde:#dedee3; --tarjeta:#fff; --ok:#1f7a3d; --mal:#b3261e; --aviso:#8a5a00; --acento:#2d5bd2; }
            @media (prefers-color-scheme: dark) { :root { --fondo:#16171a; --texto:#ececf0; --suave:#9a9aa3; --borde:#33343a; --tarjeta:#1f2024; --ok:#5fcf86; --mal:#ff8a80; --aviso:#f0c060; --acento:#8fb0ff; } }
            body { margin:0; background:var(--fondo); color:var(--texto); font:14px/1.45 system-ui, -apple-system, "Segoe UI", sans-serif; }
            main { max-width:1200px; margin:0 auto; padding:24px 16px 64px; }
            h1 { font-size:22px; margin:0 0 4px; } h2 { font-size:16px; margin:32px 0 8px; }
            p.sub { color:var(--suave); margin:0 0 16px; }
            section { background:var(--tarjeta); border:1px solid var(--borde); border-radius:8px; padding:12px 16px; overflow-x:auto; }
            table { border-collapse:collapse; width:100%; } th, td { text-align:left; padding:6px 8px; border-bottom:1px solid var(--borde); vertical-align:top; }
            th { color:var(--suave); font-weight:600; font-size:12px; } td.num { font-variant-numeric:tabular-nums; }
            code { font:12px ui-monospace, SFMono-Regular, Menlo, monospace; }
            .AE, .AS, .AR, .ok, .valido, .entregado { color:var(--ok); } .BE, .BS, .BR, .mal, .invalido, .error { color:var(--mal); } .CE, .aviso, .pendiente { color:var(--aviso); }
            a { color:var(--acento); } ul { margin:4px 0; padding-left:18px; } .vacio { color:var(--suave); }
            </style></head><body><main>
            <h1>Simulador de los servicios de la DGI</h1>
            <p class="sub">Para desarrollo y CI. No reemplaza la homologación de la DGI. Datos en JSON en <a href="/admin/sobres">/admin/sobres</a>, <a href="/admin/cfes">/admin/cfes</a>, <a href="/admin/reportes">/admin/reportes</a> e <a href="/admin/intercambio">/admin/intercambio</a>.</p>
            <section><table>
            <tr><th>Recepción (ws_efactura)</th><td><code>{{H(baseUrl)}}/ePrueba/ws_eprueba</code></td></tr>
            <tr><th>Consultas</th><td><code>{{H(baseUrl)}}/ePrueba/ws_consultasPrueba</code></td></tr>
            <tr><th>Intercambio de una empresa simulada</th><td><code>{{H(baseUrl)}}/intercambio/{rut}</code></td></tr>
            <tr><th>CA de prueba</th><td><a href="/admin/ca.pem">/admin/ca.pem</a></td></tr>
            <tr><th>Modo</th><td>{{(o.Estricto ? "estricto: las advertencias rechazan" : "normal: las advertencias solo se anotan")}} · demora de consulta {{o.DemoraConsultaSegundos}} s · confianza {{H(o.Confianza)}}</td></tr>
            </table></section>
            """);

        sb.Append("<h2>Padrón</h2><section>");
        Tabla(sb, ["RUT", "Denominación", "Correo", "Servicio web", "Simulada", "Certificado"], emisores.Select(e => new[]
        {
            H(e.Rut), H(e.Denominacion), H(e.Mail), H(e.UrlServicio), e.Simulado ? "sí" : "", $"<a href=\"/admin/emisores/{H(e.Rut)}/certificado.pfx\">PFX</a>",
        }));
        sb.Append("</section>");

        sb.Append("<h2>CAE</h2><section>");
        Tabla(sb, ["Número", "RUT", "Tipo", "Rango", "Vigencia", "Constancia"], almacen.Caes().Select(c => new[]
        {
            c.Id.ToString(), H(c.Rut), H(TiposCfe.Nombre(c.Tipo)), $"{H(c.Serie)} {c.Desde}–{c.Hasta}",
            $"{c.Autorizacion:yyyy-MM-dd} a {c.Vencimiento:yyyy-MM-dd}{(c.Especial is { } esp ? $" · especial {esp}" : "")}",
            $"<a href=\"/admin/cae/{c.Id}/constancia.xml\">XML</a>",
        }));
        sb.Append("</section>");

        sb.Append("<h2>Últimos sobres</h2><section>");
        Tabla(sb, ["Id receptor", "RUT", "Id emisor", "Estado", "CFE", "Recibido", "Motivos"], almacen.Sobres(limite: 30).Select(s => new[]
        {
            $"<a href=\"/admin/sobres/{s.IdReceptor}\">{s.IdReceptor}</a>", H(s.Rut), s.IdEmisor.ToString(), Estado(s.Estado), s.Cantidad.ToString(),
            Hora.Formato(s.Recibido), Lista(s.Motivos.Select(m => m.ToString())),
        }));
        sb.Append("</section>");

        sb.Append("<h2>Últimos CFE</h2><section>");
        Tabla(sb, ["Id", "RUT", "Comprobante", "Fecha", "Total", "Estado", "Motivos", "Advertencias"], almacen.Cfes(limite: 50).Select(c => new[]
        {
            $"<a href=\"/admin/cfes/{c.Id}\">{c.Id}</a>", H(c.Rut), H($"{(c.Tipo is { } t ? TiposCfe.Nombre(t) : "?")} {c.Serie}{c.Nro}"),
            c.Fecha?.ToString("yyyy-MM-dd") ?? "", $"{H(c.Moneda)} {c.Total:0.00}", Estado(c.Estado), Lista(c.Motivos.Select(m => m.ToString())),
            Lista(c.Advertencias, "aviso"),
        }));
        sb.Append("</section>");

        sb.Append("<h2>Reportes diarios</h2><section>");
        Tabla(sb, ["Id receptor", "RUT", "Fecha", "Secuencia", "Estado", "Motivos", "Advertencias"], almacen.Reportes(limite: 30).Select(r => new[]
        {
            $"<a href=\"/admin/reportes/{r.IdReceptor}\">{r.IdReceptor}</a>", H(r.Rut), r.FechaResumen?.ToString("yyyy-MM-dd") ?? "", r.Secuencia?.ToString() ?? "",
            Estado(r.Estado), Lista(r.Motivos.Select(m => m.ToString())), Lista(r.Advertencias, "aviso"),
        }));
        sb.Append("</section>");

        sb.Append("<h2>Intercambio</h2><section>");
        Tabla(sb, ["Id", "Dirección", "Canal", "Mensaje", "De", "Para", "Archivo", "Estado", "Respuesta"], almacen.Intercambios(limite: 30).Select(m => new[]
        {
            m.Id.ToString(), H(m.Direccion), H(m.Canal), H(m.Tipo), H(m.RutEmisor), H(m.RutReceptor),
            $"<a href=\"/admin/intercambio/{m.Id}/xml\">{H(m.Nombre ?? "XML")}</a>", Estado(m.Estado), H(m.Respuesta is { Length: > 200 } r ? r[..200] + "…" : m.Respuesta),
        }));
        sb.Append("</section>");

        sb.Append("<h2>Fallas programadas</h2><section>");
        Tabla(sb, ["Id", "Operación", "Tipo", "Código", "Restantes", "Segundos"], almacen.Fallas().Select(f => new[]
        {
            f.Id.ToString(), H(f.Operacion), H(f.Tipo), H(f.Codigo), f.Restantes.ToString(), f.Segundos?.ToString() ?? "",
        }));
        sb.Append("</section>");

        foreach (var emisor in emisores.Where(e => !e.Simulado))
        {
            var avance = set.Avance(emisor.Rut);
            sb.Append($"<h2>Homologación de {H(emisor.Denominacion)} ({H(emisor.Rut)}): {avance.Cumplidos} de {avance.Obligatorios}</h2><section>");
            sb.Append($"<p class=\"sub\">Set de pruebas {H(avance.Version)}. <a href=\"/admin/homologacion/{H(emisor.Rut)}.md\">Markdown</a> · <a href=\"/admin/homologacion/{H(emisor.Rut)}\">JSON</a></p>");
            Tabla(sb, ["Caso", "Estado", "Evidencia"], avance.Casos.Select(c => new[]
            {
                H(c.Caso.Descripcion),
                c.Cumplido ? (c.ConAdvertencias > 0 ? "<span class=\"aviso\">cumplido, con advertencias</span>" : "<span class=\"ok\">cumplido</span>")
                    : c.Caso.Opcional ? "<span class=\"vacio\">opcional</span>" : "<span class=\"pendiente\">pendiente</span>",
                Lista(c.Evidencias.Take(3)),
            }));
            sb.Append("</section>");
        }

        sb.Append("</main></body></html>");
        return Results.Content(sb.ToString(), "text/html; charset=utf-8", Encoding.UTF8);
    }

    private static void Tabla(StringBuilder sb, string[] columnas, IEnumerable<string[]> filas)
    {
        var lista = filas.ToList();
        if (lista.Count == 0)
        {
            sb.Append("<p class=\"vacio\">Nada todavía.</p>");
            return;
        }
        sb.Append("<table><tr>");
        foreach (var c in columnas)
            sb.Append("<th>").Append(H(c)).Append("</th>");
        sb.Append("</tr>");
        foreach (var fila in lista)
        {
            sb.Append("<tr>");
            foreach (var celda in fila)
                sb.Append("<td>").Append(celda).Append("</td>");
            sb.Append("</tr>");
        }
        sb.Append("</table>");
    }

    private static string Estado(string estado) => $"<span class=\"{H(estado)}\">{H(estado)}</span>";

    private static string Lista(IEnumerable<string> items, string? clase = null)
    {
        var lista = items.ToList();
        return lista.Count == 0 ? "" : $"<ul{(clase is null ? "" : $" class=\"{clase}\"")}>" + string.Concat(lista.Select(i => $"<li>{H(i)}</li>")) + "</ul>";
    }

    private static string H(string? texto) => WebUtility.HtmlEncode(texto ?? "");
}
