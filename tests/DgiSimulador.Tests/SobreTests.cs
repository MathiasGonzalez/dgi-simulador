using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml;
using DgiSimulador.Intercambio;
using DgiSimulador.Xml;

namespace DgiSimulador.Tests;

/// <summary>Recepción de sobres (ACKSobre, motivos S) y consulta del resultado (ACKCFE).</summary>
public sealed class SobreTests(Simulador sim) : IClassFixture<Simulador>
{
    internal static string Estado(XmlNode doc) => doc.Buscar("Detalle")?.Texto("Estado") ?? doc.Buscar("Estado")!.InnerText;

    internal static string[] Codigos(XmlNode nodo, string contenedor = "MotivosRechazo") =>
        [.. nodo.BuscarTodos(contenedor).Select(m => m.Texto("Motivo")!)];

    internal static void ValidaContraXsd(XmlDocument doc, Esquemas.Documento documento = Esquemas.Documento.AcuseDgi) =>
        Assert.Empty(Esquemas.Validar(doc.OuterXml, documento));

    [Fact]
    public async Task SobreValido_SeRecibeYElCfeQuedaAceptado()
    {
        var emisor = await sim.Emisor();
        var cae = sim.Cae(emisor.Rut, 101);
        var (acuse, cfes) = await sim.Enviar(emisor, emisor.Firmar(emisor.Cfe(cae, 1)));

        ValidaContraXsd(acuse);
        Assert.Equal("214844360018", acuse.Buscar("RUCReceptor")!.InnerText);
        Assert.Equal(emisor.Rut, acuse.Buscar("RUCEmisor")!.InnerText);
        Assert.NotNull(acuse.Buscar("Token"));
        Assert.NotNull(acuse.Buscar("Signature"));

        ValidaContraXsd(cfes);
        var detalle = Assert.Single(cfes.BuscarTodos("ACKCFE_Det"));
        Assert.Equal("AE", detalle.Texto("Estado"));
        Assert.Equal("101", detalle.Texto("TipoCFE"));
        Assert.Equal("1", detalle.Texto("NroCFE"));
        Assert.Equal("1", cfes.Buscar("CantCFEAceptados")!.InnerText);
    }

    [Fact]
    public async Task NoEsXml_S01()
    {
        var emisor = await sim.Emisor();
        var acuse = await sim.EnviarSobre(emisor.Certificado, "esto no es XML");
        Assert.Equal("BS", Estado(acuse));
        Assert.Equal(["S01"], Codigos(acuse));
        ValidaContraXsd(acuse);
    }

    [Fact]
    public async Task OtraRaiz_S01()
    {
        var emisor = await sim.Emisor();
        var acuse = await sim.EnviarSobre(emisor.Certificado, "<Reporte xmlns=\"http://cfe.dgi.gub.uy\"/>");
        Assert.Equal(["S01"], Codigos(acuse));
    }

    [Fact]
    public async Task EnvioFirmadoPorOtroRut_S02()
    {
        var emisor = await sim.Emisor();
        var otro = await sim.Emisor();
        var cae = sim.Cae(emisor.Rut, 101);
        var acuse = await sim.EnviarSobre(otro.Certificado, emisor.Sobre(emisor.Firmar(emisor.Cfe(cae, 1))));
        Assert.Equal("BS", Estado(acuse));
        Assert.Contains("S02", Codigos(acuse));
    }

    [Fact]
    public async Task CertificadoQueNoEsDeConfianza_S03()
    {
        var rut = Ruts.Nuevo();
        using var propio = Autofirmado(rut);
        var cae = sim.Cae(rut, 101);
        var emisor = new EmisorPrueba(rut, "Sin CA", propio, sim);
        var acuse = await sim.EnviarSobre(propio, emisor.Sobre(emisor.Firmar(emisor.Cfe(cae, 1))));
        Assert.Equal("BS", Estado(acuse));
        Assert.Contains("S03", Codigos(acuse));
    }

    [Fact]
    public async Task CertificadoImportado_SeAcepta()
    {
        var rut = Ruts.Nuevo();
        using var propio = Autofirmado(rut);
        using var http = sim.CreateClient();
        var importado = await http.PostAsync("/admin/certificados", JsonContent.Create(new { pem = propio.ExportCertificatePem() }));
        importado.EnsureSuccessStatusCode();

        var cae = sim.Cae(rut, 101);
        var emisor = new EmisorPrueba(rut, "Importado", propio, sim);
        var (_, cfes) = await sim.Enviar(emisor, emisor.Firmar(emisor.Cfe(cae, 1)));
        Assert.Equal("AE", cfes.Buscar("ACKCFE_Det")!.Texto("Estado"));
    }

    [Fact]
    public async Task RutReceptorQueNoEsLaDgi_S04()
    {
        var emisor = await sim.Emisor();
        var cae = sim.Cae(emisor.Rut, 101);
        var sobre = emisor.Sobre(emisor.Firmar(emisor.Cfe(cae, 1))).Replace("<RutReceptor>214844360018<", "<RutReceptor>211003420017<");
        var acuse = await sim.EnviarSobre(emisor.Certificado, sobre);
        Assert.Equal(["S04"], Codigos(acuse));
    }

    [Fact]
    public async Task CantidadQueNoCoincide_S05()
    {
        var emisor = await sim.Emisor();
        var cae = sim.Cae(emisor.Rut, 101);
        var sobre = emisor.Sobre(emisor.Firmar(emisor.Cfe(cae, 1))).Replace("<CantCFE>1<", "<CantCFE>2<");
        var acuse = await sim.EnviarSobre(emisor.Certificado, sobre);
        Assert.Equal(["S05"], Codigos(acuse));
    }

    [Fact]
    public async Task CfeFirmadoConOtroCertificado_S06()
    {
        var emisor = await sim.Emisor();
        using var http = sim.CreateClient();
        var nuevo = await http.PostAsync($"/admin/emisores/{emisor.Rut}/certificado", null);
        var json = await nuevo.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        using var segundo = Certificados.Autoridad.Recargar(Convert.FromBase64String(json.GetProperty("pfx").GetString()!));

        var cae = sim.Cae(emisor.Rut, 101);
        var cfe = emisor.Cfe(cae, 1).Firmar(segundo);
        var acuse = await sim.EnviarSobre(emisor.Certificado, emisor.Sobre(cfe));
        Assert.Equal(["S06"], Codigos(acuse));
    }

    [Fact]
    public async Task SobreRepetido_S08()
    {
        var emisor = await sim.Emisor();
        var cae = sim.Cae(emisor.Rut, 101);
        var sobre = emisor.Sobre(emisor.Firmar(emisor.Cfe(cae, 1)));
        Assert.Equal("AS", Estado(await sim.EnviarSobre(emisor.Certificado, sobre)));
        var repetido = await sim.EnviarSobre(emisor.Certificado, sobre);
        Assert.Equal(["S08"], Codigos(repetido));
    }

    [Fact]
    public async Task ConsultaConTokenEquivocado_EsSoapFault()
    {
        var emisor = await sim.Emisor();
        var cae = sim.Cae(emisor.Rut, 101);
        var acuse = await sim.EnviarSobre(emisor.Certificado, emisor.Sobre(emisor.Firmar(emisor.Cfe(cae, 1))));
        var consulta = $"<ConsultaCFE xmlns=\"http://dgi.gub.uy\"><IdReceptor>{acuse.Buscar("IDReceptor")!.InnerText}</IdReceptor><Token>b3Rybw==</Token></ConsultaCFE>";
        var (respuesta, soap) = await sim.Soap("/ePrueba/ws_eprueba", "WS_eFactura", "EFACCONSULTARESTADOENVIO", emisor.Certificado,
            (doc, op) => Simulador.DataIn(doc, op, consulta));
        Assert.Equal(System.Net.HttpStatusCode.InternalServerError, respuesta.StatusCode);
        Assert.NotNull(soap.Buscar("faultstring"));
    }

    [Fact]
    public async Task SinWsSecurity_EsSoapFault()
    {
        var (respuesta, soap) = await sim.Post("/ePrueba/ws_eprueba",
            "<soapenv:Envelope xmlns:soapenv=\"http://schemas.xmlsoap.org/soap/envelope/\"><soapenv:Body>" +
            "<WS_eFactura.EFACRECEPCIONSOBRE xmlns=\"http://dgi.gub.uy\"><Datain><xmlData>x</xmlData></Datain></WS_eFactura.EFACRECEPCIONSOBRE>" +
            "</soapenv:Body></soapenv:Envelope>");
        Assert.Equal(System.Net.HttpStatusCode.InternalServerError, respuesta.StatusCode);
        Assert.Contains("WS-Security", soap.Buscar("faultstring")!.InnerText);
    }

    [Fact]
    public async Task BodyModificadoDespuesDeFirmar_EsSoapFault()
    {
        var emisor = await sim.Emisor();
        var envelope = Firmas.CrearSoapFirmado(doc =>
        {
            var op = doc.CreateElement("dgi", "WS_eFactura.EFACRECEPCIONSOBRE", Ns.Dgi);
            Simulador.DataIn(doc, op, "original");
            return op;
        }, emisor.Certificado).Replace("original", "cambiado");
        var (respuesta, soap) = await sim.Post("/ePrueba/ws_eprueba", envelope);
        Assert.Equal(System.Net.HttpStatusCode.InternalServerError, respuesta.StatusCode);
        Assert.Contains("WS-Security", soap.Buscar("faultstring")!.InnerText);
    }

    [Fact]
    public async Task Wsdl_TieneLaDireccionDelSimulador()
    {
        using var http = sim.CreateClient();
        var wsdl = await http.GetStringAsync("/ePrueba/ws_eprueba?wsdl");
        Assert.Contains("location=\"http://localhost/ePrueba/ws_eprueba\"", wsdl);
        Assert.DoesNotContain("efactura.dgi.gub.uy", wsdl);
        var xsd = await http.GetStringAsync("/ePrueba/ws_eprueba.xsd1.xsd");
        Assert.Contains("WS_eFactura.EFACRECEPCIONSOBRE", xsd);
    }

    internal static X509Certificate2 Autofirmado(string rut)
    {
        using var rsa = RSA.Create(2048);
        var pedido = new CertificateRequest($"CN=Propio, SERIALNUMBER=RUC{rut}, C=UY", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificado = pedido.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return Certificados.Autoridad.Recargar(certificado.Export(X509ContentType.Pfx));
    }
}

/// <summary>Un sobre más grande que el máximo configurado.</summary>
public sealed class SobreGrandeTests(SimuladorChico sim) : IClassFixture<SimuladorChico>
{
    [Fact]
    public async Task SuperaElTamanoMaximo_S07()
    {
        var emisor = await sim.Emisor();
        var cae = sim.Cae(emisor.Rut, 101);
        var lineas = Enumerable.Range(1, 30).Select(i => new LineaCfe($"Producto {i}", 1, 10m)).ToList();
        var acuse = await sim.EnviarSobre(emisor.Certificado, emisor.Sobre(emisor.Firmar(emisor.Cfe(cae, 1) with { Lineas = lineas })));
        Assert.Equal(["S07"], SobreTests.Codigos(acuse));
    }
}

/// <summary>El resultado recién se puede consultar desde Fechahora.</summary>
public sealed class DemoraTests(SimuladorConDemora sim) : IClassFixture<SimuladorConDemora>
{
    [Fact]
    public async Task AntesDeFechahora_LaRespuestaNoTraeDetalle()
    {
        var emisor = await sim.Emisor();
        var cae = sim.Cae(emisor.Rut, 101);
        var acuse = await sim.EnviarSobre(emisor.Certificado, emisor.Sobre(emisor.Firmar(emisor.Cfe(cae, 1))));
        var consultarDesde = Hora.Leer(acuse.Buscar("Fechahora")!.InnerText)!.Value;
        Assert.True(consultarDesde > DateTimeOffset.UtcNow.AddMinutes(30));

        var cfes = await sim.ConsultarEnvio(emisor.Certificado, acuse);
        Assert.Empty(cfes.BuscarTodos("ACKCFE_Det"));
    }
}
