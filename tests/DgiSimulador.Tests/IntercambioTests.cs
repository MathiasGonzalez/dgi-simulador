using System.Net.Http.Json;
using System.Text.Json;
using System.Xml;
using DgiSimulador.Intercambio;
using DgiSimulador.Recepcion;
using DgiSimulador.Xml;

namespace DgiSimulador.Tests;

/// <summary>Intercambio entre empresas con las empresas simuladas: el producto como emisor y como receptor.</summary>
public sealed class IntercambioTests(Simulador sim) : IClassFixture<Simulador>
{
    /// <summary>El producto (emisor) arma un sobre entre empresas con una e-Factura para la empresa simulada.</summary>
    private async Task<(EmisorPrueba Producto, EmisorPrueba Simulada, string Sobre)> SobreParaSimulada(string? rutReceptorCfe = null, long nro = 1)
    {
        var producto = await sim.Emisor();
        var simulada = await sim.Emisor(nombre: "Proveedor simulado S.A.", simulado: true);
        var cae = sim.Cae(producto.Rut, 111);
        var cfe = producto.Firmar(producto.Cfe(cae, nro, new ReceptorCfe("2", "UY", rutReceptorCfe ?? simulada.Rut, simulada.Nombre)));
        var sobre = Sobres.EntreEmpresas(simulada.Rut, producto.Rut, nro, DateTimeOffset.UtcNow, producto.Certificado, [(cfe, "OC 123")]);
        Assert.Empty(Esquemas.Validar(sobre, Esquemas.Documento.SobreEntreEmpresas));
        return (producto, simulada, sobre);
    }

    private async Task<JsonElement> PorCorreo(string rut, string nombre, string xml)
    {
        using var http = sim.CreateClient();
        var respuesta = await http.PostAsync($"/admin/intercambio/{rut}/correo?nombre={nombre}", new StringContent(xml));
        Assert.True(respuesta.IsSuccessStatusCode, await respuesta.Content.ReadAsStringAsync());
        return await respuesta.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static XmlDocument Archivo(JsonElement json, string prefijo)
    {
        var archivo = json.GetProperty("respuestas").EnumerateArray().Single(a => a.GetProperty("nombre").GetString()!.StartsWith(prefijo + "_"));
        return XmlUtil.Cargar(archivo.GetProperty("xml").GetString()!);
    }

    [Fact]
    public async Task SobrePorServicioWeb_SeRecibeConCodigo1000()
    {
        var (producto, simulada, sobre) = await SobreParaSimulada();
        var respuestas = await sim.ConXmlData($"/intercambio/{simulada.Rut}", "WS_eFactura_Intercambio", "SOBRE", producto.Certificado, sobre);
        Assert.Equal("1000", respuestas.Buscar("Codigo")!.InnerText);

        var recibido = sim.Almacen.Intercambios(simulada.Rut).Single(m => m.Tipo == "SOBRE");
        Assert.Equal("AS", recibido.Estado);
        var acuse = XmlUtil.Cargar(recibido.Respuesta!);
        SobreTests.ValidaContraXsd(acuse);
        Assert.Equal(simulada.Rut, acuse.Buscar("RUCReceptor")!.InnerText);
    }

    [Fact]
    public async Task SobrePorCorreo_RespondeLosArchivosMyME()
    {
        var (producto, simulada, sobre) = await SobreParaSimulada();
        var json = await PorCorreo(simulada.Rut, $"Sob_{producto.Rut}_20261026_1.xml", sobre);

        var acuse = Archivo(json, "M");
        Assert.Equal("AS", SobreTests.Estado(acuse));
        SobreTests.ValidaContraXsd(acuse);
        var cfes = Archivo(json, "ME");
        SobreTests.ValidaContraXsd(cfes, Esquemas.Documento.AcuseCfePartes);
        Assert.Equal("AE", cfes.Buscar("ACKCFE_Det")!.Texto("Estado"));
        Assert.Equal(simulada.Rut, Autoridad(cfes));
    }

    [Fact]
    public async Task CfeDirigidoAOtroReceptor_BE()
    {
        var (_, simulada, sobre) = await SobreParaSimulada(rutReceptorCfe: "211003420017");
        var cfes = Archivo(await PorCorreo(simulada.Rut, "Sob_x.xml", sobre), "ME");
        Assert.Equal("BE", cfes.Buscar("ACKCFE_Det")!.Texto("Estado"));
        Assert.Contains("E05", SobreTests.Codigos(cfes, "MotivosRechazoCF"));
    }

    [Fact]
    public async Task SobreRepetido_S20()
    {
        var (_, simulada, sobre) = await SobreParaSimulada();
        await PorCorreo(simulada.Rut, "Sob_a.xml", sobre);
        var repetido = Archivo(await PorCorreo(simulada.Rut, "Sob_a.xml", sobre), "M");
        Assert.Equal(["S20"], SobreTests.Codigos(repetido));
    }

    [Fact]
    public async Task RechazoComercialProgramado_E21()
    {
        var (_, simulada, sobre) = await SobreParaSimulada();
        using var http = sim.CreateClient();
        (await http.PostAsJsonAsync("/admin/fallas", new { operacion = "SOBRE", tipo = "rechazo", codigo = "E21" })).EnsureSuccessStatusCode();
        var cfes = Archivo(await PorCorreo(simulada.Rut, "Sob_b.xml", sobre), "ME");
        Assert.Equal(["E21"], SobreTests.Codigos(cfes, "MotivosRechazoCF"));
        Assert.Equal("Mercadería en mal estado", cfes.Buscar("Glosa")!.InnerText);
    }

    [Fact]
    public async Task EmpresaSimuladaEmite_ElSobreEsValidoYFiguraEnLosCfeRecibidos()
    {
        var producto = await sim.Emisor();
        var simulada = await sim.Emisor(nombre: "Proveedor", simulado: true);
        using var http = sim.CreateClient();
        var respuesta = await http.PostAsJsonAsync("/admin/intercambio/emitir", new { rutEmisor = simulada.Rut, rutReceptor = producto.Rut });
        Assert.True(respuesta.IsSuccessStatusCode, await respuesta.Content.ReadAsStringAsync());
        var emision = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("disponible", emision.GetProperty("estado").GetString());
        Assert.Empty(Esquemas.Validar(emision.GetProperty("xml").GetString()!, Esquemas.Documento.SobreEntreEmpresas));

        var recibido = Assert.Single(sim.Almacen.CfesParaReceptor(producto.Rut));
        Assert.Equal("AE", recibido.Estado);
        Assert.Equal(simulada.Rut, recibido.Rut);
    }

    [Fact]
    public async Task VarianteConFirmaRota_LaDgiLaRechaza()
    {
        var producto = await sim.Emisor();
        var simulada = await sim.Emisor(simulado: true);
        using var http = sim.CreateClient();
        (await http.PostAsJsonAsync("/admin/intercambio/emitir", new { rutEmisor = simulada.Rut, rutReceptor = producto.Rut, variante = "firma" }))
            .EnsureSuccessStatusCode();
        var recibido = Assert.Single(sim.Almacen.CfesParaReceptor(producto.Rut));
        Assert.Equal("BE", recibido.Estado);
        Assert.Contains(recibido.Motivos, m => m.Codigo == "E04");
    }

    [Fact]
    public async Task ProductoRespondeComoReceptor_PorServicioWeb()
    {
        var producto = await sim.Emisor();
        var simulada = await sim.Emisor(simulado: true);
        // El producto acusa un sobre de la empresa simulada con su propio certificado.
        var caratula = new Caratula(producto.Rut, simulada.Rut, 1, "Sob_x_20261026_1.xml", DateTimeOffset.UtcNow, 1, 1, DateTimeOffset.UtcNow);
        var acuse = Acuses.Sobre(caratula, 1, "AS", [], null, null, producto.Certificado);
        var cfes = Acuses.Cfes(caratula, 1, [new DetalleCfe(1, 111, "A", 1, EmisorPrueba.Hoy, DateTimeOffset.UtcNow, "AE", [])], deLaDgi: false, producto.Certificado);

        var r1 = await sim.ConXmlData($"/intercambio/{simulada.Rut}", "WS_eFactura_Intercambio", "RESPUESTASOBRE", producto.Certificado, acuse);
        Assert.Equal("2000", r1.Buscar("Codigo")!.InnerText);
        Assert.Equal("ACK Sobre Recibido.", r1.Buscar("Descripcion")!.InnerText);
        var r2 = await sim.ConXmlData($"/intercambio/{simulada.Rut}", "WS_eFactura_Intercambio", "RESPUESTACFES", producto.Certificado, cfes);
        Assert.Equal("3000", r2.Buscar("Codigo")!.InnerText);
        Assert.Equal("ACKCFE Recibido.", r2.Buscar("Descripcion")!.InnerText);

        var avance = sim.Servicio<Homologacion.SetDePruebas>().Avance(producto.Rut);
        Assert.True(avance.Casos.Single(c => c.Caso.Id == "intercambio-receptor").Cumplido);
    }

    [Fact]
    public async Task RespuestaConFirmaDeOtroRut_EsInvalida()
    {
        var producto = await sim.Emisor();
        var otro = await sim.Emisor();
        var simulada = await sim.Emisor(simulado: true);
        var caratula = new Caratula(producto.Rut, simulada.Rut, 1, "Sob.xml", DateTimeOffset.UtcNow, 1, 1, DateTimeOffset.UtcNow);
        var acuse = Acuses.Sobre(caratula, 1, "AS", [], null, null, otro.Certificado);
        var respuesta = await sim.ConXmlData($"/intercambio/{simulada.Rut}", "WS_eFactura_Intercambio", "RESPUESTASOBRE", producto.Certificado, acuse);
        Assert.Equal("2001", respuesta.Buscar("Codigo")!.InnerText);
        Assert.Equal("ACK Sobre Rechazado.", respuesta.Buscar("Descripcion")!.InnerText);
    }

    private static string? Autoridad(XmlDocument firmado) =>
        Certificados.Autoridad.RutDe(Firmas.VerificarEnveloped(firmado.DocumentElement!).Certificado!);
}
