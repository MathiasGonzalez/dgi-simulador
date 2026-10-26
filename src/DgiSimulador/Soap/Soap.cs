using System.Xml;
using DgiSimulador.Xml;

namespace DgiSimulador.Soap;

/// <summary>Error que se responde como SOAP Fault (HTTP 500), como hace la DGI ante un pedido que no puede procesar.</summary>
internal sealed class FaultSoap(string mensaje, string codigo = "SOAP-ENV:Client") : Exception(mensaje)
{
    internal string Codigo { get; } = codigo;
}

/// <summary>Un pedido SOAP leído: el servicio y la operación (del elemento del Body), su contenido y la firma WS-Security.</summary>
internal sealed record PeticionSoap(string Servicio, string Operacion, XmlElement Contenido, Firma Firma)
{
    /// <summary>El documento de <c>Datain/xmlData</c> (texto o CDATA) de las operaciones de recepción e intercambio.</summary>
    internal string XmlData => Contenido.Hijo("Datain").Hijo("xmlData")?.InnerText
        ?? throw new FaultSoap($"{Servicio}.{Operacion}: falta Datain/xmlData.");
}

/// <summary>Lectura de los pedidos y armado de las respuestas SOAP 1.1 de los servicios de la DGI.</summary>
internal static class MensajeSoap
{
    internal static PeticionSoap Leer(string cuerpo)
    {
        if (!XmlUtil.TryCargar(cuerpo, out var doc, out var error))
            throw new FaultSoap($"El pedido no es XML: {error}");
        if (doc.DocumentElement is not { LocalName: "Envelope", NamespaceURI: Ns.Soap } envelope)
            throw new FaultSoap("El pedido no es un sobre SOAP 1.1 (soap:Envelope de http://schemas.xmlsoap.org/soap/envelope/).");
        var body = envelope.Hijos("Body").FirstOrDefault(e => e.NamespaceURI == Ns.Soap)
            ?? throw new FaultSoap("El pedido no tiene soap:Body.");
        var contenido = body.Hijos().FirstOrDefault() ?? throw new FaultSoap("El soap:Body está vacío.");
        if (contenido.NamespaceURI != Ns.Dgi)
            throw new FaultSoap($"El elemento {contenido.LocalName} no está en el namespace {Ns.Dgi}.");
        var punto = contenido.LocalName.LastIndexOf('.');
        if (punto <= 0)
            throw new FaultSoap($"No se reconoce la operación {contenido.LocalName}.");

        var firma = Firmas.VerificarWsSecurity(doc);
        if (!firma.Valida)
            throw new FaultSoap($"WS-Security: {firma.Error}", "wsse:FailedCheck");
        return new PeticionSoap(contenido.LocalName[..punto], contenido.LocalName[(punto + 1)..], contenido, firma);
    }

    /// <summary>Respuesta con el documento en <c>Dataout/xmlData</c> como CDATA.</summary>
    internal static string ConXmlData(PeticionSoap pedido, string xml) => Respuesta(pedido, w =>
    {
        w.WriteStartElement("Dataout", Ns.Dgi);
        w.WriteStartElement("xmlData", Ns.Dgi);
        // Un CDATA no puede contener "]]>": en ese caso el documento va como texto escapado, que también es válido.
        if (xml.Contains("]]>", StringComparison.Ordinal))
            w.WriteString(xml);
        else
            w.WriteCData(xml);
        w.WriteEndElement();
        w.WriteEndElement();
    });

    /// <summary>Respuesta <c>{Servicio}.{Operacion}Response</c> con el contenido que escribe <paramref name="contenido"/>.</summary>
    internal static string Respuesta(PeticionSoap pedido, Action<XmlWriter> contenido) => XmlUtil.Escribir(w =>
    {
        w.WriteStartDocument();
        w.WriteStartElement("SOAP-ENV", "Envelope", Ns.Soap);
        w.WriteStartElement("SOAP-ENV", "Body", Ns.Soap);
        w.WriteStartElement($"{pedido.Servicio}.{pedido.Operacion}Response", Ns.Dgi);
        contenido(w);
        w.WriteEndElement();
        w.WriteEndElement();
        w.WriteEndElement();
    });

    internal static string Fault(string mensaje, string codigo = "SOAP-ENV:Server") => XmlUtil.Escribir(w =>
    {
        w.WriteStartDocument();
        w.WriteStartElement("SOAP-ENV", "Envelope", Ns.Soap);
        w.WriteStartElement("SOAP-ENV", "Body", Ns.Soap);
        w.WriteStartElement("SOAP-ENV", "Fault", Ns.Soap);
        w.WriteElementString("faultcode", codigo);
        w.WriteElementString("faultstring", mensaje);
        w.WriteEndElement();
        w.WriteEndElement();
        w.WriteEndElement();
    });
}
