using System.Text;
using DgiSimulador.Certificados;
using DgiSimulador.Estado;
using DgiSimulador.Homologacion;
using DgiSimulador.Intercambio;
using DgiSimulador.Soap;

namespace DgiSimulador.Admin;

/// <summary>Programación de una falla.</summary>
public sealed class NuevaFalla
{
    public string Operacion { get; init; } = "*";
    public required string Tipo { get; init; }
    public string? Codigo { get; init; }
    public string? Detalle { get; init; }
    public int Veces { get; init; } = 1;
    public int? Segundos { get; init; }
}

/// <summary>Pedido para que una empresa simulada emita un sobre al producto.</summary>
public sealed class NuevaEmision
{
    public required string RutEmisor { get; init; }
    public required string RutReceptor { get; init; }
    public string Variante { get; init; } = "valido";
    public string Canal { get; init; } = "descarga";
    public string? Url { get; init; }
}

public sealed class CasoManual
{
    public required string Caso { get; init; }
    public bool Cumplido { get; init; } = true;
}

/// <summary>Certificado de prueba emitido por la CA del simulador: <c>pfx</c> en base64, sin contraseña, y los PEM.</summary>
public sealed record CertificadoExportado(string Rut, string Huella, string Pfx, string CertificadoPem, string ClavePem);

/// <summary>CAE autorizado: sus datos y la constancia XML con el formato que baja el portal de la DGI.</summary>
public sealed record CaeAutorizado(DatosCae Cae, string Constancia);

/// <summary>Emisor dado de alta, con su certificado de prueba y los CAE de los tipos pedidos.</summary>
public sealed record EmisorCreado(Emisor Emisor, CertificadoExportado? Certificado, IReadOnlyList<CaeAutorizado> Caes);

/// <summary>Pedido inválido.</summary>
public sealed record ErrorAdmin(string Error);

public sealed record CertificadoRegistrado(string Huella, string? Rut, bool Raiz, bool ConClave);

public sealed record CertificadoImportado(string Huella);

public sealed record SobreResumen(
    long IdReceptor, string Rut, long IdEmisor, string Estado, IReadOnlyList<Motivo> Motivos, int Cantidad, DateTimeOffset Recibido,
    DateTimeOffset? ConsultarDesde);

public sealed record SobreConCfes(SobreRecibido Sobre, IReadOnlyList<CfeRegistrado> Cfes);

public sealed record CfeResumen(
    long Id, long IdReceptor, string Rut, int? Tipo, string? Serie, long? Nro, DateOnly? Fecha, string Estado, IReadOnlyList<Motivo> Motivos,
    IReadOnlyList<string> Advertencias, string? Moneda, decimal? Total, string? RutReceptor, DateTimeOffset Recibido);

public sealed record ReporteResumen(
    long IdReceptor, string Rut, DateOnly? FechaResumen, int? Secuencia, string Estado, IReadOnlyList<Motivo> Motivos,
    IReadOnlyList<string> Advertencias, DateTimeOffset Recibido);

/// <summary>Fallas programadas y los tipos de falla con lo que hace cada uno.</summary>
public sealed record FallasProgramadas(IReadOnlyList<Falla> Activas, IReadOnlyDictionary<string, string> Tipos);

public sealed record FallaProgramada(long Id);

public sealed record IntercambioResumen(
    long Id, string Direccion, string Canal, string Tipo, string RutEmisor, string RutReceptor, long? IdEmisor, string? Nombre, string Estado,
    string? Respuesta, DateTimeOffset Fecha);

/// <summary>Lo que respondió la empresa simulada a un correo: los archivos <c>M_</c> y <c>ME_</c>.</summary>
public sealed record CorreoRecibido(string Tipo, string Detalle, IReadOnlyList<ArchivoIntercambio> Respuestas);

/// <summary>
/// API de administración (<c>/admin</c>): padrón, certificados, CAE, inspección de lo recibido, fallas programadas,
/// intercambio con las empresas simuladas y avance de homologación. No tiene autenticación: el simulador es para
/// desarrollo y CI. Su especificación OpenAPI está en <c>/openapi/v1.json</c>.
/// </summary>
internal static class AdminApi
{
    internal static void Mapear(WebApplication app)
    {
        var admin = app.MapGroup("/admin");
        admin.MapGet("/", Pagina.Inicio).ExcludeFromDescription();
        admin.MapGet("/ca.pem", (Autoridad a) => Results.Text(a.Ca.ExportCertificatePem(), "application/x-pem-file"))
            .WithTags("Certificados").WithSummary("Certificado de la CA de prueba, para confiar en los certificados y acuses del simulador")
            .Produces<string>(contentType: "application/x-pem-file");

        var padron = admin.MapGroup("/emisores").WithTags("Padrón");
        padron.MapGet("/", (Almacen a) => a.Emisores()).WithSummary("Emisores del padrón");
        padron.MapPost("/", (NuevoEmisor nuevo, Administracion adm) => Ejecutar(() =>
        {
            var (emisor, certificado, caes) = adm.CrearEmisor(nuevo);
            return Results.Ok(new EmisorCreado(emisor, Certificado(certificado), [.. caes.Select(adm.Autorizado)]));
        })).WithSummary("Da de alta (o actualiza) un emisor, con su certificado de prueba y un CAE por cada tipo de `tipos`")
            .Produces<EmisorCreado>().Produces<ErrorAdmin>(StatusCodes.Status400BadRequest);
        padron.MapDelete("/{rut}", (string rut, Almacen a) =>
        {
            a.BorrarEmisor(rut);
            return Results.NoContent();
        }).WithSummary("Borra un emisor del padrón").Produces(StatusCodes.Status204NoContent);
        padron.MapPost("/{rut}/certificado", (string rut, Almacen a, Autoridad autoridad) =>
            a.Emisor(rut) is { } e ? Results.Ok(Certificado(autoridad.EmitirExportable(e.Rut, e.Denominacion))) : Results.NotFound())
            .WithSummary("Emite otro certificado de prueba para el emisor")
            .Produces<CertificadoExportado>().Produces(StatusCodes.Status404NotFound);
        padron.MapGet("/{rut}/certificado.pfx", (string rut, Almacen a) =>
            a.PfxDe(rut) is { } pfx ? Results.File(pfx, "application/x-pkcs12", $"{rut}.pfx") : Results.NotFound())
            .WithSummary("Último certificado de prueba del emisor, en PKCS#12 sin contraseña")
            .Produces<byte[]>(contentType: "application/x-pkcs12").Produces(StatusCodes.Status404NotFound);

        var certificados = admin.MapGroup("/certificados").WithTags("Certificados");
        certificados.MapGet("/", (Almacen a) => a.Certificados().Select(c => new CertificadoRegistrado(c.Huella, c.Rut, c.Raiz, c.ConClave)))
            .WithSummary("Certificados registrados");
        certificados.MapPost("/", (NuevoCertificado nuevo, Administracion adm) => Ejecutar(() => Results.Ok(new CertificadoImportado(adm.ImportarCertificado(nuevo)))))
            .WithSummary("Importa un certificado propio, o una CA de confianza con `raiz`")
            .Produces<CertificadoImportado>().Produces<ErrorAdmin>(StatusCodes.Status400BadRequest);

        var cae = admin.MapGroup("/cae").WithTags("CAE");
        cae.MapGet("/", (string? rut, Almacen a) => a.Caes(rut)).WithSummary("CAE autorizados, de todos los emisores o de `rut`");
        cae.MapPost("/", (NuevoCae nuevo, Administracion adm) => Ejecutar(() => Results.Ok(adm.Autorizado(adm.CrearCae(nuevo)))))
            .WithSummary("Autoriza un CAE").Produces<CaeAutorizado>().Produces<ErrorAdmin>(StatusCodes.Status400BadRequest);
        cae.MapGet("/{id:long}/constancia.xml", (long id, Almacen a, Administracion adm) =>
            a.Cae(id) is { } c ? Results.Text(adm.Constancia(c), "text/xml", Encoding.UTF8) : Results.NotFound())
            .WithSummary("Constancia del CAE, firmada por la DGI simulada")
            .Produces<string>(contentType: "text/xml").Produces(StatusCodes.Status404NotFound);

        var recibido = admin.MapGroup("/").WithTags("Recibido");
        recibido.MapGet("/sobres", (string? rut, Almacen a) => a.Sobres(rut).Select(s => new SobreResumen(
            s.IdReceptor, s.Rut, s.IdEmisor, s.Estado, s.Motivos, s.Cantidad, s.Recibido, s.ConsultarDesde)))
            .WithSummary("Sobres recibidos por EFACRECEPCIONSOBRE");
        recibido.MapGet("/sobres/{id:long}", (long id, Almacen a) =>
            a.Sobre(id) is { } s ? Results.Ok(new SobreConCfes(s, a.CfesDeSobre(id))) : Results.NotFound())
            .WithSummary("Un sobre y sus CFE").Produces<SobreConCfes>().Produces(StatusCodes.Status404NotFound);
        recibido.MapGet("/cfes", (string? rut, Almacen a) => a.Cfes(rut).Select(c => new CfeResumen(
            c.Id, c.IdReceptor, c.Rut, c.Tipo, c.Serie, c.Nro, c.Fecha, c.Estado, c.Motivos, c.Advertencias, c.Moneda, c.Total, c.RutReceptor, c.Recibido)))
            .WithSummary("CFE recibidos, con sus motivos de rechazo y sus advertencias");
        recibido.MapGet("/cfes/{id:long}", (long id, Almacen a) => a.CfePorId(id) is { } c ? Results.Ok(c) : Results.NotFound())
            .WithSummary("Un CFE con su XML").Produces<CfeRegistrado>().Produces(StatusCodes.Status404NotFound);
        recibido.MapGet("/reportes", (string? rut, Almacen a) => a.Reportes(rut).Select(r => new ReporteResumen(
            r.IdReceptor, r.Rut, r.FechaResumen, r.Secuencia, r.Estado, r.Motivos, r.Advertencias, r.Recibido)))
            .WithSummary("Reportes diarios recibidos");
        recibido.MapGet("/reportes/{id:long}", (long id, Almacen a) => a.Reporte(id) is { } r ? Results.Ok(r) : Results.NotFound())
            .WithSummary("Un reporte diario con su XML").Produces<ReporteRecibido>().Produces(StatusCodes.Status404NotFound);
        recibido.MapPost("/reset", (bool? todo, Almacen a) =>
        {
            a.Limpiar(todo == true);
            return Results.NoContent();
        }).WithSummary("Borra lo recibido; con `todo=true`, también el padrón, los certificados y los CAE").Produces(StatusCodes.Status204NoContent);

        var fallas = admin.MapGroup("/fallas").WithTags("Fallas");
        fallas.MapGet("/", (Almacen a) => new FallasProgramadas(a.Fallas(), Fallas.Tipos)).WithSummary("Fallas programadas y tipos de falla");
        fallas.MapPost("/", (NuevaFalla f, Almacen a) =>
        {
            if (!Fallas.Tipos.ContainsKey(f.Tipo))
                return Results.BadRequest(new { error = $"Tipo desconocido: {f.Tipo}.", tipos = Fallas.Tipos });
            var id = a.GuardarFalla(new Falla(0, f.Operacion, f.Tipo, f.Codigo, f.Detalle, Math.Max(1, f.Veces), f.Segundos));
            return Results.Ok(new FallaProgramada(id));
        }).WithSummary("Programa una falla para las próximas llamadas a una operación")
            .Produces<FallaProgramada>().Produces<ErrorAdmin>(StatusCodes.Status400BadRequest);
        fallas.MapDelete("/", (Almacen a) =>
        {
            a.BorrarFallas();
            return Results.NoContent();
        }).WithSummary("Borra las fallas programadas").Produces(StatusCodes.Status204NoContent);

        var intercambio = admin.MapGroup("/intercambio").WithTags("Intercambio");
        intercambio.MapGet("/", (string? rut, Almacen a) => a.Intercambios(rut).Select(m => new IntercambioResumen(
            m.Id, m.Direccion, m.Canal, m.Tipo, m.RutEmisor, m.RutReceptor, m.IdEmisor, m.Nombre, m.Estado, m.Respuesta, m.Fecha)))
            .WithSummary("Mensajes de intercambio entre el producto y las empresas simuladas");
        intercambio.MapGet("/{id:long}/xml", (long id, Almacen a) =>
            a.Intercambio(id) is { } m ? Results.File(Encoding.UTF8.GetBytes(m.Xml), "text/xml", m.Nombre ?? $"intercambio-{id}.xml") : Results.NotFound())
            .WithSummary("XML de un mensaje de intercambio")
            .Produces<string>(contentType: "text/xml").Produces(StatusCodes.Status404NotFound);
        intercambio.MapPost("/emitir", async (NuevaEmision e, EmisorSimulado emisor, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await emisor.Emitir(e.RutEmisor, e.RutReceptor, e.Variante, e.Canal, e.Url, ct));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message, variantes = EmisorSimulado.Variantes, canales = EmisorSimulado.Canales });
            }
        }).WithSummary("Una empresa simulada emite un sobre al producto")
            .Produces<Emision>().Produces<ErrorAdmin>(StatusCodes.Status400BadRequest);
        intercambio.MapPost("/{rut}/correo", async (string rut, string? nombre, HttpRequest pedido, ReceptorSimulado receptor) =>
        {
            using var lector = new StreamReader(pedido.Body, Encoding.UTF8);
            var xml = await lector.ReadToEndAsync();
            try
            {
                var (tipo, respuestas, detalle) = await receptor.RecibirCorreo(rut, nombre, xml);
                return Results.Ok(new CorreoRecibido(tipo, detalle, respuestas));
            }
            catch (FaultSoap ex)
            {
                return Results.BadRequest(new ErrorAdmin(ex.Message));
            }
        }).WithSummary("Entrega por correo un XML de intercambio a una empresa simulada (el XML va en el cuerpo)")
            .Produces<CorreoRecibido>().Produces<ErrorAdmin>(StatusCodes.Status400BadRequest);

        var homologacion = admin.MapGroup("/homologacion").WithTags("Homologación");
        homologacion.MapGet("/{rut}", (string rut, SetDePruebas set) => rut.EndsWith(".md", StringComparison.Ordinal)
            ? Results.Text(set.Markdown(rut[..^3]), "text/markdown", Encoding.UTF8)
            : Results.Ok(set.Avance(rut)))
            .WithSummary("Avance del checklist de homologación del emisor; con `.md` al final del RUT, en Markdown")
            .Produces<AvanceHomologacion>().Produces<string>(contentType: "text/markdown");
        homologacion.MapPost("/{rut}/manual", (string rut, CasoManual caso, Almacen a) =>
        {
            a.MarcarCasoManual(rut, caso.Caso, caso.Cumplido);
            return Results.NoContent();
        }).WithSummary("Marca un caso que revisa la DGI a mano").Produces(StatusCodes.Status204NoContent);
    }

    private static CertificadoExportado? Certificado(CertificadoEmitido? c) => c is null
        ? null
        : new CertificadoExportado(c.Rut, c.Huella, Convert.ToBase64String(c.Pfx), c.CertificadoPem, c.ClavePem);

    private static IResult Ejecutar(Func<IResult> accion)
    {
        try
        {
            return accion();
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or System.Security.Cryptography.CryptographicException or IOException)
        {
            return Results.BadRequest(new ErrorAdmin(ex.Message));
        }
    }
}
