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
    public async Task LaEspecificacionOpenApiDescribeSoloLaAdministracion()
    {
        using var http = sim.CreateClient();
        var json = await http.GetFromJsonAsync<JsonElement>("/openapi/v1.json");
        var rutas = json.GetProperty("paths").EnumerateObject().Select(p => p.Name).ToList();
        Assert.Contains("/admin/emisores", rutas);
        Assert.Contains("/admin/cae", rutas);
        Assert.All(rutas, r => Assert.StartsWith("/admin/", r));
        var esquemas = json.GetProperty("components").GetProperty("schemas");
        Assert.True(esquemas.GetProperty("NuevoEmisor").GetProperty("properties").TryGetProperty("tipos", out _));
        Assert.True(esquemas.TryGetProperty("EmisorCreado", out _));
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
