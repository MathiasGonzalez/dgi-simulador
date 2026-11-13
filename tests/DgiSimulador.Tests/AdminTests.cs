using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DgiSimulador.Certificados;
using DgiSimulador.Xml;

namespace DgiSimulador.Tests;

/// <summary>Altas por la API de administración y su especificación OpenAPI.</summary>
public sealed class AdminTests(Simulador sim) : IClassFixture<Simulador>
{
    [Fact]
    public async Task ElAltaConTiposDejaElEmisorListoParaEnviar()
    {
        var rut = Ruts.Nuevo();
        using var http = sim.CreateClient();
        var respuesta = await http.PostAsJsonAsync("/admin/emisores", new { rut, tipos = new[] { 101, 111, 112 } });
        respuesta.EnsureSuccessStatusCode();
        var json = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        var caes = json.GetProperty("caes").EnumerateArray().ToList();
        Assert.Equal([101, 111, 112], caes.Select(c => c.GetProperty("cae").GetProperty("tipoCfe").GetInt32()));
        Assert.All(caes, c => Assert.StartsWith("<?xml", c.GetProperty("constancia").GetString()));

        // Con el certificado y los CAE de la respuesta ya se puede mandar un sobre.
        var emisor = new EmisorPrueba(rut, "Empresa " + rut,
            Autoridad.Recargar(Convert.FromBase64String(json.GetProperty("certificado").GetProperty("pfx").GetString()!)), sim);
        var cae = sim.Almacen.Caes(rut).Single(c => c.Tipo == 101);
        var (_, cfes) = await sim.Enviar(emisor, emisor.Firmar(emisor.Cfe(cae, 1)));
        Assert.Equal("AE", cfes.Buscar("ACKCFE_Det")!.Texto("Estado"));

        // Repetir el alta no autoriza otro CAE para los mismos tipos.
        (await http.PostAsJsonAsync("/admin/emisores", new { rut, tipos = new[] { 101, 111, 112 } })).EnsureSuccessStatusCode();
        Assert.Equal(3, sim.Almacen.Caes(rut).Count);
    }

    [Fact]
    public async Task UnTipoInexistenteNoDaDeAltaElEmisor()
    {
        var rut = Ruts.Nuevo();
        using var http = sim.CreateClient();
        var respuesta = await http.PostAsJsonAsync("/admin/emisores", new { rut, tipos = new[] { 101, 999 } });
        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Contains("999", (await respuesta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Null(sim.Almacen.Emisor(rut));
    }

    [Fact]
    public async Task LaEspecificacionOpenApiDescribeSoloLaAdministracionYLaSalud()
    {
        using var http = sim.CreateClient();
        var json = await http.GetFromJsonAsync<JsonElement>("/openapi/v1.json");
        var rutas = json.GetProperty("paths").EnumerateObject().Select(p => p.Name).ToList();
        Assert.Contains("/admin/emisores", rutas);
        Assert.Contains("/admin/cae", rutas);
        Assert.Contains("/salud", rutas);
        Assert.All(rutas, r => Assert.True(r == "/salud" || r.StartsWith("/admin/", StringComparison.Ordinal), r));
        Assert.Equal(Salud.Version, json.GetProperty("info").GetProperty("version").GetString());
        var esquemas = json.GetProperty("components").GetProperty("schemas");
        Assert.True(esquemas.GetProperty("NuevoEmisor").GetProperty("properties").TryGetProperty("tipos", out _));
        Assert.True(esquemas.TryGetProperty("EmisorCreado", out _));
    }

    [Fact]
    public async Task UnCertificadoPorArchivoSoloSeAdmiteEnLaSemilla()
    {
        using var http = sim.CreateClient();
        var respuesta = await http.PostAsJsonAsync("/admin/certificados", new { archivo = "/etc/ssl/cert.pem" });
        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Contains("solo se admite en la semilla", (await respuesta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task LaSaludRespondeConLaVersionDelProyecto()
    {
        using var http = sim.CreateClient();
        var json = await http.GetFromJsonAsync<JsonElement>("/salud");
        Assert.Equal("ok", json.GetProperty("estado").GetString());
        Assert.Matches(@"^\d+\.\d+\.\d+", json.GetProperty("version").GetString());
        Assert.Equal(Salud.Version, json.GetProperty("version").GetString());
    }
}

/// <summary>La semilla de ejemplo del README carga completa.</summary>
public sealed class SemillaDeEjemploTests(SimuladorConSemillaDeEjemplo sim) : IClassFixture<SimuladorConSemillaDeEjemplo>
{
    [Fact]
    public void CargaLosEmisoresConSusCertificadosYCae()
    {
        var emisor = sim.Almacen.Emisor("211234560019");
        Assert.NotNull(emisor);
        Assert.True(sim.Almacen.Emisor("217654330015")!.Simulado);
        Assert.NotNull(sim.Almacen.PfxDe("211234560019"));
        Assert.Equal([101, 102, 103, 111, 112, 113, 181, 182, 201], sim.Almacen.Caes("211234560019").Select(c => c.Tipo).Order());
    }
}

/// <summary>El simulador con clave de administración.</summary>
public sealed class SimuladorConClave() : Simulador(new() { ["Simulador:ClaveAdmin"] = Clave })
{
    internal const string Clave = "una-clave-de-prueba";
}

/// <summary>Con <c>Simulador:ClaveAdmin</c>, <c>/admin</c> exige la clave; la salud y los servicios SOAP no.</summary>
public sealed class ClaveDeAdministracionTests(SimuladorConClave sim) : IClassFixture<SimuladorConClave>
{
    private async Task<HttpResponseMessage> Pedir(string ruta, string? autorizacion)
    {
        using var http = sim.CreateClient();
        using var pedido = new HttpRequestMessage(HttpMethod.Get, ruta);
        if (autorizacion is not null)
            pedido.Headers.TryAddWithoutValidation("Authorization", autorizacion);
        return await http.SendAsync(pedido);
    }

    [Fact]
    public async Task SinClaveOConOtra_Responde401YPideBasic()
    {
        foreach (var autorizacion in new[] { null, "Bearer otra", "Basic no-es-base64", "Basic " + Convert.ToBase64String("admin:otra"u8) })
        {
            var respuesta = await Pedir("/admin/emisores", autorizacion);
            Assert.Equal(HttpStatusCode.Unauthorized, respuesta.StatusCode);
            Assert.StartsWith("Basic", respuesta.Headers.WwwAuthenticate.ToString());
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await Pedir("/admin", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Pedir("/admin/ca.pem", null)).StatusCode);
    }

    [Fact]
    public async Task ConLaClave_PorBearerOPorBasic()
    {
        Assert.Equal(HttpStatusCode.OK, (await Pedir("/admin/emisores", "Bearer " + SimuladorConClave.Clave)).StatusCode);
        var basic = "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("cualquiera:" + SimuladorConClave.Clave));
        Assert.Equal(HttpStatusCode.OK, (await Pedir("/admin", basic)).StatusCode);
    }

    [Fact]
    public async Task LaSaludElContratoYLosWsdlSiguenAbiertos()
    {
        Assert.Equal(HttpStatusCode.OK, (await Pedir("/salud", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Pedir("/openapi/v1.json", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Pedir("/ePrueba/ws_eprueba?wsdl", null)).StatusCode);
    }
}
