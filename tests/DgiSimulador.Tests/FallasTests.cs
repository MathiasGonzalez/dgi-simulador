using System.Net;
using System.Net.Http.Json;
using DgiSimulador.Xml;

namespace DgiSimulador.Tests;

/// <summary>Fallas programadas por la API de administración.</summary>
public sealed class FallasTests(Simulador sim) : IClassFixture<Simulador>
{
    private async Task Programar(object falla)
    {
        using var http = sim.CreateClient();
        (await http.PostAsJsonAsync("/admin/fallas", falla)).EnsureSuccessStatusCode();
    }

    private async Task<(HttpResponseMessage, string)> EnviarSinInterpretar(EmisorPrueba emisor, string sobre)
    {
        var envelope = Firmas.CrearSoapFirmado(doc =>
        {
            var op = doc.CreateElement("dgi", "WS_eFactura.EFACRECEPCIONSOBRE", Ns.Dgi);
            Simulador.DataIn(doc, op, sobre);
            return op;
        }, emisor.Certificado);
        using var http = sim.CreateClient();
        var respuesta = await http.PostAsync("/ePrueba/ws_eprueba", new StringContent(envelope, System.Text.Encoding.UTF8, "text/xml"));
        return (respuesta, await respuesta.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Http503UnaVez_YDespuesResponde()
    {
        var emisor = await sim.Emisor();
        var cae = sim.Cae(emisor.Rut, 101);
        await Programar(new { operacion = "EFACRECEPCIONSOBRE", tipo = "http503" });

        var (primera, _) = await EnviarSinInterpretar(emisor, emisor.Sobre(emisor.Firmar(emisor.Cfe(cae, 1))));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, primera.StatusCode);
        var (segunda, cuerpo) = await EnviarSinInterpretar(emisor, emisor.Sobre(emisor.Firmar(emisor.Cfe(cae, 1))));
        Assert.Equal(HttpStatusCode.OK, segunda.StatusCode);
        Assert.Contains("ACKSobre", cuerpo);
    }

    [Fact]
    public async Task Html502DeUnProxy()
    {
        var emisor = await sim.Emisor();
        await Programar(new { operacion = "EFACRECEPCIONSOBRE", tipo = "html502" });
        var (respuesta, cuerpo) = await EnviarSinInterpretar(emisor, "<x/>");
        Assert.Equal(HttpStatusCode.BadGateway, respuesta.StatusCode);
        Assert.Contains("<html>", cuerpo);
    }

    [Fact]
    public async Task SoapFault()
    {
        var emisor = await sim.Emisor();
        await Programar(new { operacion = "EFACRECEPCIONSOBRE", tipo = "soapfault", detalle = "mantenimiento" });
        var (respuesta, cuerpo) = await EnviarSinInterpretar(emisor, "<x/>");
        Assert.Equal(HttpStatusCode.InternalServerError, respuesta.StatusCode);
        Assert.Contains("<faultstring>mantenimiento</faultstring>", cuerpo);
    }

    [Fact]
    public async Task RechazosProgramados_DelSobreDelCfeYDelReporte()
    {
        var emisor = await sim.Emisor();
        var cae = sim.Cae(emisor.Rut, 101);

        await Programar(new { operacion = "EFACRECEPCIONSOBRE", tipo = "rechazo", codigo = "S03" });
        var acuse = await sim.EnviarSobre(emisor.Certificado, emisor.Sobre(emisor.Firmar(emisor.Cfe(cae, 1))));
        Assert.Equal(["S03"], SobreTests.Codigos(acuse));

        await Programar(new { operacion = "EFACRECEPCIONSOBRE", tipo = "rechazo", codigo = "E14" });
        var (_, cfes) = await sim.Enviar(emisor, emisor.Firmar(emisor.Cfe(cae, 2)));
        Assert.Equal(["E14"], SobreTests.Codigos(cfes, "MotivosRechazoCF"));

        await Programar(new { operacion = "EFACRECEPCIONREPORTE", tipo = "rechazo", codigo = "R04" });
        var reporte = await sim.EnviarReporte(emisor.Certificado, ReportesPrueba.Armar(emisor, EmisorPrueba.Hoy.AddDays(-1), 1, []));
        Assert.Equal(["R04"], SobreTests.Codigos(reporte));
    }

    [Fact]
    public async Task Demora_CorreFechahora()
    {
        var emisor = await sim.Emisor();
        var cae = sim.Cae(emisor.Rut, 101);
        await Programar(new { operacion = "EFACRECEPCIONSOBRE", tipo = "demora", segundos = 600 });
        var acuse = await sim.EnviarSobre(emisor.Certificado, emisor.Sobre(emisor.Firmar(emisor.Cfe(cae, 1))));
        Assert.True(Hora.Leer(acuse.Buscar("Fechahora")!.InnerText) > DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.Empty((await sim.ConsultarEnvio(emisor.Certificado, acuse)).BuscarTodos("ACKCFE_Det"));
    }
}
