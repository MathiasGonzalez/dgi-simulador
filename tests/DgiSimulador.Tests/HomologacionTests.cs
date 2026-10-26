using System.Net.Http.Json;
using DgiSimulador.Homologacion;
using DgiSimulador.Intercambio;
using DgiSimulador.Recepcion;
using DgiSimulador.Xml;

namespace DgiSimulador.Tests;

/// <summary>Checklist del set de homologación, documentos uruguayos y la página de administración.</summary>
public sealed class HomologacionTests(Simulador sim) : IClassFixture<Simulador>
{
    [Fact]
    public async Task ElAvanceMarcaLoQueElEmisorLogro()
    {
        var emisor = await sim.Emisor();
        var cae = sim.Cae(emisor.Rut, 101);
        var caeNota = sim.Cae(emisor.Rut, 102);
        await sim.Enviar(emisor, emisor.Firmar(emisor.Cfe(cae, 1)), emisor.Firmar(emisor.Cfe(cae, 2) with { Moneda = "USD", TipoCambio = 40m }));
        await sim.Enviar(emisor, emisor.Firmar(emisor.Cfe(caeNota, 1) with { Referencias = [new ReferenciaCfe(101, "A", 1, "Devolución")] }));

        var avance = sim.Servicio<SetDePruebas>().Avance(emisor.Rut);
        bool Cumplido(string id) => avance.Casos.Single(c => c.Caso.Id == id).Cumplido;
        Assert.True(Cumplido("eticket"));
        Assert.True(Cumplido("eticket-nc"));
        Assert.True(Cumplido("moneda-extranjera"));
        Assert.False(Cumplido("efactura"));
        Assert.False(Cumplido("reporte-diario"));
        Assert.True(avance.Obligatorios > avance.Cumplidos);
    }

    [Fact]
    public async Task UnCasoManualSeMarcaPorLaApi()
    {
        var emisor = await sim.Emisor();
        using var http = sim.CreateClient();
        (await http.PostAsJsonAsync($"/admin/homologacion/{emisor.Rut}/manual", new { caso = "representacion-impresa" })).EnsureSuccessStatusCode();
        Assert.True(sim.Servicio<SetDePruebas>().Avance(emisor.Rut).Casos.Single(c => c.Caso.Id == "representacion-impresa").Cumplido);

        var markdown = await http.GetStringAsync($"/admin/homologacion/{emisor.Rut}.md");
        Assert.Contains("| Representación impresa (PDF) revisada por la DGI | cumplido |", markdown);
    }

    [Fact]
    public async Task LaPaginaDeAdministracionMuestraLoRecibido()
    {
        var emisor = await sim.Emisor(nombre: "Comercio <Prueba>");
        var cae = sim.Cae(emisor.Rut, 101);
        await sim.Enviar(emisor, emisor.Firmar(emisor.Cfe(cae, 1)));
        using var http = sim.CreateClient();
        var html = await http.GetStringAsync("/admin");
        Assert.Contains("Comercio &lt;Prueba&gt;", html);
        Assert.Contains("e-Ticket A1", html);
    }

    [Fact]
    public async Task LaConstanciaDeCaeTieneElFormatoDelPortal()
    {
        var emisor = await sim.Emisor();
        using var http = sim.CreateClient();
        var respuesta = await http.PostAsJsonAsync("/admin/cae", new { rut = emisor.Rut, tipo = 111, serie = "B", desde = 1, hasta = 500 });
        respuesta.EnsureSuccessStatusCode();
        var json = await respuesta.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var constancia = XmlUtil.Cargar(json.GetProperty("constancia").GetString()!);
        Assert.Equal("CAE", constancia.DocumentElement!.LocalName);
        var da = constancia.DocumentElement.Hijo("DA")!;
        Assert.Equal(emisor.Rut, da.Texto("RucE"));
        Assert.Equal("111", da.Texto("TCFE"));
        Assert.Equal("500", da.Texto("HNro"));
        Assert.True(Firmas.VerificarEnveloped(constancia.DocumentElement).Valida);
    }

    [Theory]
    [InlineData("214844360018", true)]
    [InlineData("210650500016", true)]
    [InlineData("211003420017", true)]
    [InlineData("219999830019", true)]
    [InlineData("211003420018", false)]
    [InlineData("21100342001", false)]
    public void Rut(string rut, bool valido) => Assert.Equal(valido, Documentos.RutValido(rut));

    [Theory]
    [InlineData("12345672", true)]
    [InlineData("1.234.567-2", true)]
    [InlineData("12345678", false)]
    [InlineData("abc", false)]
    public void Cedula(string ci, bool valida) => Assert.Equal(valida, Documentos.CiValida(ci));
}
