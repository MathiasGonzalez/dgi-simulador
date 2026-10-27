using System.Xml;
using DgiSimulador.Recepcion;
using DgiSimulador.Xml;

namespace DgiSimulador.Tests;

/// <summary>
/// Los ejemplos que publica la DGI (Documentos de interés: "Ejemplo de Sobre" y "Ejemplo de Reporte"), sin modificar.
/// Lo que la DGI firmó y armó tiene que pasar los controles de formato y de firma del simulador. Su certificado es
/// de prueba y está vencido, así que no se controla la confianza.
/// </summary>
public sealed class EjemplosDgiTests
{
    private static XmlDocument Cargar(string archivo)
    {
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.Load(Path.Combine(AppContext.BaseDirectory, "EjemplosDgi", archivo));
        return doc;
    }

    [Fact]
    public void SobreDeEjemplo_ValidaContraElXsd() =>
        Assert.Empty(Esquemas.Validar(Cargar("sobre.xml").DocumentElement!.OuterXml, Esquemas.Documento.Sobre));

    [Fact]
    public void SobreDeEjemplo_LasFirmasSeVerificanConElCertificadoDeLaCaratula()
    {
        // Las firmas de los CFE traen solo X509IssuerSerial: el certificado va en la carátula.
        var raiz = Cargar("sobre.xml").DocumentElement!;
        var certificado = Firmas.Leer(raiz.Hijo("Caratula").Texto("X509Certificate"));
        var cfes = raiz.Hijos("CFE").ToList();
        Assert.Equal(8, cfes.Count);
        foreach (var cfe in cfes)
        {
            Assert.Null(Firmas.CertificadoDe(cfe.Hijos("Signature").Single()));
            var firma = Firmas.VerificarEnveloped(cfe, certificado);
            Assert.True(firma.Valida, $"{CfeLeido.Leer(cfe).Identificacion}: {firma.Error}");
        }
    }

    [Fact]
    public void SobreDeEjemplo_CumpleLasReglasDelFormato()
    {
        foreach (var cfe in Cargar("sobre.xml").DocumentElement!.Hijos("CFE"))
        {
            var leido = CfeLeido.Leer(cfe);
            var hallazgos = new ReglasFormato(new OpcionesSimulador()).Controlar(leido);
            Assert.True(hallazgos.Count == 0, $"{leido.Identificacion}: {string.Join("; ", hallazgos.Select(h => $"{h.Codigo} {h.Detalle}"))}");
        }
    }

    [Fact]
    public void ReporteDeEjemplo_ValidaContraElXsdYSuFirmaEsCorrecta()
    {
        var raiz = Cargar("reporte.xml").DocumentElement!;
        Assert.Empty(Esquemas.Validar(raiz.OuterXml, Esquemas.Documento.Reporte));
        var firma = Firmas.VerificarEnveloped(raiz);
        Assert.True(firma.Valida, firma.Error);
    }
}
