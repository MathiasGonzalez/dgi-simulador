using System.IO.Compression;
using System.Text;
using System.Xml;
using DgiSimulador.Intercambio;
using DgiSimulador.Xml;

namespace DgiSimulador.Tests;

/// <summary>WS_eFactura_Consultas.</summary>
public sealed class ConsultasTests(Simulador sim) : IClassFixture<Simulador>
{
    private Task<(HttpResponseMessage Respuesta, XmlDocument Soap)> Consultar(EmisorPrueba emisor, string operacion, string parametros) =>
        sim.Soap("/ePrueba/ws_consultasPrueba", "WS_eFactura_Consultas", operacion, emisor.Certificado, (doc, op) =>
        {
            var fragmento = doc.CreateDocumentFragment();
            fragmento.InnerXml = parametros;
            op.AppendChild(fragmento);
        });

    private static string Parametros(string elemento, params (string Nombre, string Valor)[] campos) =>
        $"<dgi:{elemento} xmlns:dgi=\"{Ns.Dgi}\">" + string.Concat(campos.Select(c => $"<dgi:{c.Nombre}>{c.Valor}</dgi:{c.Nombre}>")) + $"</dgi:{elemento}>";

    [Fact]
    public async Task EstadoCfe_DevuelveElEstadoYLosParametrosDelSobre()
    {
        var emisor = await sim.Emisor();
        var cae = sim.Cae(emisor.Rut, 101);
        var (acuse, _) = await sim.Enviar(emisor, emisor.Firmar(emisor.Cfe(cae, 4)));

        var (respuesta, soap) = await Consultar(emisor, "EFACCONSULTARESTADOCFE",
            Parametros("Cfeid", ("TipoCFE", "101"), ("Serie", "A"), ("Nro", "4")));
        Assert.True(respuesta.IsSuccessStatusCode, soap.OuterXml);
        var ack = soap.Buscar("Ackconsultaestadocfe")!;
        Assert.Equal("AE", ack.Texto("EstadoCFE"));
        Assert.Equal(acuse.Buscar("IDReceptor")!.InnerText, ack.Texto("IdReceptor"));
        Assert.Equal(acuse.Buscar("Token")!.InnerText, ack.Buscar("Token")!.InnerText);
    }

    [Fact]
    public async Task EstadoCfeQueNoExiste_EsSoapFault()
    {
        var emisor = await sim.Emisor();
        var (respuesta, soap) = await Consultar(emisor, "EFACCONSULTARESTADOCFE", Parametros("Cfeid", ("TipoCFE", "101"), ("Serie", "A"), ("Nro", "77")));
        Assert.False(respuesta.IsSuccessStatusCode);
        Assert.NotNull(soap.Buscar("faultstring"));
    }

    [Fact]
    public async Task Padron_FiltraPorRut()
    {
        var emisor = await sim.Emisor(mail: "facturas@empresa.uy");
        var (_, soap) = await Consultar(emisor, "EFACCONSULTARRUCEMISORES",
            Parametros("Consultarucemisores", ("Filtro_eFacDocNro", emisor.Rut), ("Filtro_eFacDenominacion", ""), ("Filtro_eFacRUCEmisorAudFchHora", "2000-01-01T00:00:00")));
        var item = Assert.Single(soap.BuscarTodos("RucEmisoresMail.RucEmisoresMailItem"));
        Assert.Equal("facturas@empresa.uy", item.Texto("MAIL"));
        Assert.Equal("0000-00-00", item.Texto("FECHA_FIN"));
    }

    [Fact]
    public async Task PadronComprimidoDeProveedores_EsGzipEnBase64ConLaUrl()
    {
        var emisor = await sim.Emisor(url: "https://empresa.uy/intercambio");
        var (_, soap) = await Consultar(emisor, "EFACCONSULTARRUCEMISORESTRANSICIONCOMPRIMIDOPROV",
            Parametros("Consultarucemisores", ("Filtro_eFacDocNro", emisor.Rut), ("Filtro_eFacDenominacion", ""), ("Filtro_eFacRUCEmisorAudFchHora", "2000-01-01T00:00:00Z")));
        var base64 = soap.Buscar("Rucemisorestransicionmailcomprimidoprov")!.InnerText;
        using var gzip = new GZipStream(new MemoryStream(Convert.FromBase64String(base64)), CompressionMode.Decompress);
        var padron = XmlUtil.Cargar(new StreamReader(gzip, Encoding.UTF8).ReadToEnd());
        var item = Assert.Single(padron.BuscarTodos("RucEmisoresTransicionMailProv.RucEmisoresTransicionMailProvItem"));
        Assert.Equal("https://empresa.uy/intercambio", item.Texto("URL_WEBSERVICE"));
    }

    [Fact]
    public async Task CfeRecibidos_ListaLosCfeDeOtrosEmisoresParaElConsultante()
    {
        var receptor = await sim.Emisor();
        var proveedor = await sim.Emisor();
        var cae = sim.Cae(proveedor.Rut, 111);
        var cfe = proveedor.Cfe(cae, 1) with { Receptor = new ReceptorCfe("2", "UY", receptor.Rut, "Receptor S.A.") };
        await sim.Enviar(proveedor, proveedor.Firmar(cfe));

        var (_, soap) = await Consultar(receptor, "EFACCONSULTARCFERECIBIDOS", Parametros("Consultacferecibidos",
            ("RUCEmisor", "0"), ("TipoCFE", "0"), ("Serie", " "), ("Nro", "0"), ("FechaDesde", "2000-01-01T00:00:00"), ("FechaHasta", "2050-12-31T23:59:59")));
        var datos = Assert.Single(soap.BuscarTodos("DatosCFE"));
        Assert.Equal(proveedor.Rut, datos.Texto("RucEmisor"));
        Assert.Equal("AE", datos.Texto("Estado"));
        Assert.Equal("122.00", datos.Texto("MontoTotal"));
    }

    [Fact]
    public async Task EnviosYRespuestaDelReporte()
    {
        var emisor = await sim.Emisor();
        var cae = sim.Cae(emisor.Rut, 101);
        var (acuse, _) = await sim.Enviar(emisor, emisor.Firmar(emisor.Cfe(cae, 1)));
        await sim.EnviarReporte(emisor.Certificado, ReportesPrueba.Armar(emisor, EmisorPrueba.Hoy, 1, [new ZonaPrueba(101, "A", 1, 1, 100m)]));

        var (_, sobres) = await Consultar(emisor, "EFACCONSULTARENVIOSSOBRE", Parametros("Consultaenviossobre",
            ("IdEmisor", "0"), ("IdReceptor", acuse.Buscar("IDReceptor")!.InnerText), ("FechaDesde", "2000-01-01T00:00:00"), ("FechaHasta", "2050-01-01T00:00:00")));
        Assert.Equal("AS", Assert.Single(sobres.BuscarTodos("DatosSobre")).Texto("EstadoSobre"));

        var (_, envios) = await Consultar(emisor, "EFACCONSULTARENVIOSCFE", Parametros("Cfeid", ("TipoCFE", "101"), ("Serie", "A"), ("Nro", "1")));
        Assert.Single(envios.BuscarTodos("DatosSobre"));

        var (_, reportes) = await Consultar(emisor, "EFACCONSULTARENVIOSREPORTE",
            Parametros("Consultaenviosreporte", ("FechaResumen", Hora.Formato(EmisorPrueba.Hoy))));
        var reporte = Assert.Single(reportes.BuscarTodos("DatosReporte"));
        Assert.Equal("AR", reporte.Texto("Estado"));

        var (_, respuesta) = await Consultar(emisor, "EFACCONSULTARRESPUESTAREPORTE",
            Parametros("Consultarespuestareporte", ("IdReceptor", reporte.Texto("IdReceptor")!)));
        var ack = XmlUtil.Cargar(respuesta.Buscar("Ackrepdiario")!.InnerText);
        Assert.Equal("ACKRepDiario", ack.DocumentElement!.LocalName);
    }
}
