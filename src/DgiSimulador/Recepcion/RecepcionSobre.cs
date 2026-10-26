using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml;
using DgiSimulador.Certificados;
using DgiSimulador.Estado;
using DgiSimulador.Soap;
using DgiSimulador.Xml;
using Microsoft.Extensions.Options;

namespace DgiSimulador.Recepcion;

/// <summary>
/// <c>EFACRECEPCIONSOBRE</c> y <c>EFACCONSULTARESTADOENVIO</c>: controla el sobre (S01 a S08) y responde
/// <c>ACKSobre</c>; si lo recibe (AS), controla cada CFE y deja el resultado para consultarlo desde <c>Fechahora</c>.
/// </summary>
internal sealed class RecepcionSobre(
    Almacen almacen, Autoridad autoridad, ControlCfe control, Fallas fallas, IOptions<OpcionesSimulador> opciones, TimeProvider reloj)
{
    private const string Operacion = "EFACRECEPCIONSOBRE";
    private static readonly Lock Cerrojo = new();
    private readonly OpcionesSimulador _opciones = opciones.Value;

    internal string Recibir(string xml, Firma envio)
    {
        lock (Cerrojo)
            return Procesar(xml, envio);
    }

    private string Procesar(string xml, Firma envio)
    {
        var ahora = reloj.GetUtcNow();
        var idReceptor = almacen.Siguiente("envios");
        var rutEnvio = envio.Certificado is { } certEnvio ? Autoridad.RutDe(certEnvio) : null;
        var motivos = new List<Motivo>();
        var rutEmisor = rutEnvio ?? "";
        long idEmisor = 0;
        var elementos = new List<XmlElement>();
        var firmas = new List<Firma>();
        var erroresPorCfe = new Dictionary<int, List<string>>();

        if (Encoding.UTF8.GetByteCount(xml) > _opciones.TamanoMaximoKb * 1024L)
            motivos.Add(Motivos.DeSobre("S07", $"El sobre tiene {Encoding.UTF8.GetByteCount(xml) / 1024} KB y el máximo configurado es {_opciones.TamanoMaximoKb} KB."));
        else if (!XmlUtil.TryCargar(xml, out var doc, out var error))
            motivos.Add(Motivos.DeSobre("S01", $"No es XML: {error}"));
        else if (doc.DocumentElement is not { LocalName: "EnvioCFE", NamespaceURI: Ns.Cfe } raiz)
            motivos.Add(Motivos.DeSobre("S01", $"La raíz es {doc.DocumentElement?.Name} y tiene que ser EnvioCFE del namespace {Ns.Cfe}."));
        else
        {
            var caratula = raiz.Hijo("Caratula");
            rutEmisor = caratula.Texto("RUCEmisor") ?? rutEmisor;
            idEmisor = XmlUtil.Entero(caratula.Texto("Idemisor")) ?? 0;
            elementos = [.. raiz.Hijos("CFE").Where(e => e.NamespaceURI == Ns.Cfe)];
            firmas = [.. elementos.Select(Firmas.VerificarEnveloped)];

            var errores = Esquemas.Validar(xml, Esquemas.Documento.Sobre);
            foreach (var e in errores.Where(e => e.Ordinal > 0))
                (erroresPorCfe.TryGetValue(e.Ordinal, out var lista) ? lista : erroresPorCfe[e.Ordinal] = []).Add(e.Mensaje);
            ControlarSobre(caratula, elementos, firmas, errores.Where(e => e.Ordinal == 0).Select(e => e.Mensaje).ToList(), rutEnvio, ahora, motivos);
        }

        if (motivos.Count == 0 && almacen.ExisteSobre(rutEmisor, idEmisor))
            motivos.Add(Motivos.DeSobre("S08", $"Ya se recibió el sobre {idEmisor} del RUT {rutEmisor}."));
        if (motivos.Count == 0 && fallas.Rechazo(Operacion, 'S') is { } forzado && Motivos.Sobre.ContainsKey(forzado))
            motivos.Add(Motivos.DeSobre(forzado, "Rechazo programado en el simulador (POST /admin/fallas)."));

        var estado = motivos.Count == 0 ? "AS" : "BS";
        string? token = null;
        DateTimeOffset? consultarDesde = null;
        if (estado == "AS")
        {
            token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
            var demora = fallas.Negocio(Operacion, "demora")?.Segundos ?? _opciones.DemoraConsultaSegundos;
            consultarDesde = ahora.AddSeconds(demora);
        }

        var caratulaAcuse = new Caratula(RutReceptorDe(xml), rutEmisor, almacen.Siguiente("respuestas"), NombreSobre(rutEmisor, ahora, idEmisor),
            ahora, idEmisor, idReceptor, ahora);
        var acuse = Acuses.Sobre(caratulaAcuse, elementos.Count, estado, motivos, token, consultarDesde, autoridad.Dgi);
        almacen.GuardarSobre(new SobreRecibido(idReceptor, rutEmisor, idEmisor, estado, motivos, token, consultarDesde, elementos.Count, ahora, xml, acuse));

        if (estado == "AS")
            ControlarCfes(idReceptor, rutEmisor, elementos, firmas, erroresPorCfe, ahora);
        return acuse;
    }

    private void ControlarSobre(XmlElement? caratula, List<XmlElement> elementos, List<Firma> firmas, List<string> erroresXsd, string? rutEnvio,
        DateTimeOffset ahora, List<Motivo> motivos)
    {
        if (erroresXsd.Count > 0)
            motivos.Add(Motivos.DeSobre("S04", erroresXsd[0] + (erroresXsd.Count > 1 ? $" (y {erroresXsd.Count - 1} errores más)" : "")));
        if (caratula is null)
            return;

        var rutReceptor = caratula.Texto("RutReceptor");
        if (rutReceptor is null || !_opciones.RutsDgiAceptados.Contains(rutReceptor))
            motivos.Add(Motivos.DeSobre("S04", $"RutReceptor es {rutReceptor} y el de la DGI es {string.Join(" o ", _opciones.RutsDgiAceptados)}."));
        var cantidad = XmlUtil.Entero(caratula.Texto("CantCFE"));
        if (cantidad != elementos.Count)
            motivos.Add(Motivos.DeSobre("S05", $"CantCFE indica {caratula.Texto("CantCFE")} y el sobre tiene {elementos.Count} CFE."));

        var rutEmisor = caratula.Texto("RUCEmisor");
        X509Certificate2? certificado = null;
        try
        {
            certificado = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(caratula.Texto("X509Certificate") ?? ""));
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            motivos.Add(Motivos.DeSobre("S03", "X509Certificate de la carátula no es un certificado X.509."));
        }
        if (certificado is not null)
        {
            var controlCertificado = autoridad.Controlar(certificado, ahora);
            if (!controlCertificado.Valido)
                motivos.Add(Motivos.DeSobre("S03", controlCertificado.Error));
            else if (controlCertificado.Rut != rutEmisor)
                motivos.Add(Motivos.DeSobre("S02", $"El certificado de la carátula es del RUT {controlCertificado.Rut} y RUCEmisor es {rutEmisor}."));
        }
        if (rutEnvio != rutEmisor)
            motivos.Add(Motivos.DeSobre("S02", $"El envío está firmado (WS-Security) por el RUT {rutEnvio ?? "(sin RUT)"} y RUCEmisor es {rutEmisor}."));
        var rutsCfe = elementos.Select(e => CfeLeido.Leer(e).RutEmisor).Where(r => r != rutEmisor).Distinct().ToList();
        if (rutsCfe.Count > 0)
            motivos.Add(Motivos.DeSobre("S02", $"Hay CFE del RUT {string.Join(", ", rutsCfe)} en un sobre de {rutEmisor}."));

        if (certificado is not null)
        {
            var distintos = firmas.Select((f, i) => (f, i)).Where(x => x.f.Certificado is { } c && c.Thumbprint != certificado.Thumbprint)
                .Select(x => x.i + 1).ToList();
            if (distintos.Count > 0)
                motivos.Add(Motivos.DeSobre("S06", $"Los CFE {string.Join(", ", distintos)} están firmados con otro certificado que el de la carátula."));
        }
    }

    private void ControlarCfes(long idReceptor, string rutEmisor, List<XmlElement> elementos, List<Firma> firmas, Dictionary<int, List<string>> errores,
        DateTimeOffset ahora)
    {
        var contexto = new ContextoSobre();
        var forzado = fallas.Rechazo(Operacion, 'E');
        for (var i = 0; i < elementos.Count; i++)
        {
            var cfe = CfeLeido.Leer(elementos[i]);
            var resultado = control.Controlar(cfe, firmas[i], errores.GetValueOrDefault(i + 1) ?? [], contexto, forzado);
            almacen.GuardarCfe(new CfeRegistrado(0, idReceptor, i + 1, rutEmisor, cfe.Tipo, cfe.Serie, cfe.Nro, cfe.FechaEmision, cfe.Firma,
                resultado.Estado, resultado.Motivos, resultado.Advertencias, cfe.Moneda, cfe.TipoCambio, cfe.Familia is null ? null : cfe.Neto,
                cfe.Familia is null ? null : cfe.Iva, cfe.Total, cfe.MontoInformado("MntTotRetenido"), cfe.RutReceptor, ahora, elementos[i].OuterXml));
        }
        RegistrarEnPadron(rutEmisor, elementos, ahora);
    }

    /// <summary>Un emisor que manda un sobre válido queda en el padrón (si no estaba), con la razón social de sus CFE.</summary>
    private void RegistrarEnPadron(string rut, List<XmlElement> elementos, DateTimeOffset ahora)
    {
        if (almacen.Emisor(rut) is not null)
            return;
        var razonSocial = elementos.Select(e => CfeLeido.Leer(e).Emisor.Texto("RznSoc")).FirstOrDefault(r => r is not null) ?? rut;
        almacen.GuardarEmisor(new Emisor(rut, razonSocial, null, null, null, Hora.Fecha(ahora), null, null, false, ahora));
    }

    /// <summary>
    /// <c>EFACCONSULTARESTADOENVIO</c>: el <c>ACKCFE</c> del sobre. Antes de <c>Fechahora</c> responde la carátula sin
    /// detalle (el formato no dice qué hace la DGI en ese caso; los productos lo tratan como "todavía no está").
    /// </summary>
    internal string ConsultarEnvio(string xml, Firma envio)
    {
        if (!XmlUtil.TryCargar(xml, out var doc, out var error))
            throw new FaultSoap($"La consulta no es XML: {error}");
        var raiz = doc.DocumentElement;
        if (XmlUtil.Entero(raiz.Buscar("IdReceptor")?.InnerText.Trim()) is not { } idReceptor || raiz.Buscar("Token")?.InnerText.Trim() is not { } token)
            throw new FaultSoap("La consulta tiene que tener IdReceptor y Token (ConsultaCFE).");
        var sobre = almacen.Sobre(idReceptor);
        if (sobre is null || sobre.Estado != "AS" || sobre.Token != token)
            throw new FaultSoap($"No hay un sobre recibido con IdReceptor {idReceptor} y ese Token.");
        if (envio.Certificado is { } c && Autoridad.RutDe(c) != sobre.Rut)
            throw new FaultSoap($"El sobre {idReceptor} es del RUT {sobre.Rut}.");

        var ahora = reloj.GetUtcNow();
        var detalles = ahora < sobre.ConsultarDesde
            ? []
            : almacen.CfesDeSobre(idReceptor)
                .Select(x => new DetalleCfe(x.Ordinal, x.Tipo, x.Serie, x.Nro, x.Fecha, x.Firma, x.Estado, x.Motivos)).ToList();
        var caratula = new Caratula(Autoridad.RutDgi, sobre.Rut, almacen.Siguiente("respuestas"), NombreSobre(sobre.Rut, sobre.Recibido, sobre.IdEmisor),
            sobre.Recibido, sobre.IdEmisor, sobre.IdReceptor, ahora);
        return Acuses.Cfes(caratula, sobre.Cantidad, detalles, deLaDgi: true, autoridad.Dgi);
    }

    /// <summary>El RUT de la DGI al que se dirigió el sobre, si es uno de los aceptados, para responder con ese.</summary>
    private string RutReceptorDe(string xml)
    {
        var rut = XmlUtil.TryCargar(xml, out var doc, out _) ? doc.DocumentElement.Hijo("Caratula").Texto("RutReceptor") : null;
        return rut is not null && _opciones.RutsDgiAceptados.Contains(rut) ? rut : Autoridad.RutDgi;
    }

    internal static string NombreSobre(string rut, DateTimeOffset fecha, long idEmisor) =>
        $"Sob_{rut}_{Hora.Uruguay(fecha):yyyyMMdd}_{idEmisor}";
}
