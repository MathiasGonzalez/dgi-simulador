using System.Globalization;
using System.Text;
using System.Xml;
using DgiSimulador.Certificados;
using DgiSimulador.Estado;
using DgiSimulador.Xml;
using Microsoft.Extensions.Options;

namespace DgiSimulador.Recepcion;

/// <summary>
/// <c>EFACRECEPCIONREPORTE</c>: controla el reporte diario (R01 a R06) y responde <c>ACKRepDiario</c>. La coincidencia con
/// los CFE recibidos ese día no está detallada en el formato: se anota como advertencia (rechaza en modo estricto).
/// </summary>
internal sealed class RecepcionReporte(Almacen almacen, Autoridad autoridad, Fallas fallas, IOptions<OpcionesSimulador> opciones, TimeProvider reloj)
{
    private const string Operacion = "EFACRECEPCIONREPORTE";
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
        var rutEnvio = envio.Certificado is { } c ? Autoridad.RutDe(c) : null;
        var motivos = new List<Motivo>();
        var hallazgos = new List<Hallazgo>();
        var rut = rutEnvio ?? "";
        long idEmisor = 0;
        DateOnly? fecha = null;
        int? secuencia = null;
        XmlElement? reporte = null;

        if (Encoding.UTF8.GetByteCount(xml) > _opciones.TamanoMaximoReporteKb * 1024L)
            motivos.Add(Motivos.DeReporte("R01", $"El reporte supera el tamaño máximo configurado ({_opciones.TamanoMaximoReporteKb} KB)."));
        else if (!XmlUtil.TryCargar(xml, out var doc, out var error))
            motivos.Add(Motivos.DeReporte("R01", $"No es XML: {error}"));
        else if (doc.DocumentElement is not { LocalName: "Reporte", NamespaceURI: Ns.Cfe } raiz)
            motivos.Add(Motivos.DeReporte("R01", $"La raíz es {doc.DocumentElement?.Name} y tiene que ser Reporte del namespace {Ns.Cfe}."));
        else
        {
            reporte = raiz;
            var caratula = raiz.Hijo("Caratula");
            rut = caratula.Texto("RUCEmisor") ?? rut;
            idEmisor = XmlUtil.Entero(caratula.Texto("IDEmisor")) ?? 0;
            fecha = Hora.LeerFecha(caratula.Texto("FechaResumen"));
            secuencia = XmlUtil.Entero(caratula.Texto("SecEnvio")) is { } s and <= int.MaxValue ? (int)s : null;

            var errores = Esquemas.Validar(xml, Esquemas.Documento.Reporte);
            if (errores.Count > 0)
                motivos.Add(Motivos.DeReporte("R04", errores[0].Mensaje + (errores.Count > 1 ? $" (y {errores.Count - 1} errores más)" : "")));

            var firma = Firmas.VerificarEnveloped(raiz);
            if (!firma.Valida)
                motivos.Add(Motivos.DeReporte("R03", firma.Error));
            else
            {
                var momento = Hora.Leer(caratula.Texto("TmstFirmaEnv")) ?? ahora;
                var control = autoridad.Controlar(firma.Certificado!, momento);
                if (!control.Valido)
                    motivos.Add(Motivos.DeReporte("R03", control.Error));
                else if (control.Rut != rut)
                    motivos.Add(Motivos.DeReporte("R02", $"El reporte está firmado por el RUT {control.Rut} y RUCEmisor es {rut}."));
            }
            if (rutEnvio != rut)
                motivos.Add(Motivos.DeReporte("R02", $"El envío está firmado (WS-Security) por el RUT {rutEnvio ?? "(sin RUT)"} y RUCEmisor es {rut}."));

            if (fecha is { } f && f > Hora.Fecha(ahora))
                motivos.Add(Motivos.DeReporte("R06", $"FechaResumen {Hora.Formato(f)} es posterior a la fecha de recepción {Hora.Formato(Hora.Fecha(ahora))}."));
            if (fecha is { } dia && secuencia is { } sec)
            {
                var esperada = almacen.UltimaSecuencia(rut, dia) + 1;
                if (sec != esperada)
                    motivos.Add(Motivos.DeReporte("R05", $"SecEnvio es {sec} y para el {Hora.Formato(dia)} corresponde {esperada}."));
            }
            if (fecha is { } diaResumen)
                Consistencia(raiz, rut, diaResumen, hallazgos);
        }

        if (motivos.Count == 0 && fallas.Rechazo(Operacion, 'R') is { } forzado && Motivos.Reporte.ContainsKey(forzado))
            motivos.Add(Motivos.DeReporte(forzado, "Rechazo programado en el simulador (POST /admin/fallas)."));
        motivos.AddRange(hallazgos.Where(h => h.Confirmado || _opciones.Estricto).Select(h => Motivos.DeReporte("R04", h.Detalle)));
        var advertencias = hallazgos.Where(h => !h.Confirmado && !_opciones.Estricto).Select(h => h.Detalle).ToList();

        var estado = motivos.Count == 0 ? "AR" : "BR";
        if (estado == "AR" && reporte is not null && fecha is { } fechaAnulados)
            GuardarAnulados(reporte, rut, fechaAnulados);

        var nombre = $"Rep_{rut}_{(fecha is { } fr ? fr.ToString("yyyyMMdd", CultureInfo.InvariantCulture) : "")}_{secuencia}";
        var acuse = Acuses.Reporte(new Caratula(Autoridad.RutDgi, rut, 0, nombre, ahora, idEmisor, idReceptor, ahora), estado, motivos, autoridad.Dgi);
        almacen.GuardarReporte(new ReporteRecibido(idReceptor, rut, idEmisor, fecha, secuencia, estado, motivos, advertencias, ahora, xml, acuse));
        return acuse;
    }

    /// <summary>Una zona <c>Rsmn_*</c> del reporte: el tipo de comprobante y lo que informa.</summary>
    private sealed record Zona(string Nombre, int Tipo, long? Utilizados, long? Emitidos, long? Anulados,
        List<(string Serie, long Desde, long Hasta)> Usados, List<(string Serie, long Desde, long Hasta)> RangosAnulados, decimal Total);

    private static List<Zona> Zonas(XmlElement reporte) =>
    [
        .. reporte.Hijos().Where(e => e.LocalName.StartsWith("Rsmn_", StringComparison.Ordinal)).Select(zona =>
        {
            var datos = zona.Hijo("RsmnData");
            var tipo = (int)(XmlUtil.Entero(zona.Texto("TipoComp")) ?? 0);
            static List<(string, long, long)> Rangos(XmlElement? rangos, string item) =>
            [
                .. rangos.Hijos(item).Select(r => (r.Texto("Serie") ?? "", XmlUtil.Entero(r.Texto("NroDesde")) ?? 0, XmlUtil.Entero(r.Texto("NroHasta")) ?? 0)),
            ];
            var total = datos.Hijo("Montos").Hijos().Sum(m => XmlUtil.Decimal(m.Texto("TotMntTotal")) ?? XmlUtil.Decimal(m.Texto("TotMntRetenido")) ?? 0m);
            return new Zona(zona.LocalName, tipo, XmlUtil.Entero(datos.Texto("CantDocsUtil")),
                XmlUtil.Entero(datos.Texto("CantDocsEmi")) ?? XmlUtil.Entero(datos.Texto("CantCFCEmi")), XmlUtil.Entero(datos.Texto("CantDocsAnulados")),
                Rangos(datos.Hijo("RngDocsUtil"), "RDU_Item"), Rangos(datos.Hijo("RngDocsAnulados"), "RDA_Item"), total);
        }),
    ];

    /// <summary>
    /// Las cantidades del reporte entre sí (las documenta <c>ReporteDiarioCFE.xsd</c>: rechazan) y contra los CFE aceptados
    /// del día (inferido: advertencia).
    /// </summary>
    private void Consistencia(XmlElement reporte, string rut, DateOnly fecha, List<Hallazgo> hallazgos)
    {
        var zonas = Zonas(reporte);
        foreach (var zona in zonas)
        {
            if (zona.Utilizados is { } u && zona.Emitidos is { } e && u != e + (zona.Anulados ?? 0))
                hallazgos.Add(new("R04", $"{zona.Nombre}: CantDocsUtil {u} no es CantDocsEmi {e} + CantDocsAnulados {zona.Anulados ?? 0}."));
        }

        var cantidad = XmlUtil.Entero(reporte.Hijo("Caratula").Texto("CantComprobantes"));
        var suma = zonas.Sum(z => z.Utilizados ?? z.Emitidos ?? 0);
        if (cantidad is { } cc && cc != suma)
            hallazgos.Add(new("R04", $"CantComprobantes es {cc} y la suma de los CFE utilizados y los CFC emitidos de las zonas da {suma}."));

        var recibidos = almacen.CfesAceptadosDelDia(rut, fecha).Where(x => x.Tipo is not null).GroupBy(x => x.Tipo!.Value).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var tipo in recibidos.Keys.Union(zonas.Select(z => z.Tipo)).Order())
        {
            var delTipo = zonas.Where(z => z.Tipo == tipo).ToList();
            var cfes = recibidos.GetValueOrDefault(tipo) ?? [];
            var informados = delTipo.Sum(z => z.Emitidos ?? 0);
            if (informados != cfes.Count)
                hallazgos.Add(new("R04", $"Tipo {tipo}: el reporte informa {informados} emitidos y la DGI aceptó {cfes.Count} con fecha {Hora.Formato(fecha)}.", Confirmado: false));

            var rangos = delTipo.SelectMany(z => z.Usados).ToList();
            if (rangos.Count > 0)
            {
                var fuera = cfes.Where(x => !rangos.Any(r => r.Serie == x.Serie && x.Nro >= r.Desde && x.Nro <= r.Hasta)).Select(x => $"{x.Serie}{x.Nro}").ToList();
                if (fuera.Count > 0)
                    hallazgos.Add(new("R04", $"Tipo {tipo}: {string.Join(", ", fuera.Take(10))} no están en los rangos utilizados (RngDocsUtil).", Confirmado: false));
            }

            var montoInformado = delTipo.Sum(z => z.Total);
            var montoRecibido = cfes.Sum(x => ReglasFormato.Redondear((x.Total ?? x.Retenido ?? 0m) * (x.TipoCambio ?? 1m)));
            if (Math.Abs(montoInformado - montoRecibido) > _opciones.Tolerancia * Math.Max(1, cfes.Count))
                hallazgos.Add(new("R04", $"Tipo {tipo}: el reporte informa {montoInformado:0.00} pesos y los CFE aceptados suman {montoRecibido:0.00}.", Confirmado: false));
        }
    }

    private void GuardarAnulados(XmlElement reporte, string rut, DateOnly fecha)
    {
        foreach (var zona in Zonas(reporte))
            foreach (var (serie, desde, hasta) in zona.RangosAnulados)
                almacen.GuardarAnulados(rut, zona.Tipo, serie, desde, hasta, fecha);
    }
}
