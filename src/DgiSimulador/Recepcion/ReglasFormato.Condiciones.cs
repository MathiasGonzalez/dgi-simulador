using System.Xml;
using DgiSimulador.Xml;

namespace DgiSimulador.Recepcion;

/// <summary>
/// Controles del Formato de los CFE v25.2 que dependen de otro campo del comprobante: los indicadores de facturación
/// que admite cada régimen (A-C10, B-C4 y G-C5), el propietario de la mercadería de un remito (A-C6.1 a A-C6.6), el
/// agente o responsable de las retenciones (B-C6), el mandante del complemento fiscal (zona K) y los códigos de
/// retención (B-C20). Las condiciones son las de la columna "Validación" de cada campo.
/// </summary>
internal sealed partial class ReglasFormato
{
    private static readonly string[] PaisesDelDni = ["AR", "BR", "CL", "PY"];

    // --- Indicadores de facturación y CAE especial ---------------------------------------------

    private static void Indicadores(CfeLeido cfe, Familia familia, List<Hallazgo> h)
    {
        var cuentaAjena = cfe.Tipo is { } tipo && TiposCfe.EsCuentaAjena(tipo);
        var bruto = cfe.MontosBrutos;
        var especial = XmlUtil.Entero(cfe.CaeData.Texto("CAEEspecial"));
        var indicadores = cfe.Items.Select(i => (Linea: i.Texto("NroLinDet"), Indicador: i.Texto("IndFact"))).ToList();

        // A-C10 y B-C4: las ventas de un contribuyente de IVA mínimo o Monotributo no llevan ítems gravados.
        if (bruto == 3)
            foreach (var (linea, indicador) in indicadores.Where(i => i.Indicador is "1" or "2" or "3" or "4" or "11" or "12"))
                h.Add(new("E05", $"Línea {linea}: con MntBruto 3 (IVA mínimo o Monotributo) no se puede usar el indicador de facturación {indicador}."));

        // G-C5: el CAE especial de IVA mínimo (2), Monotributo (3) o Monotributo Social MIDES (4) exige MntBruto 3, y al revés.
        if (especial is 2 or 3 or 4)
        {
            if (bruto != 3)
                h.Add(new("E05", $"Con CAEEspecial {especial} MntBruto tiene que ser 3 y es {bruto?.ToString() ?? "(ninguno)"}."));
            if (cuentaAjena)
                h.Add(new("E05", $"Con CAEEspecial {especial} el comprobante no puede ser de venta por cuenta ajena."));
        }
        else if (bruto == 3 && !cuentaAjena)
            h.Add(new("E05", "MntBruto 3 exige un CAE especial de IVA mínimo, Monotributo o Monotributo Social MIDES (CAEEspecial 2, 3 o 4)."));

        // G-C5 y G-C6: el CAE de exonerado (1) lleva causal y no admite ítems gravados; la causal solo va con ese CAE.
        var causal = cfe.CaeData.Texto("CausalCAEEsp");
        if (especial == 1)
        {
            if (causal is null)
                h.Add(new("E05", "Con CAEEspecial 1 (exonerado) falta CausalCAEEsp."));
            foreach (var (linea, indicador) in indicadores.Where(i => i.Indicador is "2" or "3" or "4" or "11" or "12"))
                h.Add(new("E05", $"Línea {linea}: con CAEEspecial 1 (exonerado) no se puede usar el indicador de facturación {indicador}."));
        }
        else if (causal is not null)
            h.Add(new("E05", $"CausalCAEEsp solo va con CAEEspecial 1 y el comprobante informa {especial?.ToString() ?? "un CAE común"}."));

        // A-C17: "no IVA al día" del mandante solo en cuenta ajena, y no con un CAE de Monotributo.
        var ivaAlDia = cfe.IdDoc.Texto("IVAalDia");
        if (ivaAlDia is "2" or "3" && !cuentaAjena)
            h.Add(new("E05", $"IVAalDia {ivaAlDia} se refiere al mandante: el comprobante tiene que ser de venta por cuenta ajena."));
        if (ivaAlDia is not null && especial is 3 or 4)
            h.Add(new("E05", $"Con CAEEspecial {especial} no puede existir IVAalDia."));

        // A-C18: el secreto profesional de una venta por cuenta ajena (2) solo va en esos comprobantes.
        if (cfe.IdDoc.Texto("SecProf") == "2" && !cuentaAjena)
            h.Add(new("E05", "SecProf 2 es de una venta por cuenta ajena y el comprobante no lo es."));

        // B-C4 16: lo usan los contribuyentes de IVA mínimo o Monotributo, o la cuenta ajena de un mandante que lo es. El
        // formato no lo da como validación: se advierte.
        if (bruto != 3 && !cuentaAjena)
            foreach (var (linea, _) in indicadores.Where(i => i.Indicador == "16"))
                h.Add(new("E05", $"Línea {linea}: el indicador de facturación 16 es de un contribuyente de IVA mínimo o Monotributo (MntBruto 3).", Confirmado: false));

        if (familia == Familia.Boleta)
            IndicadoresDeBoleta(cfe, indicadores, h);
    }

    /// <summary>B-C4 de la e-Boleta de entrada: cada indicador depende del documento del vendedor (A-C60) y de A-C21.</summary>
    private static void IndicadoresDeBoleta(CfeLeido cfe, List<(string? Linea, string? Indicador)> indicadores, List<Hallazgo> h)
    {
        var conRuc = cfe.TipoDocReceptor == "2";
        var compraParaReventa = cfe.IdDoc.Texto("IndCompraMEReventa") is "1" or "2";
        foreach (var (linea, indicador) in indicadores)
        {
            if (indicador == "13" && conRuc)
                h.Add(new("E05", $"Línea {linea}: el indicador 13 es de un vendedor no contribuyente y el receptor se identifica con RUC."));
            if (indicador is "14" or "15" && !conRuc)
                h.Add(new("E05", $"Línea {linea}: el indicador {indicador} es de un vendedor contribuyente: TipoDocRecep tiene que ser 2 (RUC)."));
            if (indicador == "17" && !compraParaReventa)
                h.Add(new("E05", $"Línea {linea}: el indicador 17 exige IndCompraMEReventa 1 o 2."));
            if (compraParaReventa && indicador != "17")
                h.Add(new("E05", $"Línea {linea}: con IndCompraMEReventa solo se puede usar el indicador 17 y es {indicador ?? "(ninguno)"}."));
        }
        if (cfe.IdDoc.Texto("IndCompraMEReventa") == "2" && cfe.IdDoc.Texto("SecProf") is not null)
            h.Add(new("E05", "Con IndCompraMEReventa 2 no puede existir SecProf."));
    }

    // --- Propietario de la mercadería de un remito ----------------------------------------------

    private static void Propietario(CfeLeido cfe, List<Hallazgo> h)
    {
        var id = cfe.IdDoc;
        var deTerceros = id.Texto("IndPropiedad") == "1";
        var tipo = id.Texto("TipoDocProp");
        var pais = id.Texto("CodPaisProp");
        var documento = id.Texto("DocProp");
        var extranjero = id.Texto("DocPropExt");
        if (!deTerceros && tipo is null && pais is null && documento is null && extranjero is null)
            return;

        if (deTerceros && tipo is null)
            h.Add(new("E05", "Con IndPropiedad 1 falta TipoDocProp."));
        if (deTerceros && id.Texto("RznSocProp") is not { Length: > 0 })
            h.Add(new("E05", "Con IndPropiedad 1 falta RznSocProp."));
        if (tipo is not null && (pais is null || (documento ?? extranjero) is null))
            h.Add(new("E05", "Con TipoDocProp tienen que existir CodPaisProp y DocProp o DocPropExt."));
        if (tipo is null && (pais is not null || documento is not null || extranjero is not null))
            h.Add(new("E05", "CodPaisProp, DocProp y DocPropExt requieren TipoDocProp."));
        if (documento is not null && extranjero is not null)
            h.Add(new("E05", "DocProp y DocPropExt no pueden existir a la vez."));

        DocumentoDe("TipoDocProp", tipo, "CodPaisProp", pais, "del propietario", documento, extranjero, "DocProp", "DocPropExt", h);
    }

    /// <summary>
    /// El país y el número que corresponden a un tipo de documento (2 RUC, 3 cédula, 4 otros, 5 pasaporte, 6 DNI y
    /// 7 NIFE), como en A-C6.3 a A-C6.5 y K-C3 a K-C4.
    /// </summary>
    private static void DocumentoDe(string campoTipo, string? tipo, string campoPais, string? pais, string deQuien, string? uruguayo, string? extranjero,
        string campoUruguayo, string campoExtranjero, List<Hallazgo> h)
    {
        if (tipo is "1" or "2" or "3" && pais is not null && pais != "UY")
            h.Add(new("E05", $"Con {campoTipo} {tipo} {campoPais} tiene que ser UY y es {pais}."));
        if (tipo == "6" && pais is not null && !PaisesDelDni.Contains(pais))
            h.Add(new("E05", $"Con {campoTipo} 6 (DNI) {campoPais} tiene que ser AR, BR, CL o PY y es {pais}."));
        if (tipo is "2" or "3" && campoUruguayo != campoExtranjero)
        {
            if (uruguayo is null)
                h.Add(new("E05", $"Con {campoTipo} {tipo} falta {campoUruguayo}."));
        }
        else if (tipo is "4" or "5" or "6" or "7" && campoUruguayo != campoExtranjero && extranjero is null)
            h.Add(new("E05", $"Con {campoTipo} {tipo} falta {campoExtranjero}."));

        var numero = uruguayo ?? extranjero;
        if (tipo == "2" && numero is not null && !Documentos.RutValido(numero))
            h.Add(new("E05", $"El RUC {deQuien} {numero} no tiene un dígito verificador válido."));
        if (tipo == "3" && numero is not null && !Documentos.CiValida(numero))
            h.Add(new("E05", $"La cédula {deQuien} {numero} no tiene un dígito verificador válido."));
    }

    // --- Agente o responsable y códigos de retención ---------------------------------------------

    /// <summary>
    /// B-C6 y la tabla de retenciones del ítem van juntos (salvo en el e-Resguardo, que no tiene B-C6), y B-C20 es un
    /// número mayor que cero de un código registrado en la DGI. Del 9999001 al 9999999 son de impuestos que la DGI no
    /// administra y no se validan.
    /// </summary>
    private void AgenteYCodigos(CfeLeido cfe, Familia familia, List<Hallazgo> h)
    {
        foreach (var item in cfe.Items)
        {
            if (familia == Familia.Resguardo)
                continue;
            var linea = item.Texto("NroLinDet");
            var agente = item.Texto("IndAgenteResp");
            var conTabla = item.Hijos("RetencPercep").Any();
            if (agente is not null && !conTabla)
                h.Add(new("E05", $"Línea {linea}: con IndAgenteResp tiene que existir la tabla de retenciones y percepciones del ítem (RetencPercep)."));
            if (agente is null && conTabla)
                h.Add(new("E05", $"Línea {linea}: la tabla de retenciones y percepciones del ítem requiere IndAgenteResp."));
        }

        var registrados = opciones.CodigosDeRetencion;
        var codigos = cfe.Items.SelectMany(i => i.Hijos("RetencPercep")).Concat(cfe.Totales.Hijos("RetencPercep"))
            .Select(r => r.Texto("CodRet")).OfType<string>().Distinct();
        foreach (var codigo in codigos)
        {
            if (XmlUtil.Entero(codigo) is not { } numero || numero <= 0 || !codigo.All(char.IsAsciiDigit))
                h.Add(new("E05", $"CodRet {codigo} tiene que ser un número mayor que cero (número de formulario más número de línea)."));
            else if (numero is < 9_999_001 or > 9_999_999 && registrados is { Count: > 0 } && !registrados.Contains(codigo))
                h.Add(new("E05", $"CodRet {codigo} no es un código de retención, percepción o crédito fiscal registrado en la DGI (Simulador:CodigosDeRetencion)."));
        }
    }

    // --- Complemento fiscal: el mandante ----------------------------------------------------------

    private static void Mandante(CfeLeido cfe, Familia familia, List<Hallazgo> h)
    {
        var cuentaAjena = cfe.Tipo is { } tipoCfe && TiposCfe.EsCuentaAjena(tipoCfe);
        var compraPorCuentaAjena = familia == Familia.Boleta && cfe.IdDoc.Texto("IndCompraMEReventa") == "2";
        if (cfe.ComplFiscal is null)
        {
            // La zona es obligatoria en la cuenta ajena, salvo con secreto profesional (A-C18 = 2), y en la compra de
            // moneda extranjera por cuenta ajena (A-C21 = 2).
            if (cuentaAjena && cfe.IdDoc.Texto("SecProf") != "2")
                h.Add(new("E05", "Los comprobantes de venta por cuenta ajena requieren el complemento fiscal (Compl_Fiscal) con el mandante."));
            if (compraPorCuentaAjena)
                h.Add(new("E05", "Con IndCompraMEReventa 2 la e-Boleta de entrada requiere el complemento fiscal (Compl_Fiscal) con el mandante."));
            return;
        }
        // Fuera de esos casos la zona no corresponde; el formato no dice que se rechace: se advierte.
        if (!cuentaAjena && !compraPorCuentaAjena)
            h.Add(new("E05", familia == Familia.Boleta
                ? "El complemento fiscal de una e-Boleta de entrada solo va con IndCompraMEReventa 2."
                : "El complemento fiscal solo va en los comprobantes de venta por cuenta ajena.", Confirmado: false));

        // Cifrado (xenc:EncryptedData) solo lo lee la DGI: no hay nada que controlar.
        if (cfe.ComplFiscal.Hijo("Compl_Fiscal_Data") is not { } datos)
            return;
        if (datos.Texto("RUCEmisor") is { } rut && !Documentos.RutValido(rut))
            h.Add(new("E05", $"El RUC del complemento fiscal {rut} no tiene un dígito verificador válido."));
        // K-C1 rechaza la cuenta ajena con A-C10 = 3, y A-C10 la exceptúa de exigir el CAE especial: no queda claro.
        if (cuentaAjena && cfe.MontosBrutos == 3)
            h.Add(new("E05", "El emisor de una venta por cuenta ajena no puede ser de IVA mínimo ni Monotributo (MntBruto 3).", Confirmado: false));

        var tipo = datos.Texto("TipoDocMdte");
        var documento = datos.Texto("DocMdte");
        if (compraPorCuentaAjena && tipo is not ("1" or "2" or "7"))
            h.Add(new("E05", $"Con IndCompraMEReventa 2 TipoDocMdte tiene que ser 1 (NIE), 2 (RUC) o 7 (NIFE) y es {tipo ?? "(ninguno)"}."));
        DocumentoDe("TipoDocMdte", tipo, "Pais", datos.Texto("Pais"), "del mandante", documento, documento, "DocMdte", "DocMdte", h);
        if (documento is not null && documento == cfe.RutEmisor)
            h.Add(new("E05", $"El mandante ({documento}) no puede ser el emisor del comprobante."));
        if (datos.Texto("NombreMdte") is not { Length: > 0 })
            h.Add(new("E05", "Falta NombreMdte en el complemento fiscal."));
    }
}
