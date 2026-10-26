using System.Globalization;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Xml;

namespace DgiSimulador.Xml;

/// <summary>Namespaces de los mensajes.</summary>
internal static class Ns
{
    internal const string Cfe = "http://cfe.dgi.gub.uy";
    internal const string Dgi = "http://dgi.gub.uy";
    internal const string Soap = "http://schemas.xmlsoap.org/soap/envelope/";
    internal const string Ds = SignedXml.XmlDsigNamespaceUrl;
    internal const string Wsse = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";
    internal const string Wsu = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd";
}

/// <summary>Lectura y escritura de XML: sin DTD ni recursos externos, que los mensajes vienen de terceros.</summary>
internal static class XmlUtil
{
    internal static XmlDocument Cargar(string xml, bool preservarEspacios = true)
    {
        var doc = new XmlDocument { PreserveWhitespace = preservarEspacios, XmlResolver = null };
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var reader = XmlReader.Create(new StringReader(xml), settings);
        doc.Load(reader);
        return doc;
    }

    /// <summary>Intenta cargar el XML; devuelve el error si no es XML bien formado.</summary>
    internal static bool TryCargar(string xml, out XmlDocument doc, out string? error)
    {
        try
        {
            doc = Cargar(xml);
            error = null;
            return true;
        }
        catch (XmlException ex)
        {
            doc = new XmlDocument();
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Hijo directo con ese nombre local.</summary>
    internal static XmlElement? Hijo(this XmlNode? padre, string nombre) =>
        padre?.ChildNodes.OfType<XmlElement>().FirstOrDefault(e => e.LocalName == nombre);

    internal static IEnumerable<XmlElement> Hijos(this XmlNode? padre, string nombre) =>
        padre?.ChildNodes.OfType<XmlElement>().Where(e => e.LocalName == nombre) ?? [];

    internal static IEnumerable<XmlElement> Hijos(this XmlNode? padre) => padre?.ChildNodes.OfType<XmlElement>() ?? [];

    /// <summary>Primer descendiente con ese nombre local.</summary>
    internal static XmlElement? Buscar(this XmlNode? nodo, string nombre) =>
        nodo?.SelectSingleNode($".//*[local-name()='{nombre}']") as XmlElement;

    internal static IEnumerable<XmlElement> BuscarTodos(this XmlNode? nodo, string nombre) =>
        nodo?.SelectNodes($".//*[local-name()='{nombre}']")?.OfType<XmlElement>() ?? [];

    /// <summary>Texto de un hijo directo (recortado), o <c>null</c> si no está.</summary>
    internal static string? Texto(this XmlNode? padre, string nombre) => padre.Hijo(nombre)?.InnerText.Trim();

    internal static long? Entero(string? texto) =>
        long.TryParse(texto, NumberStyles.Integer, CultureInfo.InvariantCulture, out var valor) ? valor : null;

    internal static decimal? Decimal(string? texto) =>
        decimal.TryParse(texto, NumberStyles.Number, CultureInfo.InvariantCulture, out var valor) ? valor : null;

    internal static string Numero(decimal valor) => valor.ToString("0.00", CultureInfo.InvariantCulture);

    internal static string Numero(long valor) => valor.ToString(CultureInfo.InvariantCulture);

    /// <summary>Escribe un documento con <see cref="XmlWriter"/> y devuelve el texto (UTF-8, con declaración).</summary>
    internal static string Escribir(Action<XmlWriter> escribir, bool indentar = false)
    {
        var sb = new StringBuilder();
        using (var w = XmlWriter.Create(new StringWriterUtf8(sb), new XmlWriterSettings { Indent = indentar, Encoding = Encoding.UTF8 }))
            escribir(w);
        return sb.ToString();
    }

    /// <summary>Elemento con texto en el namespace por defecto del escritor.</summary>
    internal static void El(this XmlWriter w, string nombre, string valor, string ns = Ns.Cfe) =>
        w.WriteElementString(nombre, ns, valor);

    internal static string Recortar(string texto, int maximo) => texto.Length <= maximo ? texto : texto[..maximo];

    private sealed class StringWriterUtf8(StringBuilder sb) : StringWriter(sb, CultureInfo.InvariantCulture)
    {
        public override Encoding Encoding => Encoding.UTF8;
    }
}
