using System.Reflection;
using System.Xml;
using System.Xml.Schema;

namespace DgiSimulador.Xml;

/// <summary>
/// Los XSD oficiales de la DGI (incluidos como recursos) y la validación contra ellos. Cada documento raíz tiene su
/// propio conjunto: <c>ACKCFE_Partes.xsd</c> redefine <c>ACKCFE</c> y la anulación comparte tipos con él.
/// </summary>
internal static partial class Esquemas
{
    private static readonly Lazy<XmlSchemaSet> Sobre = new(() => Compilar("EnvioCFE.xsd"));
    private static readonly Lazy<XmlSchemaSet> SobreEntreEmpresas = new(() => Compilar("EnvioCFE_entreEmpresas.xsd"));
    private static readonly Lazy<XmlSchemaSet> Reporte = new(() => Compilar("ReporteDiarioCFE.xsd"));
    private static readonly Lazy<XmlSchemaSet> Cfe = new(() => Compilar("CFEDGI.xsd"));
    private static readonly Lazy<XmlSchemaSet> AcusesDgi = new(() => Compilar("ACKSobre.xsd", "ACKCFE_DGI.xsd", "ACKRepDiario.xsd"));
    private static readonly Lazy<XmlSchemaSet> AcuseCfePartes = new(() => Compilar("ACKCFE_Partes.xsd"));
    private static readonly Lazy<XmlSchemaSet> Anulacion = new(() => Compilar("CFEs_Anulados_x_RechazoDGI.xsd"));

    /// <summary>Documentos que se pueden validar.</summary>
    internal enum Documento { Sobre, SobreEntreEmpresas, Reporte, Cfe, AcuseDgi, AcuseCfePartes, Anulacion }

    /// <summary>Un error de validación y el número de CFE del sobre en el que ocurrió (0: fuera de los CFE).</summary>
    internal sealed record Error(int Ordinal, string Mensaje);

    /// <summary>
    /// Valida <paramref name="xml"/>. En un sobre, cada error lleva el ordinal del CFE en el que está (el
    /// elemento <c>CFE</c> o <c>CFE_Adenda</c> hijo de la raíz), para rechazar ese CFE y no el sobre.
    /// </summary>
    internal static List<Error> Validar(string xml, Documento documento)
    {
        var errores = new List<Error>();
        var settings = new XmlReaderSettings
        {
            ValidationType = ValidationType.Schema,
            Schemas = Conjunto(documento),
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
        };
        var ordinal = 0;
        var enCfe = false;
        settings.ValidationEventHandler += (_, e) =>
        {
            if (!EsLimiteDelValidador(e.Message))
                errores.Add(new Error(enCfe ? ordinal : 0, $"{e.Message} (línea {e.Exception?.LineNumber}, columna {e.Exception?.LinePosition})"));
        };

        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            while (reader.Read())
            {
                if (reader.Depth != 1)
                    continue;
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName is "CFE" or "CFE_Adenda")
                {
                    ordinal++;
                    enCfe = !reader.IsEmptyElement;
                }
                else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName is "CFE" or "CFE_Adenda")
                {
                    enCfe = false;
                }
            }
        }
        catch (XmlException ex)
        {
            errores.Add(new Error(0, ex.Message));
        }
        return errores;
    }

    /// <summary>
    /// El validador de .NET guarda <c>xs:integer</c> en un <c>decimal</c> (29 dígitos) y rechaza enteros más largos, que
    /// el XSD admite: el número de serie de un certificado (<c>ds:X509SerialNumber</c>) suele tener 38 dígitos. Ese
    /// error es del validador, no del documento.
    /// </summary>
    private static bool EsLimiteDelValidador(string mensaje) =>
        mensaje.Contains("X509SerialNumber", StringComparison.Ordinal)
        && EnteroLargo().Match(mensaje) is { Success: true } m && m.Groups[1].Value.Length > 28;

    [System.Text.RegularExpressions.GeneratedRegex("The value '(-?[0-9]+)' is invalid according to its datatype 'http://www.w3.org/2001/XMLSchema:integer'")]
    private static partial System.Text.RegularExpressions.Regex EnteroLargo();

    private static XmlSchemaSet Conjunto(Documento documento) => documento switch
    {
        Documento.Sobre => Sobre.Value,
        Documento.SobreEntreEmpresas => SobreEntreEmpresas.Value,
        Documento.Reporte => Reporte.Value,
        Documento.Cfe => Cfe.Value,
        Documento.AcuseDgi => AcusesDgi.Value,
        Documento.AcuseCfePartes => AcuseCfePartes.Value,
        Documento.Anulacion => Anulacion.Value,
        _ => throw new ArgumentOutOfRangeException(nameof(documento)),
    };

    private static XmlSchemaSet Compilar(params string[] archivos)
    {
        var resolver = new ResolverRecursos();
        var set = new XmlSchemaSet { XmlResolver = resolver };
        foreach (var archivo in archivos)
        {
            using var stream = Abrir("Esquemas/" + archivo);
            // xmldsig y xenc traen un DTD interno sin entidades que se usen: se ignora.
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = resolver },
                ResolverRecursos.Base + archivo);
            set.Add(null, reader);
        }
        set.Compile();
        return set;
    }

    /// <summary>Un recurso incluido en el ensamblado (XSD, WSDL o el set de pruebas).</summary>
    internal static Stream Abrir(string nombre) =>
        Assembly.GetExecutingAssembly().GetManifestResourceStream(nombre)
        ?? throw new FileNotFoundException($"No se encontró el recurso {nombre}.");

    internal static string Leer(string nombre)
    {
        using var lector = new StreamReader(Abrir(nombre));
        return lector.ReadToEnd();
    }

    internal static bool Existe(string nombre) => Assembly.GetExecutingAssembly().GetManifestResourceInfo(nombre) is not null;

    /// <summary>Resuelve los <c>include</c> e <c>import</c> de los XSD contra los recursos; nada sale a la red.</summary>
    private sealed class ResolverRecursos : XmlResolver
    {
        internal const string Base = "recurso:///";

        public override Uri ResolveUri(Uri? baseUri, string? relativeUri) =>
            new(Base + Path.GetFileName(relativeUri ?? ""));

        public override object? GetEntity(Uri absoluteUri, string? role, Type? ofObjectToReturn)
        {
            var archivo = Path.GetFileName(absoluteUri.AbsolutePath);
            // El DTD externo que declaran xmldsig y xenc no se necesita.
            return archivo.EndsWith(".dtd", StringComparison.OrdinalIgnoreCase) ? new MemoryStream() : Abrir("Esquemas/" + archivo);
        }
    }
}
