using System.Security.Cryptography.X509Certificates;
using System.Text;
using DgiSimulador.Xml;

namespace DgiSimulador.Intercambio;

/// <summary>Resultado de entregar un mensaje del intercambio: si el destino lo recibió y lo que respondió.</summary>
internal sealed record Entrega(bool Recibido, string Detalle);

/// <summary>
/// Llamadas de las empresas simuladas al servicio web de intercambio de un emisor (<c>ws_efactura_intercambio</c>),
/// firmadas con WS-Security, y entrega por correo (un MIME a <see cref="OpcionesSimulador.UrlEntregaCorreo"/>).
/// </summary>
internal sealed class ClienteIntercambio(IHttpClientFactory http)
{
    /// <summary>Invoca la operación (<c>SOBRE</c>, <c>RESPUESTASOBRE</c>, <c>RESPUESTACFES</c> o <c>NOTIFICACIONANULACIONCFE</c>).</summary>
    internal async Task<Entrega> Enviar(string url, string operacion, string xml, X509Certificate2 certificado, CancellationToken cancelacion = default)
    {
        var envelope = Firmas.CrearSoapFirmado(doc =>
        {
            var op = doc.CreateElement("dgi", "WS_eFactura_Intercambio." + operacion, Ns.Dgi);
            var datain = doc.CreateElement("dgi", "Datain", Ns.Dgi);
            var xmlData = doc.CreateElement("dgi", "xmlData", Ns.Dgi);
            xmlData.AppendChild(doc.CreateCDataSection(xml));
            datain.AppendChild(xmlData);
            op.AppendChild(datain);
            return op;
        }, certificado);

        using var pedido = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(envelope, Encoding.UTF8, "text/xml") };
        pedido.Headers.TryAddWithoutValidation("SOAPAction", $"\"http://dgi.gub.uyaction/AWS_EFACTURA_INTERCAMBIO.{operacion}\"");
        try
        {
            using var cliente = http.CreateClient("intercambio");
            using var respuesta = await cliente.SendAsync(pedido, cancelacion);
            var cuerpo = await respuesta.Content.ReadAsStringAsync(cancelacion);
            if (!respuesta.IsSuccessStatusCode)
                return new(false, $"HTTP {(int)respuesta.StatusCode}: {XmlUtil.Recortar(cuerpo, 300)}");
            return LeerRespuesta(cuerpo);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new(false, $"No se pudo invocar {url}: {ex.Message}");
        }
    }

    /// <summary>La respuesta del servicio de intercambio: <c>Respuestas/Respuesta</c> con código x000 (recibido), x001 o x002.</summary>
    private static Entrega LeerRespuesta(string soap)
    {
        if (!XmlUtil.TryCargar(soap, out var doc, out var error))
            return new(false, $"La respuesta no es XML: {error}");
        var data = doc.DocumentElement.Buscar("xmlData")?.InnerText;
        if (data is null || !XmlUtil.TryCargar(data, out var respuestas, out _))
            return new(false, "La respuesta no trae Dataout/xmlData con Respuestas.");
        var respuesta = respuestas.DocumentElement.Buscar("Respuesta");
        string? Campo(string nombre) => respuesta?.Hijos().FirstOrDefault(e => string.Equals(e.LocalName, nombre, StringComparison.OrdinalIgnoreCase))?.InnerText.Trim();
        var codigo = Campo("codigo");
        return new(codigo is ['1' or '2' or '3' or '4', '0', '0', '0'], $"{codigo} {Campo("descripcion")} {Campo("detalle")}".Trim());
    }

    /// <summary>Un mensaje de correo con el XML adjunto, en formato MIME.</summary>
    internal static string Mime(string de, string para, string asunto, string nombreAdjunto, string xml)
    {
        var limite = "simulador-" + Guid.NewGuid().ToString("N");
        var adjunto = Convert.ToBase64String(Encoding.UTF8.GetBytes(xml), Base64FormattingOptions.InsertLineBreaks);
        return $"""
            From: {de}
            To: {para}
            Subject: {asunto}
            Message-ID: <{Guid.NewGuid():N}@dgi-simulador>
            Date: {DateTimeOffset.UtcNow:R}
            MIME-Version: 1.0
            Content-Type: multipart/mixed; boundary="{limite}"

            --{limite}
            Content-Type: text/plain; charset=utf-8

            {asunto}
            --{limite}
            Content-Type: text/xml; name="{nombreAdjunto}"
            Content-Disposition: attachment; filename="{nombreAdjunto}"
            Content-Transfer-Encoding: base64

            {adjunto}
            --{limite}--

            """.ReplaceLineEndings("\r\n");
    }

    /// <summary>Entrega un correo a la URL configurada (<c>?from=…&amp;to=…</c> con el MIME en el cuerpo, como el handler de wrangler).</summary>
    internal async Task<Entrega> EntregarCorreo(string url, string de, string para, string mime, CancellationToken cancelacion = default)
    {
        var destino = $"{url}{(url.Contains('?') ? '&' : '?')}from={Uri.EscapeDataString(de)}&to={Uri.EscapeDataString(para)}";
        try
        {
            using var cliente = http.CreateClient("intercambio");
            using var respuesta = await cliente.PostAsync(destino, new StringContent(mime, Encoding.UTF8, "message/rfc822"), cancelacion);
            return new(respuesta.IsSuccessStatusCode, $"HTTP {(int)respuesta.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new(false, $"No se pudo entregar el correo en {url}: {ex.Message}");
        }
    }
}
