using System.Text;
using System.Text.Json;
using DgiSimulador.Estado;
using DgiSimulador.Recepcion;
using DgiSimulador.Xml;
using Microsoft.Extensions.Options;

namespace DgiSimulador.Homologacion;

/// <summary>Un caso del set de pruebas: qué tiene que lograr el emisor y cuántas veces.</summary>
public sealed record CasoDePrueba(
    string Id,
    string Descripcion,
    string Tipo,
    int Cantidad = 1,
    int[]? Tipos = null,
    bool? ConReceptor = null,
    bool? ConReferencia = null,
    bool? MonedaExtranjera = null,
    bool? Contingencia = null,
    bool? ConComprobantes = null,
    string? Rol = null,
    bool Opcional = false);

public sealed record DefinicionSet(string Version, string Fuente, IReadOnlyList<CasoDePrueba> Casos);

/// <summary>El avance de un caso: cuántas evidencias tiene y cuáles (ids de CFE, reportes o mensajes de intercambio).</summary>
public sealed record AvanceCaso(CasoDePrueba Caso, int Logrados, bool Cumplido, IReadOnlyList<string> Evidencias, int ConAdvertencias);

public sealed record AvanceHomologacion(string Rut, string Version, string Fuente, int Cumplidos, int Obligatorios, IReadOnlyList<AvanceCaso> Casos);

/// <summary>
/// El checklist de homologación: compara lo que el emisor logró en el simulador (CFE aceptados, reportes, intercambio
/// y casos manuales) con el set de pruebas. El set incluido es provisorio y se reemplaza con <c>Simulador:SetDePruebas</c>.
/// </summary>
internal sealed class SetDePruebas(Almacen almacen, IOptions<OpcionesSimulador> opciones)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Lazy<DefinicionSet> _definicion = new(() =>
    {
        var json = opciones.Value.SetDePruebas is { Length: > 0 } archivo ? File.ReadAllText(archivo) : Esquemas.Leer("Homologacion/set-pruebas.json");
        return JsonSerializer.Deserialize<DefinicionSet>(json, Json) ?? throw new InvalidOperationException("El set de pruebas está vacío.");
    });

    internal DefinicionSet Definicion => _definicion.Value;

    internal AvanceHomologacion Avance(string rut)
    {
        var cfes = almacen.CfesAceptados(rut).Select(c => (Registro: c, Leido: CfeLeido.Leer(XmlUtil.Cargar(c.Xml).DocumentElement!))).ToList();
        var reportes = almacen.ReportesAceptados(rut);
        var intercambio = almacen.Intercambios(rut, limite: 10_000);
        var manuales = almacen.CasosManuales(rut);

        var casos = Definicion.Casos.Select(caso =>
        {
            List<string> evidencias;
            var conAdvertencias = 0;
            switch (caso.Tipo)
            {
                case "cfe":
                    var cumplen = cfes.Where(c => Cumple(caso, c.Leido)).ToList();
                    conAdvertencias = cumplen.Count(c => c.Registro.Advertencias.Count > 0);
                    evidencias = [.. cumplen.Select(c => $"CFE {c.Registro.Id}: {c.Leido.Identificacion} ({c.Registro.Estado})")];
                    break;
                case "reporte":
                    var validos = reportes.Where(r => caso.ConComprobantes != true || (r.Advertencias.Count == 0 && TieneComprobantes(r))).ToList();
                    evidencias = [.. validos.Select(r => $"Reporte {r.IdReceptor} del {r.FechaResumen} (secuencia {r.Secuencia})")];
                    break;
                case "intercambio":
                    evidencias = caso.Rol == "receptor" ? RespuestasValidas(rut, intercambio) : SobresRecibidos(rut, intercambio);
                    break;
                default:
                    evidencias = manuales.Contains(caso.Id) ? ["Marcado como cumplido"] : [];
                    break;
            }
            return new AvanceCaso(caso, evidencias.Count, evidencias.Count >= caso.Cantidad, evidencias.Take(20).ToList(), conAdvertencias);
        }).ToList();

        var obligatorios = casos.Where(c => !c.Caso.Opcional).ToList();
        return new AvanceHomologacion(rut, Definicion.Version, Definicion.Fuente, obligatorios.Count(c => c.Cumplido), obligatorios.Count, casos);
    }

    private static bool Cumple(CasoDePrueba caso, CfeLeido cfe) =>
        cfe.Tipo is { } tipo
        && (caso.Tipos is null || caso.Tipos.Contains(tipo))
        && (caso.ConReceptor is not true || cfe.DocReceptor is not null)
        && (caso.ConReferencia is not true || cfe.Referencias.Count > 0)
        && (caso.MonedaExtranjera is not true || cfe.Moneda is not (null or "UYU"))
        && (caso.Contingencia is not true || cfe.EsContingencia);

    private static bool TieneComprobantes(ReporteRecibido reporte) =>
        XmlUtil.Cargar(reporte.Xml).DocumentElement!.Hijos().Any(z => z.LocalName.StartsWith("Rsmn_", StringComparison.Ordinal));

    /// <summary>Sobres del emisor que una empresa simulada recibió (AS).</summary>
    private static List<string> SobresRecibidos(string rut, IReadOnlyList<MensajeIntercambio> mensajes) =>
    [
        .. mensajes.Where(m => m is { Direccion: "recibido", Tipo: "SOBRE", Estado: "AS" } && m.RutEmisor == rut)
            .Select(m => $"Sobre {m.Nombre} recibido por {m.RutReceptor} ({m.Canal})"),
    ];

    /// <summary>Sobres de una empresa simulada que el emisor respondió con un ACKSobre y un ACKCFE válidos.</summary>
    private static List<string> RespuestasValidas(string rut, IReadOnlyList<MensajeIntercambio> mensajes)
    {
        var validas = mensajes.Where(m => m.Direccion == "recibido" && m.RutEmisor == rut && m.Estado == "valido").ToList();
        return validas.Any(m => m.Tipo == "ACKSOBRE") && validas.Any(m => m.Tipo == "ACKCFE")
            ? [.. validas.Where(m => m.Tipo is "ACKSOBRE" or "ACKCFE").Select(m => $"{m.Tipo} {m.Nombre ?? m.Id.ToString()} para {m.RutReceptor}")]
            : [];
    }

    /// <summary>El avance en Markdown, para guardarlo como evidencia interna.</summary>
    internal string Markdown(string rut)
    {
        var avance = Avance(rut);
        var sb = new StringBuilder();
        sb.AppendLine($"# Homologación de {rut} en el simulador").AppendLine();
        sb.AppendLine($"Set de pruebas {avance.Version}: {avance.Cumplidos} de {avance.Obligatorios} casos obligatorios cumplidos.").AppendLine();
        sb.AppendLine("| Caso | Estado | Evidencia |").AppendLine("|---|---|---|");
        foreach (var c in avance.Casos)
        {
            var estado = c.Cumplido ? (c.ConAdvertencias > 0 ? "cumplido, con advertencias" : "cumplido") : c.Caso.Opcional ? "opcional" : "pendiente";
            sb.AppendLine($"| {c.Caso.Descripcion} | {estado} | {string.Join("<br>", c.Evidencias.Take(3))} |");
        }
        sb.AppendLine().AppendLine("> El simulador no reemplaza la homologación de la DGI: este resumen es una verificación previa.");
        return sb.ToString();
    }
}
