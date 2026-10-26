using System.IO.Compression;
using System.Text;
using System.Xml;
using DgiSimulador.Certificados;
using DgiSimulador.Estado;
using DgiSimulador.Soap;
using DgiSimulador.Xml;

namespace DgiSimulador.Consultas;

/// <summary>
/// <c>WS_eFactura_Consultas</c>: estado de un CFE, padrón de emisores (normal, de transición y comprimido), CFE
/// recibidos, envíos de sobres y reportes y la respuesta a un reporte. Los parámetros y las respuestas siguen los tipos
/// de <c>ws_consultasPrueba.xsd1.xsd</c>. El consultante es el RUT del certificado de WS-Security.
/// </summary>
internal sealed class ServicioConsultas(Almacen almacen, Autoridad autoridad, TimeProvider reloj)
{
    internal string Responder(PeticionSoap pedido)
    {
        var rut = Consultante(pedido);
        var p = pedido.Contenido;
        return pedido.Operacion switch
        {
            "EFACCONSULTARESTADOCFE" => EstadoCfe(pedido, rut, p.Hijo("Cfeid")),
            "EFACCONSULTARRUCEMISORES" or "EFACCONSULTARRUCEMISORESTRANSICION" => Padron(pedido, p.Hijo("Consultarucemisores")),
            "EFACCONSULTARRUCEMISORESTRANSICIONCOMPRIMIDO" => PadronComprimido(pedido, p.Hijo("Consultarucemisores"), proveedores: false),
            "EFACCONSULTARRUCEMISORESTRANSICIONCOMPRIMIDOPROV" => PadronComprimido(pedido, p.Hijo("Consultarucemisores"), proveedores: true),
            "EFACCONSULTARCFERECIBIDOS" => CfeRecibidos(pedido, rut, p.Hijo("Consultacferecibidos")),
            "EFACCONSULTARENVIOSSOBRE" => EnviosSobre(pedido, rut, p.Hijo("Consultaenviossobre")),
            "EFACCONSULTARENVIOSCFE" => EnviosCfe(pedido, rut, p.Hijo("Cfeid")),
            "EFACCONSULTARENVIOSREPORTE" => EnviosReporte(pedido, rut, p.Hijo("Consultaenviosreporte")),
            "EFACCONSULTARRESPUESTAREPORTE" => RespuestaReporte(pedido, rut, p.Hijo("Consultarespuestareporte")),
            _ => throw new FaultSoap($"WS_eFactura_Consultas no tiene la operación {pedido.Operacion}."),
        };
    }

    private string Consultante(PeticionSoap pedido)
    {
        var control = autoridad.Controlar(pedido.Firma.Certificado!, reloj.GetUtcNow());
        return control.Valido ? control.Rut! : throw new FaultSoap($"Certificado no aceptado: {control.Error}");
    }

    /// <summary>
    /// El estado de un CFE del consultante y los parámetros para consultar el sobre en que llegó. <c>EstadoCFE</c> es el
    /// resultado del comprobante (AE, BE o CE): el formato no lo define.
    /// </summary>
    private string EstadoCfe(PeticionSoap pedido, string rut, XmlElement? cfeid)
    {
        var (tipo, serie, nro) = IdCfe(cfeid);
        var cfe = almacen.Cfe(rut, tipo, serie, nro) ?? throw new FaultSoap($"La DGI no tiene el CFE {tipo} {serie}{nro} del RUT {rut}.");
        var sobre = almacen.Sobre(cfe.IdReceptor)!;
        return MensajeSoap.Respuesta(pedido, w =>
        {
            w.WriteStartElement("Ackconsultaestadocfe", Ns.Dgi);
            w.El("EstadoCFE", cfe.Estado, Ns.Dgi);
            w.El("IdEmisor", XmlUtil.Numero(sobre.IdEmisor), Ns.Dgi);
            w.El("IdReceptor", XmlUtil.Numero(sobre.IdReceptor), Ns.Dgi);
            ParamConsulta(w, sobre);
            w.WriteEndElement();
        });
    }

    private static (int Tipo, string Serie, long Nro) IdCfe(XmlElement? cfeid)
    {
        if (XmlUtil.Entero(cfeid.Texto("TipoCFE")) is not { } tipo || cfeid.Texto("Serie") is not { } serie || XmlUtil.Entero(cfeid.Texto("Nro")) is not { } nro)
            throw new FaultSoap("Cfeid tiene que tener TipoCFE, Serie y Nro.");
        return ((int)tipo, serie, nro);
    }

    private static void ParamConsulta(XmlWriter w, SobreRecibido sobre)
    {
        w.WriteStartElement("ParamConsulta", Ns.Dgi);
        w.El("Token", sobre.Token ?? "", Ns.Dgi);
        w.El("Fechahora", Hora.Formato(sobre.ConsultarDesde ?? sobre.Recibido), Ns.Dgi);
        w.WriteEndElement();
    }

    // --- Padrón -------------------------------------------------------------------------------

    private IEnumerable<Emisor> Filtrar(XmlElement? consulta)
    {
        var documento = consulta.Texto("Filtro_eFacDocNro");
        var denominacion = consulta.Texto("Filtro_eFacDenominacion");
        var desde = Hora.Leer(consulta.Texto("Filtro_eFacRUCEmisorAudFchHora"));
        return almacen.Emisores().Where(e =>
            (string.IsNullOrEmpty(documento) || documento == "0" || e.Rut == documento)
            && (string.IsNullOrEmpty(denominacion) || e.Denominacion.Contains(denominacion, StringComparison.OrdinalIgnoreCase))
            && (desde is null || e.Modificado >= desde));
    }

    /// <summary>Fecha del padrón; sin fecha es <c>0000-00-00</c>.</summary>
    private static string FechaPadron(DateOnly? fecha) => fecha is { } f ? Hora.Formato(f) : "0000-00-00";

    private string Padron(PeticionSoap pedido, XmlElement? consulta) => MensajeSoap.Respuesta(pedido, w =>
    {
        w.WriteStartElement("Rucemisoresmail", Ns.Dgi);
        foreach (var e in Filtrar(consulta))
        {
            w.WriteStartElement("RucEmisoresMail.RucEmisoresMailItem", Ns.Dgi);
            w.El("RUC", e.Rut, Ns.Dgi);
            w.El("DENOMINACION", e.Denominacion, Ns.Dgi);
            w.El("FECHA_INICIO", FechaPadron(e.FechaInicio), Ns.Dgi);
            w.El("FECHA_FIN", FechaPadron(e.FechaFin), Ns.Dgi);
            w.El("MAIL", e.Mail ?? "", Ns.Dgi);
            w.WriteEndElement();
        }
        w.WriteEndElement();
    });

    /// <summary>El padrón de transición como XML comprimido con gzip y en base64.</summary>
    private string PadronComprimido(PeticionSoap pedido, XmlElement? consulta, bool proveedores)
    {
        var raiz = proveedores ? "RucEmisoresTransicionMailProv" : "RucEmisoresTransicionMail";
        var xml = XmlUtil.Escribir(w =>
        {
            w.WriteStartDocument();
            w.WriteStartElement(raiz, Ns.Dgi);
            foreach (var e in Filtrar(consulta))
            {
                w.WriteStartElement($"{raiz}.{raiz}Item", Ns.Dgi);
                w.El("RUC", e.Rut, Ns.Dgi);
                w.El("DENOMINACION", e.Denominacion, Ns.Dgi);
                w.El("FECHA_INICIO", FechaPadron(e.FechaInicio), Ns.Dgi);
                w.El("FECHA_FIN_TRANSICION", FechaPadron(e.FechaFinTransicion), Ns.Dgi);
                w.El("FECHA_FIN", FechaPadron(e.FechaFin), Ns.Dgi);
                w.El("MAIL", e.Mail ?? "", Ns.Dgi);
                if (proveedores)
                {
                    w.El("URL_WEBSERVICE", e.UrlServicio ?? "", Ns.Dgi);
                    w.El("MAIL_CONTACTO_TECNICO", e.MailTecnico ?? "", Ns.Dgi);
                }
                w.WriteEndElement();
            }
            w.WriteEndElement();
        });
        var elemento = proveedores ? "Rucemisorestransicionmailcomprimidoprov" : "Rucemisorestransicionmailcomprimido";
        return MensajeSoap.Respuesta(pedido, w => w.El(elemento, Comprimir(xml), Ns.Dgi));
    }

    internal static string Comprimir(string texto)
    {
        using var salida = new MemoryStream();
        using (var gzip = new GZipStream(salida, CompressionLevel.Optimal))
            gzip.Write(Encoding.UTF8.GetBytes(texto));
        return Convert.ToBase64String(salida.ToArray());
    }

    // --- CFE recibidos y envíos -----------------------------------------------------------------

    /// <summary>
    /// Los CFE que otros emisores enviaron a la DGI con el consultante como receptor (RUC). Los filtros en 0 o en blanco
    /// no filtran; las fechas son las de recepción en la DGI.
    /// </summary>
    private string CfeRecibidos(PeticionSoap pedido, string rut, XmlElement? consulta)
    {
        var emisor = consulta.Texto("RUCEmisor");
        var tipo = XmlUtil.Entero(consulta.Texto("TipoCFE")) ?? 0;
        var serie = consulta.Texto("Serie");
        var nro = XmlUtil.Entero(consulta.Texto("Nro")) ?? 0;
        var desde = Hora.Leer(consulta.Texto("FechaDesde"));
        var hasta = Hora.Leer(consulta.Texto("FechaHasta"));
        var cfes = almacen.CfesParaReceptor(rut).Where(c =>
            (string.IsNullOrEmpty(emisor) || emisor == "0" || c.Rut == emisor)
            && (tipo == 0 || c.Tipo == tipo)
            && (string.IsNullOrWhiteSpace(serie) || c.Serie == serie)
            && (nro == 0 || c.Nro == nro)
            && (desde is null || c.Recibido >= desde)
            && (hasta is null || c.Recibido <= hasta));
        return MensajeSoap.Respuesta(pedido, w =>
        {
            w.WriteStartElement("Ackconsultacferecibidos", Ns.Dgi);
            w.WriteStartElement("ColeccionDatosCFE", Ns.Dgi);
            foreach (var c in cfes)
            {
                w.WriteStartElement("DatosCFE", Ns.Dgi);
                w.El("TipoCFE", XmlUtil.Numero(c.Tipo ?? 0), Ns.Dgi);
                w.El("Serie", c.Serie ?? "", Ns.Dgi);
                w.El("Nro", XmlUtil.Numero(c.Nro ?? 0), Ns.Dgi);
                w.El("Estado", c.Estado, Ns.Dgi);
                w.El("FechaHora", Hora.Formato(c.Recibido), Ns.Dgi);
                w.El("RucEmisor", c.Rut, Ns.Dgi);
                w.El("Moneda", c.Moneda ?? "", Ns.Dgi);
                w.El("TotalNeto", XmlUtil.Numero(c.Neto ?? 0m), Ns.Dgi);
                w.El("TotalIVA", XmlUtil.Numero(c.Iva ?? 0m), Ns.Dgi);
                w.El("MontoTotal", XmlUtil.Numero(c.Total ?? 0m), Ns.Dgi);
                w.El("TotalRetenido", XmlUtil.Numero(c.Retenido ?? 0m), Ns.Dgi);
                w.WriteEndElement();
            }
            w.WriteEndElement();
            w.WriteEndElement();
        });
    }

    private string EnviosSobre(PeticionSoap pedido, string rut, XmlElement? consulta)
    {
        var idEmisor = XmlUtil.Entero(consulta.Texto("IdEmisor")) ?? 0;
        var idReceptor = XmlUtil.Entero(consulta.Texto("IdReceptor")) ?? 0;
        var desde = Hora.Leer(consulta.Texto("FechaDesde"));
        var hasta = Hora.Leer(consulta.Texto("FechaHasta"));
        var sobres = almacen.Sobres(rut, limite: 10_000).Where(s =>
            (idEmisor == 0 || s.IdEmisor == idEmisor) && (idReceptor == 0 || s.IdReceptor == idReceptor)
            && (desde is null || s.Recibido >= desde) && (hasta is null || s.Recibido <= hasta)).OrderBy(s => s.IdReceptor);
        return MensajeSoap.Respuesta(pedido, w => DatosSobres(w, "Ackconsultaenviossobre", "ColeccionDatosSobre", sobres));
    }

    private string EnviosCfe(PeticionSoap pedido, string rut, XmlElement? cfeid)
    {
        var (tipo, serie, nro) = IdCfe(cfeid);
        return MensajeSoap.Respuesta(pedido, w =>
            DatosSobres(w, "Ackconsultaenvioscfe", "ColeccionDatosSobres", almacen.SobresConCfe(rut, tipo, serie, nro)));
    }

    private static void DatosSobres(XmlWriter w, string raiz, string coleccion, IEnumerable<SobreRecibido> sobres)
    {
        w.WriteStartElement(raiz, Ns.Dgi);
        w.WriteStartElement(coleccion, Ns.Dgi);
        foreach (var s in sobres)
        {
            w.WriteStartElement("DatosSobre", Ns.Dgi);
            w.El("EstadoSobre", s.Estado, Ns.Dgi);
            w.El("FechaHoraSobre", Hora.Formato(s.Recibido), Ns.Dgi);
            w.El("IdEmisor", XmlUtil.Numero(s.IdEmisor), Ns.Dgi);
            w.El("IdReceptor", XmlUtil.Numero(s.IdReceptor), Ns.Dgi);
            ParamConsulta(w, s);
            w.WriteEndElement();
        }
        w.WriteEndElement();
        w.WriteEndElement();
    }

    private string EnviosReporte(PeticionSoap pedido, string rut, XmlElement? consulta)
    {
        var fecha = Hora.LeerFecha(consulta.Texto("FechaResumen")) ?? throw new FaultSoap("Consultaenviosreporte tiene que tener FechaResumen.");
        var secuencia = XmlUtil.Entero(consulta.Texto("Secuencia")) ?? 0;
        var idEmisor = XmlUtil.Entero(consulta.Texto("IdEmisor")) ?? 0;
        var reportes = almacen.Reportes(rut, fecha, limite: 10_000)
            .Where(r => (secuencia == 0 || r.Secuencia == secuencia) && (idEmisor == 0 || r.IdEmisor == idEmisor)).OrderBy(r => r.IdReceptor);
        return MensajeSoap.Respuesta(pedido, w =>
        {
            w.WriteStartElement("Ackconsultaenviosreporte", Ns.Dgi);
            w.WriteStartElement("ColeccionDatosReporte", Ns.Dgi);
            foreach (var r in reportes)
            {
                w.WriteStartElement("DatosReporte", Ns.Dgi);
                w.El("IdEmisor", XmlUtil.Numero(r.IdEmisor), Ns.Dgi);
                w.El("IdReceptor", XmlUtil.Numero(r.IdReceptor), Ns.Dgi);
                w.El("Estado", r.Estado, Ns.Dgi);
                w.El("FechaHoraRecepcion", Hora.Formato(r.Recibido), Ns.Dgi);
                w.WriteEndElement();
            }
            w.WriteEndElement();
            w.WriteEndElement();
        });
    }

    private string RespuestaReporte(PeticionSoap pedido, string rut, XmlElement? consulta)
    {
        var id = XmlUtil.Entero(consulta.Texto("IdReceptor")) ?? throw new FaultSoap("Consultarespuestareporte tiene que tener IdReceptor.");
        var reporte = almacen.Reporte(id);
        if (reporte is null || reporte.Rut != rut)
            throw new FaultSoap($"No hay un reporte con IdReceptor {id} del RUT {rut}.");
        return MensajeSoap.Respuesta(pedido, w => w.El("Ackrepdiario", reporte.Acuse, Ns.Dgi));
    }
}
