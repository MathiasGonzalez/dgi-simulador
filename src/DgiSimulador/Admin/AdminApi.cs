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

/// <summary>
/// API de administración (<c>/admin</c>): padrón, certificados, CAE, inspección de lo recibido, fallas programadas,
/// intercambio con las empresas simuladas y avance de homologación. No tiene autenticación: el simulador es para
/// desarrollo y CI.
/// </summary>
internal static class AdminApi
{
    internal static void Mapear(WebApplication app)
    {
        var admin = app.MapGroup("/admin");
        admin.MapGet("/", Pagina.Inicio);
        admin.MapGet("/ca.pem", (Autoridad a) => Results.Text(a.Ca.ExportCertificatePem(), "application/x-pem-file"));

        admin.MapGet("/emisores", (Almacen a) => a.Emisores());
        admin.MapPost("/emisores", (NuevoEmisor nuevo, Administracion adm) => Ejecutar(() =>
        {
            var (emisor, certificado) = adm.CrearEmisor(nuevo);
            return Results.Ok(new { emisor, certificado = Certificado(certificado) });
        }));
        admin.MapDelete("/emisores/{rut}", (string rut, Almacen a) =>
        {
            a.BorrarEmisor(rut);
            return Results.NoContent();
        });
        admin.MapPost("/emisores/{rut}/certificado", (string rut, Almacen a, Autoridad autoridad) =>
            a.Emisor(rut) is { } e ? Results.Ok(Certificado(autoridad.EmitirExportable(e.Rut, e.Denominacion))) : Results.NotFound());
        admin.MapGet("/emisores/{rut}/certificado.pfx", (string rut, Almacen a) =>
            a.PfxDe(rut) is { } pfx ? Results.File(pfx, "application/x-pkcs12", $"{rut}.pfx") : Results.NotFound());

        admin.MapGet("/certificados", (Almacen a) => a.Certificados().Select(c => new { c.Huella, c.Rut, c.Raiz, c.ConClave }));
        admin.MapPost("/certificados", (NuevoCertificado nuevo, Administracion adm) => Ejecutar(() => Results.Ok(new { huella = adm.ImportarCertificado(nuevo) })));

        admin.MapGet("/cae", (string? rut, Almacen a) => a.Caes(rut));
        admin.MapPost("/cae", (NuevoCae nuevo, Administracion adm) => Ejecutar(() =>
        {
            var cae = adm.CrearCae(nuevo);
            return Results.Ok(new { cae = Administracion.DatosCae(cae), constancia = adm.Constancia(cae) });
        }));
        admin.MapGet("/cae/{id:long}/constancia.xml", (long id, Almacen a, Administracion adm) =>
            a.Cae(id) is { } cae ? Results.Text(adm.Constancia(cae), "text/xml", Encoding.UTF8) : Results.NotFound());

        admin.MapGet("/sobres", (string? rut, Almacen a) => a.Sobres(rut).Select(s => new
        {
            s.IdReceptor, s.Rut, s.IdEmisor, s.Estado, s.Motivos, s.Cantidad, s.Recibido, s.ConsultarDesde,
        }));
        admin.MapGet("/sobres/{id:long}", (long id, Almacen a) =>
            a.Sobre(id) is { } s ? Results.Ok(new { sobre = s, cfes = a.CfesDeSobre(id) }) : Results.NotFound());
        admin.MapGet("/cfes", (string? rut, Almacen a) => a.Cfes(rut).Select(c => new
        {
            c.Id, c.IdReceptor, c.Rut, c.Tipo, c.Serie, c.Nro, c.Fecha, c.Estado, c.Motivos, c.Advertencias, c.Moneda, c.Total, c.RutReceptor, c.Recibido,
        }));
        admin.MapGet("/cfes/{id:long}", (long id, Almacen a) => a.CfePorId(id) is { } c ? Results.Ok(c) : Results.NotFound());
        admin.MapGet("/reportes", (string? rut, Almacen a) => a.Reportes(rut).Select(r => new
        {
            r.IdReceptor, r.Rut, r.FechaResumen, r.Secuencia, r.Estado, r.Motivos, r.Advertencias, r.Recibido,
        }));
        admin.MapGet("/reportes/{id:long}", (long id, Almacen a) => a.Reporte(id) is { } r ? Results.Ok(r) : Results.NotFound());

        admin.MapGet("/fallas", (Almacen a) => new { activas = a.Fallas(), tipos = Fallas.Tipos });
        admin.MapPost("/fallas", (NuevaFalla f, Almacen a) =>
        {
            if (!Fallas.Tipos.ContainsKey(f.Tipo))
                return Results.BadRequest(new { error = $"Tipo desconocido: {f.Tipo}.", tipos = Fallas.Tipos });
            var id = a.GuardarFalla(new Falla(0, f.Operacion, f.Tipo, f.Codigo, f.Detalle, Math.Max(1, f.Veces), f.Segundos));
            return Results.Ok(new { id });
        });
        admin.MapDelete("/fallas", (Almacen a) =>
        {
            a.BorrarFallas();
            return Results.NoContent();
        });

        admin.MapPost("/reset", (bool? todo, Almacen a) =>
        {
            a.Limpiar(todo == true);
            return Results.NoContent();
        });

        admin.MapGet("/intercambio", (string? rut, Almacen a) => a.Intercambios(rut).Select(m => new
        {
            m.Id, m.Direccion, m.Canal, m.Tipo, m.RutEmisor, m.RutReceptor, m.IdEmisor, m.Nombre, m.Estado, m.Respuesta, m.Fecha,
        }));
        admin.MapGet("/intercambio/{id:long}/xml", (long id, Almacen a) =>
            a.Intercambio(id) is { } m ? Results.File(Encoding.UTF8.GetBytes(m.Xml), "text/xml", m.Nombre ?? $"intercambio-{id}.xml") : Results.NotFound());
        admin.MapPost("/intercambio/emitir", async (NuevaEmision e, EmisorSimulado emisor, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await emisor.Emitir(e.RutEmisor, e.RutReceptor, e.Variante, e.Canal, e.Url, ct));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message, variantes = EmisorSimulado.Variantes, canales = EmisorSimulado.Canales });
            }
        });
        admin.MapPost("/intercambio/{rut}/correo", async (string rut, string? nombre, HttpRequest pedido, ReceptorSimulado receptor) =>
        {
            using var lector = new StreamReader(pedido.Body, Encoding.UTF8);
            var xml = await lector.ReadToEndAsync();
            try
            {
                var (tipo, respuestas, detalle) = await receptor.RecibirCorreo(rut, nombre, xml);
                return Results.Ok(new { tipo, detalle, respuestas });
            }
            catch (FaultSoap ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        admin.MapGet("/homologacion/{rut}", (string rut, SetDePruebas set) => rut.EndsWith(".md", StringComparison.Ordinal)
            ? Results.Text(set.Markdown(rut[..^3]), "text/markdown", Encoding.UTF8)
            : Results.Ok(set.Avance(rut)));
        admin.MapPost("/homologacion/{rut}/manual", (string rut, CasoManual caso, Almacen a) =>
        {
            a.MarcarCasoManual(rut, caso.Caso, caso.Cumplido);
            return Results.NoContent();
        });
    }

    private static object? Certificado(CertificadoEmitido? c) => c is null
        ? null
        : new { c.Rut, c.Huella, pfx = Convert.ToBase64String(c.Pfx), c.CertificadoPem, c.ClavePem };

    private static IResult Ejecutar(Func<IResult> accion)
    {
        try
        {
            return accion();
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or System.Security.Cryptography.CryptographicException or IOException)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }
}
