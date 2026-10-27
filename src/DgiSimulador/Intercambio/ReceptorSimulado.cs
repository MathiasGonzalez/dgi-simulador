using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml;
using DgiSimulador.Certificados;
using DgiSimulador.Estado;
using DgiSimulador.Recepcion;
using DgiSimulador.Soap;
using DgiSimulador.Xml;
using Microsoft.Extensions.Options;

namespace DgiSimulador.Intercambio;

/// <summary>Un archivo del intercambio por correo: su nombre oficial y el XML.</summary>
public sealed record ArchivoIntercambio(string Nombre, string Xml);

/// <summary>
/// Una empresa simulada en el rol de receptor electrónico: recibe sobres <c>EnvioCFE_entreEmpresas</c> del producto
/// (por su servicio web o por correo), los controla del lado receptor (S01 a S07 y S20; E02 a E07 por CFE) y responde
/// <c>ACKSobre</c> y <c>ACKCFE</c> firmados. También recibe las respuestas del producto a los sobres que le mandó una
/// empresa simulada y sus comunicaciones de anulación.
/// </summary>
internal sealed class ReceptorSimulado(
    Almacen almacen, Autoridad autoridad, Fallas fallas, ClienteIntercambio cliente, IOptions<OpcionesSimulador> opciones, TimeProvider reloj,
    ILogger<ReceptorSimulado> log)
{
    private readonly OpcionesSimulador _opciones = opciones.Value;

    /// <summary>Una operación del servicio web de intercambio de la empresa simulada <paramref name="rut"/>.</summary>
    internal string Recibir(string rut, PeticionSoap pedido)
    {
        var empresa = EmpresaSimulada(rut);
        var xml = pedido.XmlData;
        switch (pedido.Operacion)
        {
            case "SOBRE":
                var (acuse, respuestaCfes, recibido) = ProcesarSobre(empresa, xml, "ws", nombre: null);
                _ = EnviarRespuestas(empresa, acuse, respuestaCfes);
                return Respuesta(recibido ? 1000 : 1001, recibido ? "Sobre Recibido." : "Sobre Rechazado.", null);
            case "RESPUESTASOBRE":
            case "RESPUESTACFES":
            case "NOTIFICACIONANULACIONCFE":
                var tipo = pedido.Operacion switch { "RESPUESTASOBRE" => "ACKSOBRE", "RESPUESTACFES" => "ACKCFE", _ => "ANULACION" };
                var (valido, detalle) = RegistrarRespuesta(empresa, tipo, xml, "ws", null);
                var codigo = pedido.Operacion switch { "RESPUESTASOBRE" => 2000, "RESPUESTACFES" => 3000, _ => 4000 };
                return Respuesta(valido ? codigo : codigo + 1, valido ? "Recibido." : "Rechazado.", detalle);
            default:
                throw new FaultSoap($"WS_eFactura_Intercambio no tiene la operación {pedido.Operacion}.");
        }
    }

    /// <summary>
    /// Un archivo que llegó por correo a la empresa simulada: un sobre (responde los archivos <c>M_</c> y <c>ME_</c>,
    /// y si hay <see cref="OpcionesSimulador.UrlEntregaCorreo"/> los entrega), una respuesta del producto o una anulación.
    /// </summary>
    internal async Task<(string Tipo, IReadOnlyList<ArchivoIntercambio> Respuestas, string Detalle)> RecibirCorreo(string rut, string? nombre, string xml)
    {
        var empresa = EmpresaSimulada(rut);
        var raiz = XmlUtil.TryCargar(xml, out var doc, out _) ? doc.DocumentElement?.LocalName : null;
        if (raiz is "ACKSobre" or "ACKCFE" or "CFEs_Anulados_x_RechazoDGI")
        {
            var tipo = raiz switch { "ACKSobre" => "ACKSOBRE", "ACKCFE" => "ACKCFE", _ => "ANULACION" };
            var (valido, detalle) = RegistrarRespuesta(empresa, tipo, xml, "correo", nombre);
            return (tipo, [], valido ? "Recibido." : detalle ?? "Rechazado.");
        }

        var (acuse, respuestaCfes, recibido) = ProcesarSobre(empresa, xml, "correo", nombre);
        var archivos = new List<ArchivoIntercambio> { acuse };
        if (respuestaCfes is not null)
            archivos.Add(respuestaCfes);
        var entrega = "";
        if (_opciones.UrlEntregaCorreo is { Length: > 0 } url && XmlUtil.TryCargar(xml, out var sobre, out _)
            && almacen.Emisor(sobre.DocumentElement.Hijo("Caratula").Texto("RUCEmisor") ?? "") is { Mail: { Length: > 0 } mailEmisor })
        {
            foreach (var archivo in archivos)
            {
                var mime = ClienteIntercambio.Mime(empresa.Mail ?? $"{empresa.Rut}@simulador.dgi", mailEmisor, archivo.Nombre, archivo.Nombre, archivo.Xml);
                var resultado = await cliente.EntregarCorreo(url, empresa.Mail ?? $"{empresa.Rut}@simulador.dgi", mailEmisor, mime);
                entrega += $" {archivo.Nombre}: {resultado.Detalle}.";
            }
        }
        return ("SOBRE", archivos, (recibido ? "Sobre recibido." : "Sobre rechazado.") + entrega);
    }

    private Emisor EmpresaSimulada(string rut) =>
        almacen.Emisor(rut) is { Simulado: true } empresa
            ? empresa
            : throw new FaultSoap($"El RUT {rut} no es una empresa simulada (POST /admin/emisores con \"simulado\": true).");

    /// <summary>Controla el sobre y arma el acuse; si lo recibe, también la respuesta a los CFE.</summary>
    private (ArchivoIntercambio Acuse, ArchivoIntercambio? RespuestaCfes, bool Recibido) ProcesarSobre(Emisor empresa, string xml, string canal, string? nombre)
    {
        var ahora = reloj.GetUtcNow();
        var motivos = new List<Motivo>();
        var detalles = new List<DetalleCfe>();
        string rutEmisor = "";
        long idEmisor = 0;
        var fechaSobre = ahora;
        var cantidad = 0;

        if (Encoding.UTF8.GetByteCount(xml) > _opciones.TamanoMaximoKb * 1024L)
            motivos.Add(Motivos.DeSobre("S07"));
        else if (!XmlUtil.TryCargar(xml, out var doc, out var error))
            motivos.Add(Motivos.DeSobre("S01", $"No es XML: {error}"));
        else if (doc.DocumentElement is not { LocalName: "EnvioCFE_entreEmpresas", NamespaceURI: Ns.Cfe } raiz)
            motivos.Add(Motivos.DeSobre("S01", $"La raíz es {doc.DocumentElement?.Name} y tiene que ser EnvioCFE_entreEmpresas."));
        else
        {
            var caratula = raiz.Hijo("Caratula");
            rutEmisor = caratula.Texto("RUCEmisor") ?? "";
            idEmisor = XmlUtil.Entero(caratula.Texto("Idemisor")) ?? 0;
            fechaSobre = Hora.Leer(caratula.Texto("Fecha")) ?? ahora;
            var items = raiz.Hijos("CFE_Adenda").ToList();
            cantidad = items.Count;
            var errores = Esquemas.Validar(xml, Esquemas.Documento.SobreEntreEmpresas);
            if (errores.FirstOrDefault(e => e.Ordinal == 0) is { } errorSobre)
                motivos.Add(Motivos.DeSobre("S04", errorSobre.Mensaje));
            if (caratula.Texto("RutReceptor") != empresa.Rut)
                motivos.Add(Motivos.DeSobre("S02", $"RutReceptor es {caratula.Texto("RutReceptor")} y el sobre llegó a {empresa.Rut}."));
            if (XmlUtil.Entero(caratula.Texto("CantCFE")) != items.Count)
                motivos.Add(Motivos.DeSobre("S05", $"CantCFE indica {caratula.Texto("CantCFE")} y el sobre tiene {items.Count} CFE."));

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
                var control = autoridad.Controlar(certificado, ahora);
                if (!control.Valido)
                    motivos.Add(Motivos.DeSobre("S03", control.Error));
                else if (control.Rut != rutEmisor)
                    motivos.Add(Motivos.DeSobre("S02", $"El certificado es del RUT {control.Rut} y RUCEmisor es {rutEmisor}."));
            }

            var cfes = items.Select(i => i.Hijo("CFE")).ToList();
            var firmas = cfes.Select(c => c is null ? Firma.Invalida("Falta el CFE.") : Firmas.VerificarEnveloped(c, certificado)).ToList();
            if (certificado is not null && (firmas.Any(f => f.Certificado is { } c && c.Thumbprint != certificado.Thumbprint)
                                            || cfes.Any(c => c is not null && Firmas.SerialIndicado(c) is { } serial && serial != Firmas.Serial(certificado))))
                motivos.Add(Motivos.DeSobre("S06"));
            if (cfes.Any(c => c is not null && CfeLeido.Leer(c).RutEmisor != rutEmisor))
                motivos.Add(Motivos.DeSobre("S02", "Hay CFE de otro RUC que el de la carátula."));
            if (motivos.Count == 0 && almacen.ExisteSobreIntercambio(rutEmisor, empresa.Rut, idEmisor))
                motivos.Add(Motivos.DeSobre("S20", $"La empresa ya recibió el sobre {idEmisor} de {rutEmisor}."));

            if (motivos.Count == 0)
                detalles = ControlarCfes(empresa, cfes, firmas, errores);
        }

        var estado = motivos.Count == 0 ? "AS" : "BS";
        var nombreSobre = nombre ?? $"Sob_{rutEmisor}_{Hora.Uruguay(fechaSobre):yyyyMMdd}_{idEmisor}.xml";
        var idReceptor = almacen.Siguiente("intercambio-" + empresa.Rut);
        var certificadoEmpresa = autoridad.CertificadoDe(empresa.Rut, empresa.Denominacion);

        var idRespuesta = almacen.Siguiente("respuestas-" + empresa.Rut);
        var caratulaAcuse = new Caratula(empresa.Rut, rutEmisor, idRespuesta, nombreSobre, ahora, idEmisor, idReceptor, ahora);
        var acuse = new ArchivoIntercambio(NombreRespuesta("M", idRespuesta, nombreSobre),
            Acuses.Sobre(caratulaAcuse, cantidad, estado, motivos, null, null, certificadoEmpresa));
        almacen.GuardarIntercambio(new MensajeIntercambio(0, "recibido", canal, "SOBRE", rutEmisor, empresa.Rut, idEmisor, nombreSobre, xml,
            acuse.Xml, estado, ahora));

        ArchivoIntercambio? respuestaCfes = null;
        if (estado == "AS")
        {
            var idRespuestaCfes = almacen.Siguiente("respuestas-" + empresa.Rut);
            respuestaCfes = new ArchivoIntercambio(NombreRespuesta("ME", idRespuestaCfes, nombreSobre),
                Acuses.Cfes(caratulaAcuse with { IdRespuesta = idRespuestaCfes }, cantidad, detalles, deLaDgi: false, certificadoEmpresa));
        }
        return (acuse, respuestaCfes, estado == "AS");
    }

    private List<DetalleCfe> ControlarCfes(Emisor empresa, List<XmlElement?> cfes, List<Firma> firmas, List<Esquemas.Error> errores)
    {
        var detalles = new List<DetalleCfe>();
        var vistos = new HashSet<(int?, string?, long?)>();
        var reglas = new ReglasFormato(_opciones);
        var comercial = fallas.Rechazo("SOBRE", 'E');
        for (var i = 0; i < cfes.Count; i++)
        {
            var motivos = new List<Motivo>();
            if (cfes[i] is not { } elemento)
            {
                detalles.Add(new DetalleCfe(i + 1, null, null, null, null, null, "BE", [Motivos.DeReceptor("E05", "El CFE_Adenda no tiene CFE.")]));
                continue;
            }
            var cfe = CfeLeido.Leer(elemento);
            if (!firmas[i].Valida)
                motivos.Add(Motivos.DeReceptor("E04", firmas[i].Error));
            if (errores.FirstOrDefault(e => e.Ordinal == i + 1) is { } error)
                motivos.Add(Motivos.DeReceptor("E05", "No valida contra el XSD: " + error.Mensaje));
            motivos.AddRange(reglas.Controlar(cfe).Where(h => h.Confirmado && h.Codigo == "E05").Select(h => Motivos.DeReceptor("E05", h.Detalle)));
            if (cfe.RutReceptor != empresa.Rut)
                motivos.Add(Motivos.DeReceptor("E05", $"El receptor del CFE es {cfe.DocReceptor ?? "(ninguno)"} y no {empresa.Rut}."));
            if (!vistos.Add((cfe.Tipo, cfe.Serie, cfe.Nro)))
                motivos.Add(Motivos.DeReceptor("E02", "Está repetido en el sobre."));
            if (cfe.Nro is { } nro && (XmlUtil.Entero(cfe.CaeData.Texto("DNro")) is not { } desde || XmlUtil.Entero(cfe.CaeData.Texto("HNro")) is not { } hasta
                                       || nro < desde || nro > hasta))
                motivos.Add(Motivos.DeReceptor("E03", "El número no está en el rango del CAE."));
            if (cfe.Firma is { } firma && Hora.LeerFecha(cfe.CaeData.Texto("FecVenc")) is { } vencimiento && Hora.Fecha(firma) > vencimiento)
                motivos.Add(Motivos.DeReceptor("E07", "Se firmó después del vencimiento del CAE."));
            if (comercial is not null)
                motivos.Add(Motivos.DeReceptor(comercial, "Rechazo programado en el simulador (POST /admin/fallas, operación SOBRE)."));
            detalles.Add(new DetalleCfe(i + 1, cfe.Tipo, cfe.Serie, cfe.Nro, cfe.FechaEmision, cfe.Firma, motivos.Count == 0 ? "AE" : "BE", motivos));
        }
        return detalles;
    }

    /// <summary>Manda el acuse y la respuesta a los CFE al servicio web del emisor, si figura en el padrón.</summary>
    private async Task EnviarRespuestas(Emisor empresa, ArchivoIntercambio acuse, ArchivoIntercambio? respuestaCfes)
    {
        try
        {
            var rutEmisor = XmlUtil.Cargar(acuse.Xml).DocumentElement.Hijo("Caratula").Texto("RUCEmisor") ?? "";
            if (almacen.Emisor(rutEmisor) is not { UrlServicio: { Length: > 0 } url })
                return;
            // Como un receptor real, responde después de haber contestado la recepción del sobre.
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            var certificado = autoridad.CertificadoDe(empresa.Rut, empresa.Denominacion);
            foreach (var (operacion, tipo, archivo) in new[] { ("RESPUESTASOBRE", "ACKSOBRE", acuse), ("RESPUESTACFES", "ACKCFE", respuestaCfes) })
            {
                if (archivo is null)
                    continue;
                var id = almacen.GuardarIntercambio(new MensajeIntercambio(0, "enviado", "ws", tipo, empresa.Rut, rutEmisor, null, archivo.Nombre,
                    archivo.Xml, null, "pendiente", reloj.GetUtcNow()));
                var entrega = await cliente.Enviar(url, operacion, archivo.Xml, certificado);
                almacen.ActualizarIntercambio(id, entrega.Recibido ? "entregado" : "error", entrega.Detalle);
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "No se pudieron enviar las respuestas de {Rut}", empresa.Rut);
        }
    }

    /// <summary>Registra una respuesta del producto (ACKSobre, ACKCFE) o una anulación, controlando su esquema y su firma.</summary>
    private (bool Valido, string? Detalle) RegistrarRespuesta(Emisor empresa, string tipo, string xml, string canal, string? nombre)
    {
        var documento = tipo switch
        {
            "ACKSOBRE" => Esquemas.Documento.AcuseDgi,
            "ACKCFE" => Esquemas.Documento.AcuseCfePartes,
            _ => Esquemas.Documento.Anulacion,
        };
        var problemas = new List<string>();
        var rutEmisor = "";
        if (!XmlUtil.TryCargar(xml, out var doc, out var error))
            problemas.Add($"No es XML: {error}");
        else
        {
            rutEmisor = (tipo == "ANULACION" ? doc.DocumentElement.Hijo("Caratula").Texto("RUCEmisor") : doc.DocumentElement.Hijo("Caratula").Texto("RUCReceptor")) ?? "";
            problemas.AddRange(Esquemas.Validar(xml, documento).Select(e => e.Mensaje).Take(3));
            var firma = Firmas.VerificarEnveloped(doc.DocumentElement!);
            if (!firma.Valida)
                problemas.Add("Firma: " + firma.Error);
            else if (Autoridad.RutDe(firma.Certificado!) != rutEmisor)
                problemas.Add($"La firma es del RUT {Autoridad.RutDe(firma.Certificado!)} y el mensaje es de {rutEmisor}.");
        }
        var detalle = problemas.Count == 0 ? null : string.Join(" ", problemas);
        almacen.GuardarIntercambio(new MensajeIntercambio(0, "recibido", canal, tipo, rutEmisor, empresa.Rut, null, nombre, xml, detalle,
            problemas.Count == 0 ? "valido" : "invalido", reloj.GetUtcNow()));
        return (problemas.Count == 0, detalle);
    }

    private static string Respuesta(int codigo, string descripcion, string? detalle) => XmlUtil.Escribir(w =>
    {
        w.WriteStartDocument();
        w.WriteStartElement("Respuestas", Ns.Cfe);
        w.WriteStartElement("Respuesta", Ns.Cfe);
        w.El("Codigo", XmlUtil.Numero(codigo));
        w.El("Descripcion", descripcion);
        w.El("Detalle", detalle ?? "");
        w.WriteEndElement();
        w.WriteEndElement();
    });

    /// <summary><c>{prefijo}_IDRespuesta_Sob_RUC_AAAAMMDD_IdEmisor.xml</c>.</summary>
    internal static string NombreRespuesta(string prefijo, long idRespuesta, string nombreSobre) =>
        $"{prefijo}_{idRespuesta}_{Path.GetFileNameWithoutExtension(nombreSobre)}.xml";
}
