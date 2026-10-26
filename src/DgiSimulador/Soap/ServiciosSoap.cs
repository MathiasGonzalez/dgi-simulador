using System.Text;
using DgiSimulador.Consultas;
using DgiSimulador.Intercambio;
using DgiSimulador.Recepcion;
using DgiSimulador.Xml;
using Microsoft.Extensions.Options;

namespace DgiSimulador.Soap;

/// <summary>
/// Los servicios SOAP con las rutas de la DGI (prueba y producción): <c>ws_eprueba</c>/<c>ws_efactura</c>,
/// <c>ws_consultasPrueba</c>/<c>ws_consultas</c>, sus WSDL (<c>?wsdl</c>) y el servicio de intercambio de las
/// empresas simuladas (<c>/intercambio/{rut}</c>).
/// </summary>
internal static class ServiciosSoap
{
    private const string UrlEnvioDgi = "https://efactura.dgi.gub.uy:6443/ePrueba/ws_eprueba";
    private const string UrlConsultasDgi = "https://efactura.dgi.gub.uy:6460/ePrueba/ws_consultasPrueba";

    internal static void Mapear(WebApplication app)
    {
        foreach (var ruta in new[] { "/ePrueba/ws_eprueba", "/efactura/ws_efactura" })
        {
            app.MapPost(ruta, Efactura);
            app.MapGet(ruta, (HttpContext c, IOptions<OpcionesSimulador> o) => Wsdl(c, "ws_eprueba", UrlEnvioDgi, o.Value));
        }
        foreach (var ruta in new[] { "/ePrueba/ws_consultasPrueba", "/efactura/ws_consultas" })
        {
            app.MapPost(ruta, Consultas);
            app.MapGet(ruta, (HttpContext c, IOptions<OpcionesSimulador> o) => Wsdl(c, "ws_consultasPrueba", UrlConsultasDgi, o.Value));
        }
        app.MapGet("/ePrueba/{archivo}", Xsd);
        app.MapGet("/efactura/{archivo}", Xsd);
        app.MapPost("/intercambio/{rut}", Intercambio);
    }

    private static Task<IResult> Efactura(HttpContext contexto, RecepcionSobre sobres, RecepcionReporte reportes, Fallas fallas) =>
        Atender(contexto, fallas, "WS_eFactura", p => p.Operacion switch
        {
            "EFACRECEPCIONSOBRE" => MensajeSoap.ConXmlData(p, sobres.Recibir(p.XmlData, p.Firma)),
            "EFACCONSULTARESTADOENVIO" => MensajeSoap.ConXmlData(p, sobres.ConsultarEnvio(p.XmlData, p.Firma)),
            "EFACRECEPCIONREPORTE" => MensajeSoap.ConXmlData(p, reportes.Recibir(p.XmlData, p.Firma)),
            _ => throw new FaultSoap($"WS_eFactura no tiene la operación {p.Operacion}."),
        });

    private static Task<IResult> Consultas(HttpContext contexto, ServicioConsultas consultas, Fallas fallas) =>
        Atender(contexto, fallas, "WS_eFactura_Consultas", consultas.Responder);

    private static Task<IResult> Intercambio(HttpContext contexto, string rut, ReceptorSimulado receptor, Fallas fallas) =>
        Atender(contexto, fallas, "WS_eFactura_Intercambio", p => MensajeSoap.ConXmlData(p, receptor.Recibir(rut, p)));

    private static async Task<IResult> Atender(HttpContext contexto, Fallas fallas, string servicio, Func<PeticionSoap, string> responder)
    {
        string cuerpo;
        using (var lector = new StreamReader(contexto.Request.Body, Encoding.UTF8))
            cuerpo = await lector.ReadToEndAsync(contexto.RequestAborted);
        try
        {
            var pedido = MensajeSoap.Leer(cuerpo);
            if (pedido.Servicio != servicio)
                throw new FaultSoap($"Esta URL es de {servicio} y el pedido es para {pedido.Servicio}.");
            if (await fallas.Transporte(pedido.Operacion, contexto.RequestAborted) is { } falla)
                return falla;
            return Xml(responder(pedido), StatusCodes.Status200OK);
        }
        catch (FaultSoap f)
        {
            return Xml(MensajeSoap.Fault(f.Message, f.Codigo), StatusCodes.Status500InternalServerError);
        }
    }

    private static IResult Xml(string xml, int estado) => Results.Content(xml, "text/xml; charset=utf-8", Encoding.UTF8, estado);

    /// <summary>El WSDL oficial con la dirección del simulador en <c>soap:address</c>.</summary>
    private static IResult Wsdl(HttpContext contexto, string nombre, string urlDgi, OpcionesSimulador opciones)
    {
        var pideWsdl = contexto.Request.QueryString.Value?.Contains("wsdl", StringComparison.OrdinalIgnoreCase) == true;
        if (!pideWsdl)
            return Results.Text($"Simulador de los servicios de la DGI. El WSDL está en {contexto.Request.Path}?wsdl.");
        var baseUrl = (opciones.UrlPublica ?? $"{contexto.Request.Scheme}://{contexto.Request.Host}").TrimEnd('/');
        var wsdl = Esquemas.Leer($"Wsdl/{nombre}.wsdl").Replace(urlDgi, baseUrl + contexto.Request.Path, StringComparison.Ordinal);
        return Xml(wsdl, StatusCodes.Status200OK);
    }

    /// <summary>Los XSD que importan los WSDL (<c>ws_eprueba.xsd1.xsd</c>, <c>ws_consultasPrueba.xsd1.xsd</c>).</summary>
    private static IResult Xsd(string archivo) =>
        archivo.EndsWith(".xsd", StringComparison.OrdinalIgnoreCase) && Esquemas.Existe($"Esquemas/{archivo}")
            ? Xml(Esquemas.Leer($"Esquemas/{archivo}"), StatusCodes.Status200OK)
            : Results.NotFound();
}
