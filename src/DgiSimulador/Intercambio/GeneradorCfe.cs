using System.Security.Cryptography.X509Certificates;
using System.Xml;
using DgiSimulador.Estado;
using DgiSimulador.Recepcion;
using DgiSimulador.Xml;

namespace DgiSimulador.Intercambio;

/// <summary>Receptor de un CFE generado.</summary>
internal sealed record ReceptorCfe(string TipoDocumento, string Pais, string Documento, string RazonSocial, string Direccion = "Av. 18 de Julio 1000", string Ciudad = "Montevideo");

/// <summary>Línea de un CFE generado.</summary>
internal sealed record LineaCfe(string Nombre, decimal Cantidad, decimal Precio, int Indicador = 3);

/// <summary>Referencia de una nota: a un CFE (tipo, serie y número) o global.</summary>
internal sealed record ReferenciaCfe(int? Tipo, string? Serie, long? Nro, string? Razon = null)
{
    internal static ReferenciaCfe Global(string razon) => new(null, null, null, razon);
}

/// <summary>
/// Genera e-Tickets y e-Facturas (y sus notas y contingencias) válidos para el XSD, con los totales calculados, para
/// las empresas que simula el simulador y para las pruebas. <see cref="Modificar"/> permite romperlos antes de firmar.
/// </summary>
internal sealed record GeneradorCfe
{
    internal int Tipo { get; init; } = 111;
    internal string Serie { get; init; } = "A";
    internal long Nro { get; init; } = 1;
    internal DateOnly Fecha { get; init; }
    internal DateTimeOffset Firma { get; init; }
    internal required string RutEmisor { get; init; }
    internal string RazonSocialEmisor { get; init; } = "Empresa simulada";
    internal ReceptorCfe? Receptor { get; init; }
    internal string Moneda { get; init; } = "UYU";
    internal decimal? TipoCambio { get; init; }
    internal IReadOnlyList<LineaCfe> Lineas { get; init; } = [new("Servicio de prueba", 1, 100m)];
    internal IReadOnlyList<ReferenciaCfe> Referencias { get; init; } = [];
    internal required Cae Cae { get; init; }

    /// <summary>Cambio sobre el XML ya armado y antes de firmar (para generar CFE inválidos).</summary>
    internal Action<XmlDocument>? Modificar { get; init; }

    internal const decimal TasaMinima = 10m;
    internal const decimal TasaBasica = 22m;

    /// <summary>El CFE firmado con <paramref name="certificado"/>, como documento propio (con la declaración XML).</summary>
    internal string Firmar(X509Certificate2 certificado)
    {
        var doc = XmlUtil.Cargar(Xml(), preservarEspacios: false);
        Modificar?.Invoke(doc);
        Firmas.FirmarEnveloped(doc, certificado);
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" + doc.DocumentElement!.OuterXml;
    }

    /// <summary>El CFE sin firmar.</summary>
    internal string Xml()
    {
        var familia = TiposCfe.FamiliaDe(Tipo) ?? throw new ArgumentException($"Tipo {Tipo} desconocido.");
        if (familia is not (Familia.Ticket or Familia.Factura))
            throw new ArgumentException("El generador arma e-Tickets y e-Facturas.");

        var montos = Lineas.Select(l => (Linea: l, Monto: ReglasFormato.Redondear(l.Cantidad * l.Precio))).ToList();
        decimal Suma(int indicador) => montos.Where(m => m.Linea.Indicador == indicador).Sum(m => m.Monto);
        var noGravado = Suma(1);
        var netoMinima = Suma(2);
        var netoBasica = Suma(3);
        var ivaMinima = ReglasFormato.Redondear(netoMinima * TasaMinima / 100m);
        var ivaBasica = ReglasFormato.Redondear(netoBasica * TasaBasica / 100m);
        var total = noGravado + netoMinima + netoBasica + ivaMinima + ivaBasica;

        return XmlUtil.Escribir(w =>
        {
            w.WriteStartDocument();
            w.WriteStartElement("CFE", Ns.Cfe);
            w.WriteAttributeString("version", "1.0");
            w.WriteStartElement(TiposCfe.Raiz(familia), Ns.Cfe);
            w.El("TmstFirma", Hora.Formato(Firma));

            w.WriteStartElement("Encabezado", Ns.Cfe);
            w.WriteStartElement("IdDoc", Ns.Cfe);
            w.El("TipoCFE", XmlUtil.Numero(Tipo));
            w.El("Serie", Serie);
            w.El("Nro", XmlUtil.Numero(Nro));
            w.El("FchEmis", Hora.Formato(Fecha));
            w.El("FmaPago", "1");
            w.WriteEndElement();

            w.WriteStartElement("Emisor", Ns.Cfe);
            w.El("RUCEmisor", RutEmisor);
            w.El("RznSoc", RazonSocialEmisor);
            w.El("CdgDGISucur", "1");
            w.El("DomFiscal", "Rincón 500");
            w.El("Ciudad", "Montevideo");
            w.El("Departamento", "Montevideo");
            w.WriteEndElement();

            if (Receptor is { } r)
            {
                w.WriteStartElement("Receptor", Ns.Cfe);
                w.El("TipoDocRecep", r.TipoDocumento);
                w.El("CodPaisRecep", r.Pais);
                w.El(r.Pais == "UY" || r.TipoDocumento is "2" or "3" ? "DocRecep" : "DocRecepExt", r.Documento);
                w.El("RznSocRecep", r.RazonSocial);
                w.El("DirRecep", r.Direccion);
                w.El("CiudadRecep", r.Ciudad);
                w.WriteEndElement();
            }

            w.WriteStartElement("Totales", Ns.Cfe);
            w.El("TpoMoneda", Moneda);
            if (TipoCambio is { } tc)
                w.El("TpoCambio", tc.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
            if (noGravado != 0)
                w.El("MntNoGrv", XmlUtil.Numero(noGravado));
            if (netoMinima != 0)
                w.El("MntNetoIvaTasaMin", XmlUtil.Numero(netoMinima));
            if (netoBasica != 0)
                w.El("MntNetoIVATasaBasica", XmlUtil.Numero(netoBasica));
            if (netoMinima != 0 || netoBasica != 0)
            {
                w.El("IVATasaMin", XmlUtil.Numero(TasaMinima));
                w.El("IVATasaBasica", XmlUtil.Numero(TasaBasica));
            }
            if (netoMinima != 0)
                w.El("MntIVATasaMin", XmlUtil.Numero(ivaMinima));
            if (netoBasica != 0)
                w.El("MntIVATasaBasica", XmlUtil.Numero(ivaBasica));
            w.El("MntTotal", XmlUtil.Numero(total));
            w.El("CantLinDet", XmlUtil.Numero(Lineas.Count));
            w.El("MntPagar", XmlUtil.Numero(total));
            w.WriteEndElement();
            w.WriteEndElement();

            w.WriteStartElement("Detalle", Ns.Cfe);
            var nro = 0;
            foreach (var (linea, monto) in montos)
            {
                w.WriteStartElement("Item", Ns.Cfe);
                w.El("NroLinDet", XmlUtil.Numero(++nro));
                w.El("IndFact", XmlUtil.Numero(linea.Indicador));
                w.El("NomItem", linea.Nombre);
                w.El("Cantidad", linea.Cantidad.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
                w.El("UniMed", "N/A");
                w.El("PrecioUnitario", linea.Precio.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture));
                w.El("MontoItem", XmlUtil.Numero(monto));
                w.WriteEndElement();
            }
            w.WriteEndElement();

            if (Referencias.Count > 0)
            {
                w.WriteStartElement("Referencia", Ns.Cfe);
                var nroRef = 0;
                foreach (var referencia in Referencias)
                {
                    w.WriteStartElement("Referencia", Ns.Cfe);
                    w.El("NroLinRef", XmlUtil.Numero(++nroRef));
                    if (referencia.Tipo is null)
                        w.El("IndGlobal", "1");
                    else
                    {
                        w.El("TpoDocRef", XmlUtil.Numero(referencia.Tipo.Value));
                        w.El("Serie", referencia.Serie!);
                        w.El("NroCFERef", XmlUtil.Numero(referencia.Nro!.Value));
                    }
                    if (referencia.Razon is { } razon)
                        w.El("RazonRef", razon);
                    w.WriteEndElement();
                }
                w.WriteEndElement();
            }

            w.WriteStartElement("CAEData", Ns.Cfe);
            w.El("CAE_ID", XmlUtil.Numero(Cae.Id));
            w.El("DNro", XmlUtil.Numero(Cae.Desde));
            w.El("HNro", XmlUtil.Numero(Cae.Hasta));
            w.El("FecVenc", Hora.Formato(Cae.Vencimiento));
            if (Cae.Especial is { } especial)
                w.El("CAEEspecial", XmlUtil.Numero(especial));
            if (Cae.Causal is { } causal)
                w.El("CausalCAEEsp", XmlUtil.Numero(causal));
            w.WriteEndElement();

            w.WriteEndElement();
            w.WriteEndElement();
        });
    }
}

/// <summary>Arma los sobres con CFE ya firmados: a la DGI (<c>EnvioCFE</c>) y entre empresas (<c>EnvioCFE_entreEmpresas</c>).</summary>
internal static class Sobres
{
    internal static string ParaDgi(string rutEmisor, long idEmisor, DateTimeOffset fecha, X509Certificate2 certificado, IReadOnlyList<string> cfes) =>
        Armar("EnvioCFE", "214844360018", rutEmisor, idEmisor, fecha, certificado, cfes.Select(c => Contenido(c)).ToList());

    internal static string EntreEmpresas(string rutReceptor, string rutEmisor, long idEmisor, DateTimeOffset fecha, X509Certificate2 certificado,
        IReadOnlyList<(string Cfe, string? Adenda)> cfes) =>
        Armar("EnvioCFE_entreEmpresas", rutReceptor, rutEmisor, idEmisor, fecha, certificado,
            cfes.Select(c => "<CFE_Adenda>" + Contenido(c.Cfe) + (c.Adenda is null ? "" : $"<Adenda>{System.Security.SecurityElement.Escape(c.Adenda)}</Adenda>") + "</CFE_Adenda>").ToList());

    /// <summary>El CFE firmado sin la declaración XML, tal cual (los espacios son parte de la firma).</summary>
    private static string Contenido(string cfe) =>
        cfe.StartsWith("<?xml", StringComparison.Ordinal) ? cfe[(cfe.IndexOf("?>", StringComparison.Ordinal) + 2)..] : cfe;

    private static string Armar(string raiz, string rutReceptor, string rutEmisor, long idEmisor, DateTimeOffset fecha, X509Certificate2 certificado,
        IReadOnlyList<string> contenidos) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
        $"<{raiz} xmlns=\"{Ns.Cfe}\" version=\"1.0\"><Caratula version=\"1.0\">" +
        $"<RutReceptor>{rutReceptor}</RutReceptor><RUCEmisor>{rutEmisor}</RUCEmisor><Idemisor>{idEmisor}</Idemisor>" +
        $"<CantCFE>{contenidos.Count}</CantCFE><Fecha>{Hora.Formato(fecha)}</Fecha>" +
        $"<X509Certificate>{Convert.ToBase64String(certificado.RawData)}</X509Certificate></Caratula>" +
        string.Concat(contenidos) + $"</{raiz}>";
}
