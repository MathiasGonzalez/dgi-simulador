using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using System.Xml;
using DgiSimulador.Estado;
using DgiSimulador.Xml;

namespace DgiSimulador.Recepcion;

/// <summary>Carátula común de los acuses: quién responde, a quién y qué envío.</summary>
internal sealed record Caratula(
    string RutReceptor,
    string RutEmisor,
    long IdRespuesta,
    string NombreArchivo,
    DateTimeOffset Recibido,
    long IdEmisor,
    long IdReceptor,
    DateTimeOffset Tmst);

/// <summary>Resultado de un CFE para su acuse.</summary>
internal sealed record DetalleCfe(int Ordinal, int? Tipo, string? Serie, long? Nro, DateOnly? Fecha, DateTimeOffset? Firma, string Estado, IReadOnlyList<Motivo> Motivos);

/// <summary>
/// Los acuses firmados: <c>ACKSobre</c>, <c>ACKCFE</c> (de la DGI, <c>ACKCFE_DGI.xsd</c>, o de un receptor,
/// <c>ACKCFE_Partes.xsd</c>) y <c>ACKRepDiario</c>. Los datos opcionales del CFE se informan solo si son válidos para
/// el esquema, para que el acuse de un CFE mal formado siga siendo un XML válido.
/// </summary>
internal static partial class Acuses
{
    internal static string Sobre(Caratula c, int cantidad, string estado, IReadOnlyList<Motivo> motivos, string? token, DateTimeOffset? consultarDesde,
        X509Certificate2 firmante) => Firmar(w =>
    {
        Inicio(w, "ACKSobre");
        w.WriteStartElement("Caratula", Ns.Cfe);
        w.El("RUCReceptor", c.RutReceptor);
        w.El("RUCEmisor", Rut(c.RutEmisor));
        w.El("IDRespuesta", XmlUtil.Numero(c.IdRespuesta));
        w.El("NomArch", XmlUtil.Recortar(c.NombreArchivo, 40));
        w.El("FecHRecibido", Hora.Formato(c.Recibido));
        w.El("IDEmisor", XmlUtil.Numero(Id(c.IdEmisor)));
        w.El("IDReceptor", XmlUtil.Numero(c.IdReceptor));
        w.El("CantidadCFE", XmlUtil.Numero(Math.Clamp(cantidad, 0, 250)));
        w.El("Tmst", Hora.Formato(c.Tmst));
        w.WriteEndElement();

        w.WriteStartElement("Detalle", Ns.Cfe);
        w.El("Estado", estado);
        foreach (var motivo in motivos.Take(30))
            Motivo(w, "MotivosRechazo", motivo, 500);
        if (token is not null && consultarDesde is { } desde)
        {
            w.WriteStartElement("ParamConsulta", Ns.Cfe);
            w.El("Token", token);
            w.El("Fechahora", Hora.Formato(desde));
            w.WriteEndElement();
        }
        w.WriteEndElement();
        w.WriteEndElement();
    }, firmante);

    /// <summary>
    /// <c>ACKCFE</c>. Con <paramref name="deLaDgi"/> los observados de contingencia (CE) llevan <c>ObsCFC</c>; el de un
    /// receptor solo tiene AE o BE.
    /// </summary>
    internal static string Cfes(Caratula c, int enSobre, IReadOnlyList<DetalleCfe> detalles, bool deLaDgi, X509Certificate2 firmante) => Firmar(w =>
    {
        Inicio(w, "ACKCFE");
        w.WriteStartElement("Caratula", Ns.Cfe);
        w.El("RUCReceptor", c.RutReceptor);
        w.El("RUCEmisor", Rut(c.RutEmisor));
        w.El("IDRespuesta", XmlUtil.Numero(c.IdRespuesta));
        w.El("NomArch", XmlUtil.Recortar(c.NombreArchivo, 40));
        w.El("FecHRecibido", Hora.Formato(c.Recibido));
        w.El("IDEmisor", XmlUtil.Numero(Id(c.IdEmisor)));
        w.El("IDReceptor", XmlUtil.Numero(c.IdReceptor));
        w.El("CantenSobre", XmlUtil.Numero(Math.Clamp(enSobre, 0, 250)));
        w.El("CantResponden", XmlUtil.Numero(detalles.Count));
        w.El("CantCFEAceptados", XmlUtil.Numero(detalles.Count(d => d.Estado == "AE" && !EsContingencia(d))));
        w.El("CantCFERechazados", XmlUtil.Numero(detalles.Count(d => d.Estado == "BE" && !EsContingencia(d))));
        if (deLaDgi)
        {
            w.El("CantCFCAceptados", XmlUtil.Numero(detalles.Count(d => d.Estado == "AE" && EsContingencia(d))));
            w.El("CantCFCObservados", XmlUtil.Numero(detalles.Count(d => d.Estado == "CE")));
            w.El("CantOtrosRechazados", XmlUtil.Numero(detalles.Count(d => d.Estado == "BE" && EsContingencia(d))));
        }
        w.El("Tmst", Hora.Formato(c.Tmst));
        w.WriteEndElement();

        foreach (var d in detalles)
        {
            w.WriteStartElement("ACKCFE_Det", Ns.Cfe);
            w.El("Nro_ordinal", XmlUtil.Numero(d.Ordinal));
            if (d.Tipo is { } tipo && TiposCfe.Existe(tipo))
                w.El("TipoCFE", XmlUtil.Numero(tipo));
            if (d.Serie is { } serie && SerieValida().IsMatch(serie))
                w.El("Serie", serie);
            if (d.Nro is >= 1 and <= 9_999_999)
                w.El("NroCFE", XmlUtil.Numero(d.Nro.Value));
            if (d.Fecha is { } fecha && fecha >= new DateOnly(2011, 10, 1) && fecha <= new DateOnly(2050, 12, 31))
                w.El("FechaCFE", Hora.Formato(fecha));
            if (d.Firma is { } firma && firma.Year is >= 2012 and <= 2050)
                w.El("TmstCFE", Hora.Formato(firma));
            w.El("Estado", d.Estado);
            if (d.Estado == "CE")
            {
                foreach (var motivo in d.Motivos.Take(10))
                    Motivo(w, "ObsCFC", motivo, 200);
            }
            else
            {
                // El ACKCFE de un receptor admite hasta 10 motivos por CFE; el de la DGI, 30.
                foreach (var motivo in d.Motivos.Take(deLaDgi ? 30 : 10))
                    Motivo(w, "MotivosRechazoCF", motivo, 500);
            }
            w.WriteEndElement();
        }
        w.WriteEndElement();
    }, firmante);

    internal static string Reporte(Caratula c, string estado, IReadOnlyList<Motivo> motivos, X509Certificate2 firmante) => Firmar(w =>
    {
        Inicio(w, "ACKRepDiario");
        w.WriteStartElement("Caratula", Ns.Cfe);
        w.El("RUCReceptor", c.RutReceptor);
        w.El("RUCEmisor", Rut(c.RutEmisor));
        w.El("NomArch", XmlUtil.Recortar(c.NombreArchivo, 40));
        w.El("FecHRecibido", Hora.Formato(c.Recibido));
        w.El("IDEmisor", XmlUtil.Numero(Id(c.IdEmisor)));
        w.El("IDReceptor", XmlUtil.Numero(c.IdReceptor));
        w.El("Tmst", Hora.Formato(c.Tmst));
        w.WriteEndElement();
        w.WriteStartElement("Detalle", Ns.Cfe);
        w.El("Estado", estado);
        foreach (var motivo in motivos.Take(30))
            Motivo(w, "MotivosRechazo", motivo, 500);
        w.WriteEndElement();
        w.WriteEndElement();
    }, firmante);

    private static bool EsContingencia(DetalleCfe d) => d.Tipo is { } t && TiposCfe.EsContingencia(t);

    private static void Inicio(XmlWriter w, string raiz)
    {
        w.WriteStartDocument();
        w.WriteStartElement(raiz, Ns.Cfe);
        w.WriteAttributeString("version", "1.0");
    }

    private static void Motivo(XmlWriter w, string elemento, Motivo motivo, int maximoDetalle)
    {
        w.WriteStartElement(elemento, Ns.Cfe);
        // Las observaciones de contingencia usan el código C equivalente.
        var (codigo, glosa) = elemento == "ObsCFC" && Motivos.Contingencia.TryGetValue(motivo.Codigo, out var obs) ? obs : (motivo.Codigo, motivo.Glosa);
        w.El("Motivo", codigo);
        w.El("Glosa", glosa);
        if (!string.IsNullOrEmpty(motivo.Detalle))
            w.El("Detalle", XmlUtil.Recortar(motivo.Detalle, maximoDetalle));
        w.WriteEndElement();
    }

    /// <summary>Un RUT válido para <c>RUCType</c> (12 dígitos); si el envío no lo trae, ceros.</summary>
    private static string Rut(string? rut) => rut is { Length: 12 } && rut.All(char.IsAsciiDigit) ? rut : "000000000000";

    private static long Id(long id) => id is >= 0 and <= 9_999_999_999 ? id : 0;

    private static string Firmar(Action<XmlWriter> escribir, X509Certificate2 firmante) =>
        Firmas.FirmarEnveloped(XmlUtil.Escribir(escribir), firmante);

    [GeneratedRegex("^([A-Z]{1,2}|[1-9][A-Z])$")]
    private static partial Regex SerieValida();
}
