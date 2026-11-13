using System.Globalization;
using System.Xml;
using DgiSimulador.Xml;

namespace DgiSimulador.Recepcion;

/// <summary>Una zona <c>Rsmn_*</c> del reporte: el tipo de comprobante y lo que informa.</summary>
internal sealed record ZonaReporte(string Nombre, int Tipo, XmlElement? Datos, long? Utilizados, long? Emitidos, long? Anulados,
    List<(string Serie, long Desde, long Hasta)> Usados, List<(string Serie, long Desde, long Hasta)> RangosAnulados, decimal Total)
{
    /// <summary>Si es de comprobantes de contingencia (CFC): informa <c>CantCFCEmi</c> y no lleva rangos.</summary>
    internal bool EsContingencia => Datos.Hijo("CantCFCEmi") is not null;

    internal IEnumerable<XmlElement> Montos => Datos.Hijo("Montos").Hijos();
}

/// <summary>
/// Controles del Formato del Reporte Diario v13.2 que el XSD no expresa (motivo R04): las cantidades de la carátula y
/// de cada zona entre sí y con sus rangos, y los montos de cada línea de la tabla por fecha y sucursal. Las fórmulas son
/// las de la columna "Validación" de cada campo; el número de campo va en el comentario de cada control.
/// </summary>
internal static class ReglasReporte
{
    private static readonly DateOnly FechaMinima = new(2011, 10, 1);
    private static readonly DateOnly FechaMaxima = new(2050, 12, 31);

    /// <summary>B-C12 a B-C21: los conceptos que suman en el total de la línea (B-C24).</summary>
    private static readonly string[] ConceptosDelTotal =
    [
        "TotMntNoGrv", "TotMntExpyAsim", "TotMntImpPerc", "TotMntIVAenSusp", "TotMntIVATasaMin", "TotMntIVATasaBas", "TotMntIVAOtra",
        "MntIVATasaMin", "MntIVATasaBas", "MntIVAOtra",
    ];

    internal static List<ZonaReporte> Zonas(XmlElement reporte) =>
    [
        .. reporte.Hijos().Where(e => e.LocalName.StartsWith("Rsmn_", StringComparison.Ordinal)).Select(zona =>
        {
            var datos = zona.Hijo("RsmnData");
            var tipo = (int)(XmlUtil.Entero(zona.Texto("TipoComp")) ?? 0);
            static List<(string, long, long)> Rangos(XmlElement? rangos, string item) =>
            [
                .. rangos.Hijos(item).Select(r => (r.Texto("Serie") ?? "", XmlUtil.Entero(r.Texto("NroDesde")) ?? 0, XmlUtil.Entero(r.Texto("NroHasta")) ?? 0)),
            ];
            var total = datos.Hijo("Montos").Hijos().Sum(m => XmlUtil.Decimal(m.Texto("TotMntTotal")) ?? XmlUtil.Decimal(m.Texto("TotMntRetenido")) ?? 0m);
            return new ZonaReporte(zona.LocalName, tipo, datos, XmlUtil.Entero(datos.Texto("CantDocsUtil")),
                XmlUtil.Entero(datos.Texto("CantDocsEmi")) ?? XmlUtil.Entero(datos.Texto("CantCFCEmi")), XmlUtil.Entero(datos.Texto("CantDocsAnulados")),
                Rangos(datos.Hijo("RngDocsUtil"), "RDU_Item"), Rangos(datos.Hijo("RngDocsAnulados"), "RDA_Item"), total);
        }),
    ];

    internal static List<Hallazgo> Controlar(XmlElement reporte, IReadOnlyList<ZonaReporte> zonas, OpcionesSimulador opciones, DateOnly hoy)
    {
        var h = new List<Hallazgo>();
        Caratula(reporte.Hijo("Caratula"), zonas, h);
        foreach (var zona in zonas)
        {
            Cantidades(zona, h);
            Montos(zona, opciones, hoy, h);
        }
        return h;
    }

    private static void Caratula(XmlElement? caratula, IReadOnlyList<ZonaReporte> zonas, List<Hallazgo> h)
    {
        // A-C2: dígito verificador del RUC.
        if (caratula.Texto("RUCEmisor") is { } rut && !Documentos.RutValido(rut))
            h.Add(new("R04", $"RUCEmisor {rut} no tiene un dígito verificador válido."));
        // A-C3 y A-C6: fechas entre el 2011-10-01 y el 2050-12-31.
        if (Hora.LeerFecha(caratula.Texto("FechaResumen")) is { } resumen && FueraDeRango(resumen))
            h.Add(new("R04", $"FechaResumen {Hora.Formato(resumen)} tiene que estar entre el 2011-10-01 y el 2050-12-31."));
        if (Hora.Leer(caratula.Texto("TmstFirmaEnv")) is { } firma && FueraDeRango(Hora.Fecha(firma)))
            h.Add(new("R04", $"TmstFirmaEnv {Hora.Formato(firma)} tiene que estar entre el 2011-10-01 y el 2050-12-31."));

        // A-C7: la suma de los CFE utilizados (B-C26) y de los CFC emitidos (B-C30) de las zonas.
        var cantidad = XmlUtil.Entero(caratula.Texto("CantComprobantes"));
        var suma = zonas.Sum(z => z.Utilizados ?? z.Emitidos ?? 0);
        if (cantidad is { } cc && cc != suma)
            h.Add(new("R04", $"CantComprobantes es {cc} y la suma de los CFE utilizados y los CFC emitidos de las zonas da {suma}."));
    }

    private static bool FueraDeRango(DateOnly fecha) => fecha < FechaMinima || fecha > FechaMaxima;

    private static void Cantidades(ZonaReporte zona, List<Hallazgo> h)
    {
        // B-C29: los emitidos son los utilizados menos los anulados.
        if (zona.Utilizados is { } u && zona.Emitidos is { } e && u != e + (zona.Anulados ?? 0))
            h.Add(new("R04", $"{zona.Nombre}: CantDocsUtil {u} no es CantDocsEmi {e} + CantDocsAnulados {zona.Anulados ?? 0}."));

        // B-C42, B-C43, B-C45 y B-C46: cada rango va del 1 al 9999999, con el final mayor o igual que el inicial.
        foreach (var (serie, desde, hasta) in zona.Usados.Concat(zona.RangosAnulados))
            if (desde < 1 || hasta < desde || hasta > 9_999_999)
                h.Add(new("R04", $"{zona.Nombre}: el rango {serie} {desde}–{hasta} tiene que ir de 1 a 9999999, con el final mayor o igual que el inicial."));

        if (zona.EsContingencia)
            return;
        // B-C26 y B-C28: las cantidades son la suma de los números de sus rangos.
        var enRangos = zona.Usados.Sum(r => r.Hasta - r.Desde + 1);
        if (zona.Utilizados is { } utilizados && utilizados != enRangos)
            h.Add(new("R04", $"{zona.Nombre}: CantDocsUtil es {utilizados} y los rangos de RngDocsUtil suman {enRangos} números."));
        var enAnulados = zona.RangosAnulados.Sum(r => r.Hasta - r.Desde + 1);
        if ((zona.Anulados ?? 0) != enAnulados)
            h.Add(new("R04", $"{zona.Nombre}: CantDocsAnulados es {zona.Anulados ?? 0} y los rangos de RngDocsAnulados suman {enAnulados} números."));
    }

    private static void Montos(ZonaReporte zona, OpcionesSimulador opciones, DateOnly hoy, List<Hallazgo> h)
    {
        var lineas = zona.Montos.ToList();
        // La tabla de montos es obligatoria si hay comprobantes emitidos (B-C29 + B-C30 > 0).
        if (lineas.Count == 0 && zona.Emitidos > 0)
            h.Add(new("R04", $"{zona.Nombre}: informa {zona.Emitidos} comprobantes emitidos y no tiene la tabla de montos por fecha y sucursal."));
        // La tabla se agrupa por fecha, sucursal e indicador de pagos por cuenta de terceros, sin repetir.
        foreach (var repetida in lineas.GroupBy(l => (l.Texto("Fecha"), l.Texto("CodSuc"), l.Texto("IndPagCta3ros"))).Where(g => g.Count() > 1))
            h.Add(new("R04", $"{zona.Nombre}: la tabla de montos repite la fecha {repetida.Key.Item1} y la sucursal {repetida.Key.Item2}."));

        // Cada comprobante redondea por su cuenta: las sumas pueden diferir de la fórmula hasta la tolerancia por comprobante.
        var margen = opciones.Tolerancia * Math.Max(1, zona.Emitidos ?? 1);
        var sinTotal = zona.Tipo is 124 or 224;
        foreach (var linea in lineas)
        {
            var donde = $"{zona.Nombre}, {linea.Texto("Fecha")} sucursal {linea.Texto("CodSuc")}";
            decimal? Monto(string campo) => XmlUtil.Decimal(linea.Texto(campo));

            // B-C10: la fecha del comprobante, dentro del rango y hasta 60 días después de hoy.
            if (Hora.LeerFecha(linea.Texto("Fecha")) is { } fecha && (FueraDeRango(fecha) || fecha > hoy.AddDays(60)))
                h.Add(new("R04", $"{donde}: la fecha tiene que estar entre el 2011-10-01 y el 2050-12-31 y no pasar de hoy + 60 días."));

            // B-C22 y B-C23: las tasas vigentes. B-C19 y B-C20: el IVA es el neto por la tasa.
            Iva(donde, "Min", Monto("TotMntIVATasaMin"), Monto("MntIVATasaMin"), Monto("IVATasaMin"), opciones.TasaMinima, margen, h);
            Iva(donde, "Bas", Monto("TotMntIVATasaBas"), Monto("MntIVATasaBas"), Monto("IVATasaBas"), opciones.TasaBasica, margen, h);

            // B-C18 y B-C21: el neto y el IVA a otra tasa van juntos.
            if ((Monto("TotMntIVAOtra") ?? 0m) > 0 != (Monto("MntIVAOtra") ?? 0m) > 0)
                h.Add(new("R04", $"{donde}: TotMntIVAOtra y MntIVAOtra tienen que ser los dos mayores que cero o ninguno."));

            // B-C24: el total es la suma de B-C12 a B-C21, salvo en el e-Remito de Exportación.
            if (!sinTotal && Monto("TotMntTotal") is { } total)
            {
                var suma = ConceptosDelTotal.Sum(c => Monto(c) ?? 0m);
                if (Math.Abs(total - suma) > margen)
                    h.Add(new("R04", $"{donde}: TotMntTotal informa {F(total)} y la suma de los montos netos y de IVA da {F(suma)}."));
            }
        }
    }

    private static void Iva(string donde, string tasa, decimal? neto, decimal? iva, decimal? informada, decimal vigente, decimal margen, List<Hallazgo> h)
    {
        if (informada is { } t && t != vigente)
            h.Add(new("R04", $"{donde}: IVATasa{tasa} informa {t.ToString(CultureInfo.InvariantCulture)} y la tasa vigente es {vigente.ToString(CultureInfo.InvariantCulture)}."));
        if (neto is null && iva is null)
            return;
        var esperado = ReglasFormato.Redondear((neto ?? 0m) * (informada ?? vigente) / 100m);
        if (Math.Abs((iva ?? 0m) - esperado) > margen)
            h.Add(new("R04", $"{donde}: MntIVATasa{tasa} informa {F(iva ?? 0m)} y TotMntIVATasa{tasa} {F(neto ?? 0m)} por la tasa da {F(esperado)}."));
    }

    private static string F(decimal valor) => valor.ToString("0.00", CultureInfo.InvariantCulture);
}
