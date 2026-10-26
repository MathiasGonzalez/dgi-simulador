using System.Xml;
using DgiSimulador.Xml;

namespace DgiSimulador.Recepcion;

/// <summary>Familia de comprobantes: el elemento raíz que le corresponde dentro de <c>CFE</c>.</summary>
internal enum Familia { Ticket, Factura, Exportacion, Remito, RemitoExportacion, Resguardo, Boleta }

/// <summary>Tipos de CFE (<c>CFEType</c> de <c>DGITypes.xsd</c>) y sus características.</summary>
internal static class TiposCfe
{
    private static readonly Dictionary<int, (Familia Familia, string Nombre)> Tipos = new()
    {
        [101] = (Familia.Ticket, "e-Ticket"),
        [102] = (Familia.Ticket, "Nota de crédito de e-Ticket"),
        [103] = (Familia.Ticket, "Nota de débito de e-Ticket"),
        [111] = (Familia.Factura, "e-Factura"),
        [112] = (Familia.Factura, "Nota de crédito de e-Factura"),
        [113] = (Familia.Factura, "Nota de débito de e-Factura"),
        [121] = (Familia.Exportacion, "e-Factura de Exportación"),
        [122] = (Familia.Exportacion, "Nota de crédito de e-Factura de Exportación"),
        [123] = (Familia.Exportacion, "Nota de débito de e-Factura de Exportación"),
        [124] = (Familia.RemitoExportacion, "e-Remito de Exportación"),
        [131] = (Familia.Ticket, "e-Ticket venta por cuenta ajena"),
        [132] = (Familia.Ticket, "Nota de crédito de e-Ticket venta por cuenta ajena"),
        [133] = (Familia.Ticket, "Nota de débito de e-Ticket venta por cuenta ajena"),
        [141] = (Familia.Factura, "e-Factura venta por cuenta ajena"),
        [142] = (Familia.Factura, "Nota de crédito de e-Factura venta por cuenta ajena"),
        [143] = (Familia.Factura, "Nota de débito de e-Factura venta por cuenta ajena"),
        [151] = (Familia.Boleta, "e-Boleta de entrada"),
        [152] = (Familia.Boleta, "Nota de crédito de e-Boleta de entrada"),
        [153] = (Familia.Boleta, "Nota de débito de e-Boleta de entrada"),
        [181] = (Familia.Remito, "e-Remito"),
        [182] = (Familia.Resguardo, "e-Resguardo"),
    };

    internal static IEnumerable<int> Todos => Tipos.Keys.Concat(Tipos.Keys.Select(t => t + 100)).Order();

    /// <summary>Si el código es un tipo de CFE (los 2xx son la versión de contingencia de los 1xx).</summary>
    internal static bool Existe(int tipo) => Tipos.ContainsKey(Base(tipo));

    internal static bool EsContingencia(int tipo) => tipo >= 200;

    internal static int Base(int tipo) => tipo >= 200 ? tipo - 100 : tipo;

    internal static Familia? FamiliaDe(int tipo) => Tipos.TryGetValue(Base(tipo), out var t) ? t.Familia : null;

    internal static string Nombre(int tipo) => Tipos.TryGetValue(Base(tipo), out var t)
        ? EsContingencia(tipo) ? t.Nombre + " (contingencia)" : t.Nombre
        : $"Tipo {tipo}";

    internal static bool EsNota(int tipo) => Base(tipo) % 10 is 2 or 3 && Base(tipo) is not 182;

    internal static bool EsCuentaAjena(int tipo) => Base(tipo) is >= 131 and <= 143;

    internal static string Raiz(Familia familia) => familia switch
    {
        Familia.Ticket => "eTck",
        Familia.Factura => "eFact",
        Familia.Exportacion => "eFact_Exp",
        Familia.Remito => "eRem",
        Familia.RemitoExportacion => "eRem_Exp",
        Familia.Resguardo => "eResg",
        Familia.Boleta => "eBoleta",
        _ => throw new ArgumentOutOfRangeException(nameof(familia)),
    };

    internal static Familia? FamiliaDeRaiz(string raiz) => raiz switch
    {
        "eTck" => Familia.Ticket,
        "eFact" => Familia.Factura,
        "eFact_Exp" => Familia.Exportacion,
        "eRem" => Familia.Remito,
        "eRem_Exp" => Familia.RemitoExportacion,
        "eResg" => Familia.Resguardo,
        "eBoleta" => Familia.Boleta,
        _ => null,
    };
}

/// <summary>Los datos de un CFE que usan los controles, leídos sin suponer que el XML es válido.</summary>
internal sealed class CfeLeido
{
    private CfeLeido(XmlElement elemento) => Elemento = elemento;

    /// <summary>El elemento <c>CFE</c>.</summary>
    internal XmlElement Elemento { get; }

    /// <summary><c>eTck</c>, <c>eFact</c>, …</summary>
    internal XmlElement? Comprobante { get; private init; }

    internal Familia? Familia { get; private init; }
    internal XmlElement? IdDoc { get; private init; }
    internal XmlElement? Emisor { get; private init; }
    internal XmlElement? Receptor { get; private init; }
    internal XmlElement? Totales { get; private init; }
    internal XmlElement? CaeData { get; private init; }
    internal XmlElement? ComplFiscal { get; private init; }
    internal IReadOnlyList<XmlElement> Items { get; private init; } = [];
    internal IReadOnlyList<XmlElement> Referencias { get; private init; } = [];
    internal IReadOnlyList<XmlElement> DescuentosGlobales { get; private init; } = [];

    internal int? Tipo { get; private init; }
    internal string? Serie { get; private init; }
    internal long? Nro { get; private init; }
    internal DateOnly? FechaEmision { get; private init; }
    internal DateTimeOffset? Firma { get; private init; }
    internal string? RutEmisor { get; private init; }
    internal string? Moneda { get; private init; }
    internal decimal? TipoCambio { get; private init; }

    /// <summary><c>MntBruto</c>: 1 = precios con IVA incluido.</summary>
    internal int? MontosBrutos { get; private init; }

    internal string? TipoDocReceptor => Receptor.Texto("TipoDocRecep");
    internal string? DocReceptor => Receptor.Texto("DocRecep") ?? Receptor.Texto("DocRecepExt");

    /// <summary>El RUT del receptor, si es una empresa uruguaya (documento 2).</summary>
    internal string? RutReceptor => TipoDocReceptor == "2" ? Receptor.Texto("DocRecep") : null;

    internal bool EsContingencia => Tipo is { } t && TiposCfe.EsContingencia(t);

    internal decimal Monto(string campo) => XmlUtil.Decimal(Totales.Texto(campo)) ?? 0m;

    internal decimal? MontoInformado(string campo) => XmlUtil.Decimal(Totales.Texto(campo));

    /// <summary>Total del comprobante para mostrar y para el reporte: <c>MntTotal</c>, o lo retenido en el resguardo.</summary>
    internal decimal? Total => MontoInformado("MntTotal") ?? MontoInformado("MntTotRetenido");

    internal decimal Neto => Monto("MntNoGrv") + Monto("MntExpoyAsim") + Monto("MntImpuestoPerc") + Monto("MntIVaenSusp")
                              + Monto("MntNetoIvaTasaMin") + Monto("MntNetoIVATasaBasica") + Monto("MntNetoIVAOtra");

    internal decimal Iva => Monto("MntIVATasaMin") + Monto("MntIVATasaBasica") + Monto("MntIVAOtra");

    internal static CfeLeido Leer(XmlElement cfe)
    {
        var comprobante = cfe.Hijos().FirstOrDefault(e => e.NamespaceURI == Ns.Cfe && TiposCfe.FamiliaDeRaiz(e.LocalName) is not null);
        var encabezado = comprobante.Hijo("Encabezado");
        var idDoc = encabezado.Hijo("IdDoc");
        var totales = encabezado.Hijo("Totales");
        return new CfeLeido(cfe)
        {
            Comprobante = comprobante,
            Familia = comprobante is null ? null : TiposCfe.FamiliaDeRaiz(comprobante.LocalName),
            IdDoc = idDoc,
            Emisor = encabezado.Hijo("Emisor"),
            Receptor = encabezado.Hijo("Receptor"),
            Totales = totales,
            CaeData = comprobante.Hijo("CAEData"),
            ComplFiscal = comprobante.Hijo("Compl_Fiscal"),
            Items = [.. comprobante.Hijo("Detalle").Hijos("Item")],
            Referencias = [.. comprobante.Hijo("Referencia").Hijos("Referencia")],
            DescuentosGlobales = [.. comprobante.Hijo("DscRcgGlobal").Hijos("DRG_Item")],
            Tipo = XmlUtil.Entero(idDoc.Texto("TipoCFE")) is { } tipo and <= 999 ? (int)tipo : null,
            Serie = idDoc.Texto("Serie"),
            Nro = XmlUtil.Entero(idDoc.Texto("Nro")),
            FechaEmision = Hora.LeerFecha(idDoc.Texto("FchEmis")),
            Firma = Hora.Leer(comprobante.Texto("TmstFirma")),
            RutEmisor = encabezado.Hijo("Emisor").Texto("RUCEmisor"),
            Moneda = totales.Texto("TpoMoneda"),
            TipoCambio = XmlUtil.Decimal(totales.Texto("TpoCambio")),
            MontosBrutos = XmlUtil.Entero(idDoc.Texto("MntBruto")) is { } bruto ? (int)bruto : null,
        };
    }

    internal string Identificacion => $"{(Tipo is { } t ? TiposCfe.Nombre(t) : "CFE")} {Serie}{Nro}";
}
