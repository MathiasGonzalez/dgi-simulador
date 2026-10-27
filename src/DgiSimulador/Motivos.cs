using DgiSimulador.Estado;

namespace DgiSimulador;

/// <summary>
/// Códigos de rechazo y observación con sus glosas oficiales: las tablas 4.1 a 4.4 del Formato de los Mensajes de
/// Respuesta v19, que coinciden con las enumeraciones de <c>Glosa</c> en <c>DGITypes.xsd</c> (el esquema solo admite
/// esas). Donde el texto de la tabla y el del XSD difieren (C09) se usa el del XSD.
/// </summary>
internal static class Motivos
{
    /// <summary>Rechazo de un sobre (<c>RechazoSobreType</c>).</summary>
    internal static readonly IReadOnlyDictionary<string, string> Sobre = new Dictionary<string, string>
    {
        ["S01"] = "Formato del archivo no es el indicado",
        ["S02"] = "No coincide RUC de Sobre, Certificado, envío o CFE",
        ["S03"] = "Certificado electrónico no es válido",
        ["S04"] = "No cumple validaciones según Formato de sobre",
        ["S05"] = "No coinciden cantidad CFE de carátula y contenido",
        ["S06"] = "No coinciden certificado de sobre y comprobantes",
        ["S07"] = "Sobre enviado supera el tamaño máximo admitido",
        ["S08"] = "Sobre enviado ya existe en los registros de DGI",
        ["S20"] = "Sobre enviado ya existe en los registros del receptor",
    };

    /// <summary>Rechazo de un CFE por la DGI (<c>RechazoCFE_DGIType</c>).</summary>
    internal static readonly IReadOnlyDictionary<string, string> Cfe = new Dictionary<string, string>
    {
        ["E01"] = "Tipo y Nº de CFE ya fue reportado como anulado",
        ["E02"] = "Tipo y Nº de CFE ya existe en los registros",
        ["E03"] = "Tipo y Nº de CFE no se corresponden con el CAE",
        ["E04"] = "Firma electrónica no es válida",
        ["E05"] = "No cumple validaciones de Formato comprobantes",
        ["E06"] = "Importe informado en el CFE excede montos habituales",
        ["E07"] = "Fecha Firma de CFE no se corresponde con fecha CAE",
        ["E08"] = "No coincide RUC de CFE y Complemento Fiscal",
        ["E09"] = "RUC emisor y/o tipo de CFE no se corresponden con el CAE",
        ["E10"] = "Tipo de cambio no válido",
        ["E11"] = "Tipo de CAE especial del CFE no se corresponde con el CAE",
        ["E12"] = "Información en Zona de Referencia no existe en los registros",
        ["E13"] = "Tipo de CAE  del CFE  no se corresponde con el CAE utilizado",
        ["E14"] = "Irregularidades detectadas",
        ["E15"] = "Deudor contumaz sin informar situación",
    };

    /// <summary>
    /// Observación de un comprobante de contingencia (<c>ObsCFCType</c>), con la glosa que corresponde a cada código
    /// según la documentación del tipo. Va asociado al código E equivalente.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, (string Codigo, string Glosa)> Contingencia = new Dictionary<string, (string, string)>
    {
        ["E03"] = ("C03", "Constancia no autorizada según registros"),
        ["E04"] = ("C04", "Firma electrónica no es válida"),
        ["E05"] = ("C05", "No cumple validaciones de Formato comprobantes"),
        ["E06"] = ("C06", "Importe informado en el CFE excede montos habituales"),
        ["E08"] = ("C08", "No coincide RUC de CFE y Complemento Fiscal"),
        ["E09"] = ("C09", "Nº de CFC no fue autorizado según registros"),
        ["E10"] = ("C10", "Tipo de cambio no válido"),
        ["E11"] = ("C11", "Tipo de constancia especial del CFC no se corresponde con el constancia"),
        ["E12"] = ("C12", "Información en Zona de Referencia no existe en nuestros registros"),
        ["E13"] = ("C13", "Tipo de constancia del CFC no se corresponde con la constancia"),
        ["E14"] = ("C14", "Irregularidades detectadas"),
        ["E15"] = ("C15", "Deudor contumaz sin informar situación"),
    };

    /// <summary>Rechazo del reporte diario (<c>RechazoRepType</c>).</summary>
    internal static readonly IReadOnlyDictionary<string, string> Reporte = new Dictionary<string, string>
    {
        ["R01"] = "Formato del archivo no es el indicado",
        ["R02"] = "No coincide RUC en Reporte, Certificado o envío",
        ["R03"] = "Firma o Certificado electrónicos no son válidos",
        ["R04"] = "No cumple validaciones según Formato de Reporte",
        ["R05"] = "La secuencia indicada en el reporte no es correcta",
        ["R06"] = "Fecha recepción no puede ser menor a fecha resumen",
    };

    /// <summary>
    /// Rechazo de un CFE por un receptor (<c>RechazoCFE_PartesType</c>): E02 a E15 con las glosas de la DGI y los
    /// comerciales E20 a E26 (E27 a E60 los definen las partes).
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> CfeReceptor = new Dictionary<string, string>
    {
        ["E02"] = "Tipo y Nº de CFE ya existe en los registros",
        ["E03"] = "Tipo y Nº de CFE no se corresponden con el CAE",
        ["E04"] = "Firma electrónica no es válida",
        ["E05"] = "No cumple validaciones de Formato comprobantes",
        ["E07"] = "Fecha Firma de CFE no se corresponde con fecha CAE",
        ["E20"] = "Orden de compra vencida",
        ["E21"] = "Mercadería en mal estado",
        ["E22"] = "Proveedor inhabilitado por organismo de contralor",
        ["E23"] = "Contraprestación no recibida",
        ["E24"] = "Diferencia precios y/o descuentos",
        ["E25"] = "Factura con error cálculos",
        ["E26"] = "Diferencia con plazos",
    };

    internal static Motivo DeSobre(string codigo, string? detalle = null) => new(codigo, Sobre[codigo], Detalle(detalle));

    internal static Motivo DeCfe(string codigo, string? detalle = null) => new(codigo, Cfe[codigo], Detalle(detalle));

    internal static Motivo DeReporte(string codigo, string? detalle = null) => new(codigo, Reporte[codigo], Detalle(detalle));

    internal static Motivo DeReceptor(string codigo, string? detalle = null) =>
        new(codigo, CfeReceptor.TryGetValue(codigo, out var glosa) ? glosa : "Rechazo comercial", Detalle(detalle));

    /// <summary>El detalle tiene hasta 500 caracteres en los acuses.</summary>
    private static string? Detalle(string? detalle) => detalle is null ? null : detalle.Length <= 500 ? detalle : detalle[..497] + "...";
}
