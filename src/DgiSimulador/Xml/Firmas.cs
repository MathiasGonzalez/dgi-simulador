using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Xml;

namespace DgiSimulador.Xml;

/// <summary>Resultado de verificar una firma: el certificado del firmante, o el motivo por el que no es válida.</summary>
internal sealed record Firma(X509Certificate2? Certificado, string? Error)
{
    internal bool Valida => Error is null && Certificado is not null;

    internal static Firma Invalida(string error, X509Certificate2? certificado = null) => new(certificado, error);
}

/// <summary>
/// Firmas XML: la enveloped de los CFE, reportes y acuses (referencia al documento completo, <c>URI=""</c>) y la
/// de WS-Security del Body de los mensajes SOAP. Solo se aceptan RSA con SHA-1 o SHA-256, que es lo que admite la DGI.
/// </summary>
internal static class Firmas
{
    private static readonly HashSet<string> AlgoritmosFirma = [SignedXml.XmlDsigRSASHA1Url, SignedXml.XmlDsigRSASHA256Url];

    /// <summary>
    /// Verifica la firma enveloped de <paramref name="elemento"/> tal como se firmó: como documento propio, con los
    /// namespaces que tenía en el documento que lo contiene.
    /// </summary>
    internal static Firma VerificarEnveloped(XmlElement elemento)
    {
        XmlDocument doc;
        try
        {
            doc = XmlUtil.Cargar(elemento.OuterXml);
        }
        catch (XmlException ex)
        {
            return Firma.Invalida($"No se pudo leer el documento firmado: {ex.Message}");
        }

        var raiz = doc.DocumentElement!;
        var firma = raiz.Hijos("Signature").FirstOrDefault(e => e.NamespaceURI == Ns.Ds);
        if (firma is null)
            return Firma.Invalida("No tiene ds:Signature como hijo de la raíz.");

        var signedXml = new SignedXml(doc);
        try
        {
            signedXml.LoadXml(firma);
        }
        catch (CryptographicException ex)
        {
            return Firma.Invalida($"La firma no se puede leer: {ex.Message}");
        }

        if (!AlgoritmosFirma.Contains(signedXml.SignatureMethod ?? ""))
            return Firma.Invalida($"Algoritmo de firma no admitido: {signedXml.SignatureMethod}.");
        var referencias = signedXml.SignedInfo!.References.OfType<Reference>().ToList();
        if (referencias.Count != 1 || referencias[0].Uri != "")
            return Firma.Invalida("La firma tiene que tener una sola referencia al documento completo (URI=\"\").");
        var transformaciones = referencias[0].TransformChain;
        if (!Enumerable.Range(0, transformaciones.Count).Any(i => transformaciones[i] is XmlDsigEnvelopedSignatureTransform))
            return Firma.Invalida("La referencia no tiene la transformación enveloped-signature.");

        var certificado = CertificadoDe(firma);
        if (certificado is null)
            return Firma.Invalida("La firma no trae el certificado (KeyInfo/X509Data/X509Certificate).");

        try
        {
            return signedXml.CheckSignature(certificado, verifySignatureOnly: true)
                ? new Firma(certificado, null)
                : Firma.Invalida("El valor de la firma o el digest no coinciden con el documento.", certificado);
        }
        catch (CryptographicException ex)
        {
            return Firma.Invalida($"No se pudo verificar la firma: {ex.Message}", certificado);
        }
    }

    /// <summary>
    /// Firma <paramref name="doc"/> con una firma enveloped (c14n, RSA-SHA256) que agrega como último hijo de la
    /// raíz, con el certificado en <c>KeyInfo</c>, como los documentos de la DGI.
    /// </summary>
    internal static void FirmarEnveloped(XmlDocument doc, X509Certificate2 certificado)
    {
        using var rsa = certificado.GetRSAPrivateKey() ?? throw new InvalidOperationException("El certificado no tiene clave privada RSA.");
        var signedXml = new SignedXml(doc) { SigningKey = rsa };
        signedXml.SignedInfo!.CanonicalizationMethod = SignedXml.XmlDsigC14NTransformUrl;
        signedXml.SignedInfo.SignatureMethod = SignedXml.XmlDsigRSASHA256Url;

        var referencia = new Reference("") { DigestMethod = SignedXml.XmlDsigSHA256Url };
        referencia.AddTransform(new XmlDsigEnvelopedSignatureTransform());
        referencia.AddTransform(new XmlDsigC14NTransform());
        signedXml.AddReference(referencia);

        var keyInfo = new KeyInfo();
        keyInfo.AddClause(new KeyInfoX509Data(certificado));
        signedXml.KeyInfo = keyInfo;

        signedXml.ComputeSignature();
        doc.DocumentElement!.AppendChild(doc.ImportNode(signedXml.GetXml(), true));
    }

    /// <summary>Firma un documento escrito como texto y lo devuelve con la declaración XML.</summary>
    internal static string FirmarEnveloped(string xml, X509Certificate2 certificado)
    {
        var doc = XmlUtil.Cargar(xml, preservarEspacios: false);
        FirmarEnveloped(doc, certificado);
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" + doc.DocumentElement!.OuterXml;
    }

    /// <summary>
    /// Verifica la firma WS-Security de un mensaje SOAP: el <c>BinarySecurityToken</c> con el certificado y una
    /// firma cuya referencia es el <c>Body</c> (por su <c>wsu:Id</c>).
    /// </summary>
    internal static Firma VerificarWsSecurity(XmlDocument envelope)
    {
        var raiz = envelope.DocumentElement;
        var header = raiz.Hijos("Header").FirstOrDefault(e => e.NamespaceURI == Ns.Soap);
        var body = raiz.Hijos("Body").FirstOrDefault(e => e.NamespaceURI == Ns.Soap);
        var security = header.Hijos("Security").FirstOrDefault(e => e.NamespaceURI == Ns.Wsse);
        if (body is null)
            return Firma.Invalida("El mensaje no tiene soap:Body.");
        if (security is null)
            return Firma.Invalida("El mensaje no tiene el encabezado wsse:Security.");

        var token = security.Hijos("BinarySecurityToken").FirstOrDefault(e => e.NamespaceURI == Ns.Wsse);
        if (token is null)
            return Firma.Invalida("wsse:Security no tiene BinarySecurityToken.");
        X509Certificate2 certificado;
        try
        {
            certificado = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(token.InnerText.Trim()));
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return Firma.Invalida($"El BinarySecurityToken no es un certificado X.509: {ex.Message}");
        }

        var firma = security.Hijos("Signature").FirstOrDefault(e => e.NamespaceURI == Ns.Ds);
        if (firma is null)
            return Firma.Invalida("wsse:Security no tiene ds:Signature.", certificado);

        var signedXml = new SignedXmlWsu(envelope);
        try
        {
            signedXml.LoadXml(firma);
        }
        catch (CryptographicException ex)
        {
            return Firma.Invalida($"La firma WS-Security no se puede leer: {ex.Message}", certificado);
        }
        if (!AlgoritmosFirma.Contains(signedXml.SignatureMethod ?? ""))
            return Firma.Invalida($"Algoritmo de firma no admitido: {signedXml.SignatureMethod}.", certificado);

        var referencias = signedXml.SignedInfo!.References.OfType<Reference>().ToList();
        var firmaElBody = referencias.Any(r => r.Uri is ['#', .. var id] && ReferenceEquals(signedXml.GetIdElement(envelope, id), body));
        if (!firmaElBody)
            return Firma.Invalida("La firma WS-Security no referencia al soap:Body.", certificado);

        try
        {
            return signedXml.CheckSignature(certificado, verifySignatureOnly: true)
                ? new Firma(certificado, null)
                : Firma.Invalida("La firma WS-Security no coincide con el Body.", certificado);
        }
        catch (CryptographicException ex)
        {
            return Firma.Invalida($"No se pudo verificar la firma WS-Security: {ex.Message}", certificado);
        }
    }

    /// <summary>
    /// Arma un mensaje SOAP 1.1 con <paramref name="contenido"/> en el Body y lo firma con WS-Security (exc-c14n,
    /// RSA-SHA256), para las llamadas del receptor simulado al servicio de intercambio de un emisor.
    /// </summary>
    internal static string CrearSoapFirmado(Func<XmlDocument, XmlElement> contenido, X509Certificate2 certificado)
    {
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.AppendChild(doc.CreateXmlDeclaration("1.0", "UTF-8", null));
        var envelope = doc.CreateElement("soapenv", "Envelope", Ns.Soap);
        envelope.SetAttribute("xmlns:dgi", Ns.Dgi);
        doc.AppendChild(envelope);
        var header = doc.CreateElement("soapenv", "Header", Ns.Soap);
        envelope.AppendChild(header);

        var body = doc.CreateElement("soapenv", "Body", Ns.Soap);
        body.SetAttribute("xmlns:wsu", Ns.Wsu);
        var idBody = doc.CreateAttribute("wsu", "Id", Ns.Wsu);
        idBody.Value = "Body";
        body.SetAttributeNode(idBody);
        body.AppendChild(contenido(doc));
        envelope.AppendChild(body);

        var security = doc.CreateElement("wsse", "Security", Ns.Wsse);
        var entender = doc.CreateAttribute("soapenv", "mustUnderstand", Ns.Soap);
        entender.Value = "1";
        security.SetAttributeNode(entender);
        header.AppendChild(security);

        var token = doc.CreateElement("wsse", "BinarySecurityToken", Ns.Wsse);
        token.SetAttribute("EncodingType", "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary");
        token.SetAttribute("ValueType", "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-x509-token-profile-1.0#X509v3");
        token.SetAttribute("xmlns:wsu", Ns.Wsu);
        var idToken = doc.CreateAttribute("wsu", "Id", Ns.Wsu);
        idToken.Value = "X509Token";
        token.SetAttributeNode(idToken);
        token.InnerText = Convert.ToBase64String(certificado.RawData);
        security.AppendChild(token);

        using var rsa = certificado.GetRSAPrivateKey() ?? throw new InvalidOperationException("El certificado no tiene clave privada RSA.");
        var signedXml = new SignedXmlWsu(doc) { SigningKey = rsa };
        signedXml.SignedInfo!.CanonicalizationMethod = SignedXml.XmlDsigExcC14NTransformUrl;
        signedXml.SignedInfo.SignatureMethod = SignedXml.XmlDsigRSASHA256Url;
        var referencia = new Reference("#Body") { DigestMethod = SignedXml.XmlDsigSHA256Url };
        referencia.AddTransform(new XmlDsigExcC14NTransform());
        signedXml.AddReference(referencia);

        var str = doc.CreateElement("wsse", "SecurityTokenReference", Ns.Wsse);
        var refToken = doc.CreateElement("wsse", "Reference", Ns.Wsse);
        refToken.SetAttribute("URI", "#X509Token");
        refToken.SetAttribute("ValueType", "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-x509-token-profile-1.0#X509v3");
        str.AppendChild(refToken);
        var keyInfo = new KeyInfo();
        keyInfo.AddClause(new KeyInfoNode(str));
        signedXml.KeyInfo = keyInfo;

        signedXml.ComputeSignature();
        security.AppendChild(doc.ImportNode(signedXml.GetXml(), true));
        return doc.OuterXml;
    }

    /// <summary>El certificado de <c>KeyInfo/X509Data</c> de una firma (el primero que traiga).</summary>
    internal static X509Certificate2? CertificadoDe(XmlElement firma)
    {
        var nodo = firma.BuscarTodos("X509Certificate").FirstOrDefault(e => e.NamespaceURI == Ns.Ds);
        if (nodo is null)
            return null;
        try
        {
            return X509CertificateLoader.LoadCertificate(Convert.FromBase64String(nodo.InnerText.Trim()));
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return null;
        }
    }

    /// <summary><see cref="SignedXml"/> que resuelve las referencias por <c>wsu:Id</c>, como usa WS-Security.</summary>
    private sealed class SignedXmlWsu(XmlDocument doc) : SignedXml(doc)
    {
        public override XmlElement? GetIdElement(XmlDocument? document, string idValue)
        {
            var porDefecto = base.GetIdElement(document, idValue);
            if (porDefecto is not null || document is null)
                return porDefecto;
            return document.SelectNodes("//*[@*[local-name()='Id']]")!.OfType<XmlElement>()
                .FirstOrDefault(e => e.GetAttribute("Id", Ns.Wsu) == idValue);
        }
    }
}
