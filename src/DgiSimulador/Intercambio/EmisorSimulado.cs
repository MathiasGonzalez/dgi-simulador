using System.Globalization;
using DgiSimulador.Certificados;
using DgiSimulador.Estado;
using DgiSimulador.Recepcion;
using DgiSimulador.Xml;
using Microsoft.Extensions.Options;

namespace DgiSimulador.Intercambio;

/// <summary>Un sobre que emitió una empresa simulada hacia el producto.</summary>
public sealed record Emision(long Id, string Nombre, string Variante, string Canal, string Estado, string? Entrega, string Xml);

/// <summary>
/// Una empresa simulada en el rol de emisor: genera una e-Factura para el producto, la manda a "la DGI" (queda en
/// los CFE recibidos del producto) y le entrega el sobre <c>EnvioCFE_entreEmpresas</c> por servicio web, por correo o
/// para descargar. Las variantes generan los errores que el producto tiene que detectar como receptor.
/// </summary>
internal sealed class EmisorSimulado(
    Almacen almacen, Autoridad autoridad, RecepcionSobre recepcion, ClienteIntercambio cliente, IOptions<OpcionesSimulador> opciones, TimeProvider reloj)
{
    internal static readonly IReadOnlyDictionary<string, string> Variantes = new Dictionary<string, string>
    {
        ["valido"] = "e-Factura correcta, con adenda.",
        ["firma"] = "Se modifica después de firmar: la firma no es válida (E04).",
        ["cae-vencido"] = "Se firma con un CAE vencido (E07).",
        ["fuera-de-rango"] = "El número está fuera del rango del CAE (E03).",
        ["otro-receptor"] = "Está dirigida a otro RUT (E05).",
        ["totales"] = "MntTotal no coincide con los ítems (E05).",
    };

    internal static readonly string[] Canales = ["ws", "correo", "descarga"];

    internal async Task<Emision> Emitir(string rutEmisor, string rutReceptor, string variante, string canal, string? url, CancellationToken cancelacion)
    {
        if (!Variantes.ContainsKey(variante))
            throw new ArgumentException($"Variante desconocida: {variante}. Opciones: {string.Join(", ", Variantes.Keys)}.");
        if (!Canales.Contains(canal))
            throw new ArgumentException($"Canal desconocido: {canal}. Opciones: {string.Join(", ", Canales)}.");
        var empresa = almacen.Emisor(rutEmisor) is { Simulado: true } e
            ? e
            : throw new ArgumentException($"El RUT {rutEmisor} no es una empresa simulada.");
        var receptor = almacen.Emisor(rutReceptor);

        var ahora = reloj.GetUtcNow();
        var hoy = Hora.Fecha(ahora);
        var cae = CaeDe(empresa.Rut, vencido: variante == "cae-vencido", hoy);
        var nro = variante == "fuera-de-rango" ? cae.Hasta + 1 : almacen.Siguiente($"nro-{empresa.Rut}-{cae.Id}");
        var certificado = autoridad.CertificadoDe(empresa.Rut, empresa.Denominacion);
        var generador = new GeneradorCfe
        {
            Tipo = 111,
            Serie = cae.Serie,
            Nro = nro,
            Fecha = hoy,
            Firma = ahora,
            RutEmisor = empresa.Rut,
            RazonSocialEmisor = empresa.Denominacion,
            Receptor = new ReceptorCfe("2", "UY", variante == "otro-receptor" ? "211003420017" : rutReceptor, receptor?.Denominacion ?? "Receptor"),
            Lineas = [new LineaCfe("Servicio de prueba", 2, 150m)],
            Cae = cae,
            Modificar = variante == "totales"
                ? doc => doc.DocumentElement.Buscar("MntTotal")!.InnerText = "1.00"
                : null,
        };
        var cfe = generador.Firmar(certificado);
        if (variante == "firma")
            cfe = cfe.Replace("Servicio de prueba", "Servicio modificado", StringComparison.Ordinal);

        // La empresa lo manda a la DGI: así figura en los CFE recibidos del producto.
        var sobreDgi = Sobres.ParaDgi(empresa.Rut, almacen.Siguiente("idemisor-" + empresa.Rut), ahora, certificado, [cfe]);
        recepcion.Recibir(sobreDgi, new Firma(certificado, null));

        var idEmisor = almacen.Siguiente("idemisor-intercambio-" + empresa.Rut);
        var sobre = Sobres.EntreEmpresas(rutReceptor, empresa.Rut, idEmisor, ahora, certificado, [(cfe, $"Orden de compra OC-{nro}")]);
        var nombre = $"Sob_{empresa.Rut}_{Hora.Uruguay(ahora):yyyyMMdd}_{idEmisor}.xml";
        var id = almacen.GuardarIntercambio(new MensajeIntercambio(0, "enviado", canal, "SOBRE", empresa.Rut, rutReceptor, idEmisor, nombre, sobre,
            null, "pendiente", ahora));

        string estado;
        string? detalle;
        switch (canal)
        {
            case "ws" when (url ?? receptor?.UrlServicio) is { Length: > 0 } destino:
                var entrega = await cliente.Enviar(destino, "SOBRE", sobre, certificado, cancelacion);
                (estado, detalle) = (entrega.Recibido ? "entregado" : "error", entrega.Detalle);
                break;
            case "correo" when opciones.Value.UrlEntregaCorreo is { Length: > 0 } urlCorreo:
                var de = empresa.Mail ?? $"{empresa.Rut}@simulador.dgi";
                var para = receptor?.Mail ?? $"{rutReceptor}@receptor.uy";
                var correo = await cliente.EntregarCorreo(urlCorreo, de, para, ClienteIntercambio.Mime(de, para, nombre, nombre, sobre), cancelacion);
                (estado, detalle) = (correo.Recibido ? "entregado" : "error", correo.Detalle);
                break;
            case "ws":
                (estado, detalle) = ("error", $"El RUT {rutReceptor} no tiene URL de servicio web en el padrón y no se indicó 'url'.");
                break;
            case "correo":
                (estado, detalle) = ("error", "Falta configurar Simulador:UrlEntregaCorreo.");
                break;
            default:
                (estado, detalle) = ("disponible", "Descargar con GET /admin/intercambio/{id}/xml.".Replace("{id}", id.ToString(CultureInfo.InvariantCulture)));
                break;
        }
        almacen.ActualizarIntercambio(id, estado, detalle);
        return new Emision(id, nombre, variante, canal, estado, detalle, sobre);
    }

    /// <summary>El CAE de e-Factura de la empresa simulada; lo crea si no tiene (uno vencido para la variante cae-vencido).</summary>
    private Cae CaeDe(string rut, bool vencido, DateOnly hoy)
    {
        var existente = almacen.Caes(rut).FirstOrDefault(c => c.Tipo == 111 && (vencido ? c.Vencimiento < hoy : c.Vencimiento >= hoy));
        if (existente is not null)
            return existente;
        var cae = new Cae(90_000_000_000 + almacen.Siguiente("cae"), rut, 111, vencido ? "Z" : "A", 1, 100_000,
            vencido ? hoy.AddYears(-2) : hoy.AddDays(-1), vencido ? hoy.AddDays(-1) : hoy.AddYears(1), null, null);
        almacen.GuardarCae(cae);
        return cae;
    }
}
