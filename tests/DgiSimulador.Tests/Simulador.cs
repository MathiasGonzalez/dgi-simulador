using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Xml;
using DgiSimulador.Admin;
using DgiSimulador.Certificados;
using DgiSimulador.Estado;
using DgiSimulador.Intercambio;
using DgiSimulador.Recepcion;
using DgiSimulador.Xml;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace DgiSimulador.Tests;

/// <summary>El simulador en memoria (TestServer) con una base SQLite temporal propia.</summary>
public class Simulador : WebApplicationFactory<Program>
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), $"dgi-simulador-{Guid.NewGuid():N}.db");
    private readonly Dictionary<string, string?> _configuracion;

    public Simulador() : this([])
    {
    }

    protected Simulador(Dictionary<string, string?> configuracion)
    {
        _configuracion = new Dictionary<string, string?>
        {
            ["Simulador:BaseDeDatos"] = $"Data Source={_base}",
            ["Simulador:DemoraConsultaSegundos"] = "0",
        };
        foreach (var (clave, valor) in configuracion)
            _configuracion[clave] = valor;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        foreach (var (clave, valor) in _configuracion)
            builder.UseSetting(clave, valor);
    }

    internal T Servicio<T>() where T : notnull => Services.GetRequiredService<T>();

    internal Almacen Almacen => Servicio<Almacen>();

    /// <summary>Da de alta un emisor por la API y devuelve su certificado con clave.</summary>
    internal async Task<EmisorPrueba> Emisor(string? rut = null, string nombre = "Empresa de prueba S.A.", bool simulado = false, string? mail = null, string? url = null)
    {
        rut ??= Ruts.Nuevo();
        using var http = CreateClient();
        var respuesta = await http.PostAsJsonAsync("/admin/emisores", new { rut, denominacion = nombre, simulado, mail, urlServicio = url });
        respuesta.EnsureSuccessStatusCode();
        var json = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        var pfx = Convert.FromBase64String(json.GetProperty("certificado").GetProperty("pfx").GetString()!);
        return new EmisorPrueba(rut, nombre, Autoridad.Recargar(pfx), this);
    }

    internal Cae Cae(string rut, int tipo, string serie = "A", long desde = 1, long hasta = 1000, DateOnly? autorizacion = null, DateOnly? vencimiento = null,
        int? especial = null, int? causal = null) =>
        Servicio<Administracion>().CrearCae(new NuevoCae
        {
            Rut = rut, Tipo = tipo, Serie = serie, Desde = desde, Hasta = hasta, Autorizacion = autorizacion, Vencimiento = vencimiento,
            Especial = especial, Causal = causal,
        });

    /// <summary>Llama a una operación SOAP firmando con WS-Security; devuelve la respuesta HTTP y el SOAP de respuesta.</summary>
    internal async Task<(HttpResponseMessage Respuesta, XmlDocument Soap)> Soap(string ruta, string servicio, string operacion, X509Certificate2 certificado,
        Action<XmlDocument, XmlElement> contenido)
    {
        var envelope = Firmas.CrearSoapFirmado(doc =>
        {
            var op = doc.CreateElement("dgi", $"{servicio}.{operacion}", Ns.Dgi);
            contenido(doc, op);
            return op;
        }, certificado);
        return await Post(ruta, envelope);
    }

    internal async Task<(HttpResponseMessage Respuesta, XmlDocument Soap)> Post(string ruta, string cuerpo)
    {
        using var http = CreateClient();
        var respuesta = await http.PostAsync(ruta, new StringContent(cuerpo, Encoding.UTF8, "text/xml"));
        var texto = await respuesta.Content.ReadAsStringAsync();
        return (respuesta, XmlUtil.TryCargar(texto, out var doc, out _) ? doc : new XmlDocument());
    }

    /// <summary>Una operación con <c>Datain/xmlData</c>; devuelve el documento de <c>Dataout/xmlData</c>.</summary>
    internal async Task<XmlDocument> ConXmlData(string ruta, string servicio, string operacion, X509Certificate2 certificado, string xml)
    {
        var (respuesta, soap) = await Soap(ruta, servicio, operacion, certificado, (doc, op) => DataIn(doc, op, xml));
        Assert.True(respuesta.IsSuccessStatusCode, soap.OuterXml);
        return XmlUtil.Cargar(soap.DocumentElement.Buscar("xmlData")!.InnerText);
    }

    internal static void DataIn(XmlDocument doc, XmlElement op, string xml)
    {
        var datain = doc.CreateElement("dgi", "Datain", Ns.Dgi);
        var xmlData = doc.CreateElement("dgi", "xmlData", Ns.Dgi);
        xmlData.AppendChild(doc.CreateCDataSection(xml));
        datain.AppendChild(xmlData);
        op.AppendChild(datain);
    }

    internal Task<XmlDocument> EnviarSobre(X509Certificate2 certificado, string sobre) =>
        ConXmlData("/ePrueba/ws_eprueba", "WS_eFactura", "EFACRECEPCIONSOBRE", certificado, sobre);

    internal Task<XmlDocument> ConsultarEnvio(X509Certificate2 certificado, XmlDocument acuseSobre) =>
        ConXmlData("/ePrueba/ws_eprueba", "WS_eFactura", "EFACCONSULTARESTADOENVIO", certificado,
            $"<ConsultaCFE xmlns=\"http://dgi.gub.uy\"><IdReceptor>{acuseSobre.Buscar("IDReceptor")!.InnerText}</IdReceptor>" +
            $"<Token>{acuseSobre.Buscar("Token")!.InnerText}</Token></ConsultaCFE>");

    internal Task<XmlDocument> EnviarReporte(X509Certificate2 certificado, string reporte) =>
        ConXmlData("/ePrueba/ws_eprueba", "WS_eFactura", "EFACRECEPCIONREPORTE", certificado, reporte);

    /// <summary>Manda los CFE en un sobre y devuelve el acuse del sobre y el de los CFE.</summary>
    internal async Task<(XmlDocument Sobre, XmlDocument Cfes)> Enviar(EmisorPrueba emisor, params string[] cfes)
    {
        var acuse = await EnviarSobre(emisor.Certificado, emisor.Sobre(cfes));
        Assert.Equal("AS", acuse.Buscar("Estado")!.InnerText);
        return (acuse, await ConsultarEnvio(emisor.Certificado, acuse));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var archivo in new[] { _base, _base + "-wal", _base + "-shm" })
            if (File.Exists(archivo))
                File.Delete(archivo);
    }
}

/// <summary>Un emisor dado de alta, con su certificado.</summary>
internal sealed record EmisorPrueba(string Rut, string Nombre, X509Certificate2 Certificado, Simulador Simulador)
{
    private static long _idEmisor;

    internal static DateOnly Hoy => Hora.Fecha(DateTimeOffset.UtcNow);

    /// <summary>Un generador de CFE de este emisor con el CAE indicado (sin receptor, salvo en la e-Factura).</summary>
    internal GeneradorCfe Cfe(Cae cae, long nro, ReceptorCfe? receptor = null) => new()
    {
        Tipo = cae.Tipo,
        Serie = cae.Serie,
        Nro = nro,
        Fecha = Hoy,
        Firma = DateTimeOffset.UtcNow.AddSeconds(-5),
        RutEmisor = Rut,
        RazonSocialEmisor = Nombre,
        Receptor = receptor ?? (TiposCfe.FamiliaDe(cae.Tipo) == Familia.Factura ? new ReceptorCfe("2", "UY", "211003420017", "Cliente S.A.") : null),
        Cae = cae,
    };

    internal string Firmar(GeneradorCfe cfe) => cfe.Firmar(Certificado);

    internal string Sobre(params string[] cfes) =>
        Sobres.ParaDgi(Rut, Interlocked.Increment(ref _idEmisor), DateTimeOffset.UtcNow, Certificado, cfes);
}

/// <summary>RUT válidos distintos para cada prueba.</summary>
internal static class Ruts
{
    private static int _siguiente = 100_000;

    internal static string Nuevo()
    {
        while (true)
        {
            var cuerpo = $"21{Interlocked.Increment(ref _siguiente):000000}001";
            for (var dv = 0; dv <= 9; dv++)
                if (Documentos.RutValido(cuerpo + dv))
                    return cuerpo + dv;
        }
    }
}

/// <summary>El simulador con configuración propia para una clase de pruebas.</summary>
public sealed class SimuladorEstricto() : Simulador(new() { ["Simulador:Estricto"] = "true" });

public sealed class SimuladorChico() : Simulador(new() { ["Simulador:TamanoMaximoKb"] = "1" });

public sealed class SimuladorConDemora() : Simulador(new() { ["Simulador:DemoraConsultaSegundos"] = "3600" });

public sealed class SimuladorConDemoraCorta() : Simulador(new() { ["Simulador:DemoraConsultaSegundos"] = "1" });

/// <summary>El simulador con la semilla de ejemplo del repositorio.</summary>
public sealed class SimuladorConSemillaDeEjemplo()
    : Simulador(new() { ["Simulador:Semilla"] = Path.Combine(AppContext.BaseDirectory, "semilla.ejemplo.json") });
