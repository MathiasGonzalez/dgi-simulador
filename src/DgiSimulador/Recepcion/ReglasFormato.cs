using System.Globalization;
using System.Xml;
using DgiSimulador.Xml;

namespace DgiSimulador.Recepcion;

/// <summary>
/// Un problema que encontró un control. <see cref="Confirmado"/> distingue las reglas que están en el formato de
/// las inferidas: estas solo se anotan como advertencias, salvo en modo estricto.
/// </summary>
internal sealed record Hallazgo(string Codigo, string Detalle, bool Confirmado = true);

/// <summary>
/// Controles del Formato de los CFE que el XSD no expresa (motivo E05, salvo el tipo de cambio que es E10):
/// montos de los ítems, totales por indicador de facturación, IVA, total a pagar, retenciones, receptor, referencias
/// de las notas y documentos. Escritos a partir del Formato de los CFE v25.2, sin código de los productos que se prueban.
/// </summary>
internal sealed class ReglasFormato(OpcionesSimulador opciones)
{
    private decimal Tolerancia => opciones.Tolerancia;

    internal List<Hallazgo> Controlar(CfeLeido cfe)
    {
        var hallazgos = new List<Hallazgo>();
        if (cfe.Familia is not { } familia || cfe.Totales is null)
            return hallazgos;

        Emisor(cfe, hallazgos);
        Lineas(cfe, familia, hallazgos);
        if (familia != Familia.Remito)
            Totales(cfe, familia, hallazgos);
        Retenciones(cfe, familia, hallazgos);
        Receptor(cfe, familia, hallazgos);
        Notas(cfe, hallazgos);
        TipoDeCambio(cfe, hallazgos);
        return hallazgos;
    }

    private static void Emisor(CfeLeido cfe, List<Hallazgo> h)
    {
        if (cfe.RutEmisor is { } rut && !Documentos.RutValido(rut))
            h.Add(new("E05", $"El RUC del emisor {rut} no tiene un dígito verificador válido."));
    }

    // --- Ítems ------------------------------------------------------------------------------

    private void Lineas(CfeLeido cfe, Familia familia, List<Hallazgo> h)
    {
        var cantidad = XmlUtil.Entero(cfe.Totales.Texto("CantLinDet"));
        if (cantidad is { } c && c != cfe.Items.Count)
            h.Add(new("E05", $"CantLinDet informa {c} líneas y el detalle tiene {cfe.Items.Count}."));

        if (familia is Familia.Remito or Familia.Resguardo)
            return;
        var cobranzaPropia = cfe.IdDoc.Texto("IndCobPropia") == "1";
        foreach (var item in cfe.Items)
        {
            var linea = item.Texto("NroLinDet");
            var indicador = item.Texto("IndFact");
            // A-C20: la cobranza propia solo admite ítems no facturables (6 o 7).
            if (cobranzaPropia && indicador is not ("6" or "7"))
                h.Add(new("E05", $"Línea {linea}: con IndCobPropia el indicador de facturación tiene que ser 6 o 7 y es {indicador ?? "(ninguno)"}."));
            var informado = XmlUtil.Decimal(item.Texto("MontoItem"));
            var cant = XmlUtil.Decimal(item.Texto("Cantidad"));
            var precio = XmlUtil.Decimal(item.Texto("PrecioUnitario"));
            // B-C11: el precio es mayor que cero, salvo en la entrega gratuita (5), que es cero, y en el ítem a rebajar (8).
            if (precio is { } p0 && (indicador == "5" ? p0 != 0 : indicador == "8" ? p0 < 0 : p0 <= 0))
                h.Add(new("E05", indicador == "5"
                    ? $"Línea {linea}: una entrega gratuita (indicador 5) tiene PrecioUnitario 0 y informa {p0.ToString(CultureInfo.InvariantCulture)}."
                    : $"Línea {linea}: PrecioUnitario {p0.ToString(CultureInfo.InvariantCulture)} tiene que ser mayor que cero."));
            // B-C24: el monto de una entrega gratuita (5) es cero.
            if (indicador == "5")
            {
                if (informado is { } gratuito && gratuito != 0)
                    h.Add(new("E05", $"Línea {linea}: una entrega gratuita (indicador 5) tiene MontoItem 0 y informa {F(gratuito)}."));
                continue;
            }
            if (informado is null || cant is null || precio is null)
                continue;
            var esperado = Redondear(cant.Value * precio.Value)
                           - (XmlUtil.Decimal(item.Texto("DescuentoMonto")) ?? 0m)
                           + (XmlUtil.Decimal(item.Texto("RecargoMnt")) ?? 0m);
            if (Math.Abs(esperado - informado.Value) > Tolerancia)
                h.Add(new("E05", $"Línea {linea}: MontoItem {F(informado.Value)} no es Cantidad × PrecioUnitario − DescuentoMonto + RecargoMnt = {F(esperado)}."));

            if (item.Texto("DescuentoPct") is { } pct && XmlUtil.Decimal(pct) is { } p && item.Texto("DescuentoMonto") is { } dm
                && XmlUtil.Decimal(dm) is { } monto && Math.Abs(Redondear(cant.Value * precio.Value * p / 100m) - monto) > Tolerancia)
                h.Add(new("E05", $"Línea {linea}: DescuentoMonto {F(monto)} no es el {pct}% de Cantidad × PrecioUnitario.", Confirmado: false));
        }
    }

    // --- Totales ------------------------------------------------------------------------------

    /// <summary>A qué total de <c>Totales</c> va el monto de cada indicador de facturación.</summary>
    private static string? CampoDe(string? indicador, Familia familia) => indicador switch
    {
        "1" or "16" => "MntNoGrv",
        "2" => "MntNetoIvaTasaMin",
        "3" => "MntNetoIVATasaBasica",
        "4" => "MntNetoIVAOtra",
        "6" or "7" => "MontoNF",
        "8" when familia == Familia.RemitoExportacion => "MntExpoyAsim",
        "10" => "MntExpoyAsim",
        "11" => "MntImpuestoPerc",
        "12" => "MntIVaenSusp",
        "13" or "14" or "15" or "17" => "MntNoGrv",
        null when familia is Familia.Exportacion or Familia.RemitoExportacion => "MntExpoyAsim",
        _ => null,
    };

    /// <summary>Signo con que suma el indicador: 7 (no facturable negativo) y 8 (ítem a rebajar) restan.</summary>
    private static int Signo(string? indicador) => indicador is "7" or "8" ? -1 : 1;

    private void Totales(CfeLeido cfe, Familia familia, List<Hallazgo> h)
    {
        var sumas = new Dictionary<string, decimal>();
        var indicadoresSinControl = new SortedSet<string>();
        var camposSinControl = new HashSet<string>();
        foreach (var item in cfe.Items)
        {
            var indicador = item.Texto("IndFact");
            if (CampoDe(indicador, familia) is not { } campo)
            {
                if (indicador is not null and not "5")
                    indicadoresSinControl.Add(indicador);
                continue;
            }
            sumas[campo] = sumas.GetValueOrDefault(campo) + Signo(indicador) * (XmlUtil.Decimal(item.Texto("MontoItem")) ?? 0m);
        }

        foreach (var dr in cfe.DescuentosGlobales)
        {
            var indicador = dr.Texto("IndFactDR");
            if (CampoDe(indicador, familia) is not { } campo)
                continue;
            if (dr.Texto("TpoDR") == "2")
            {
                // El formato no detalla sobre qué base se aplica un porcentaje global: ese total no se controla.
                camposSinControl.Add(campo);
                continue;
            }
            // A-C129: sobre los ítems con indicador 7 el descuento suma y el recargo resta.
            var valor = XmlUtil.Decimal(dr.Texto("ValorDR")) ?? 0m;
            sumas[campo] = sumas.GetValueOrDefault(campo) + Signo(indicador) * (dr.Texto("TpoMovDR") == "D" ? -valor : valor);
        }

        // A-C113 del e-Remito de Exportación escribe ∑B-C24 − ∑B-C24 (si B-C4=8) y no aclara si la primera suma incluye
        // los ítems a rebajar: con ítems a rebajar el total se advierte.
        var hayRebajas = familia == Familia.RemitoExportacion && cfe.Items.Any(i => i.Texto("IndFact") == "8");
        var brutos = cfe.MontosBrutos == 1;
        foreach (var (campo, suma) in sumas)
        {
            if (camposSinControl.Contains(campo))
                continue;
            var esperado = suma;
            var confirmado = !(hayRebajas && campo == "MntExpoyAsim");
            if (brutos && campo is "MntNetoIvaTasaMin" or "MntNetoIVATasaBasica")
            {
                // Con precios con IVA incluido el neto es el bruto dividido (1 + tasa): el redondeo no está confirmado.
                var tasa = campo == "MntNetoIvaTasaMin" ? opciones.TasaMinima : opciones.TasaBasica;
                esperado = Redondear(suma / (1 + tasa / 100m));
                confirmado = false;
            }
            var informado = cfe.Monto(campo);
            if (Math.Abs(esperado - informado) > Tolerancia)
                h.Add(new("E05", $"{campo} informa {F(informado)} y la suma de los ítems da {F(esperado)}.", confirmado));
        }
        foreach (var indicador in indicadoresSinControl)
            h.Add(new("E05", $"El simulador no controla el total de los ítems con indicador de facturación {indicador}.", Confirmado: false));

        Iva(cfe, "MntNetoIvaTasaMin", "IVATasaMin", "MntIVATasaMin", opciones.TasaMinima, h);
        Iva(cfe, "MntNetoIVATasaBasica", "IVATasaBasica", "MntIVATasaBasica", opciones.TasaBasica, h);

        // A-C118 y A-C123: el neto y el IVA a otra tasa van juntos.
        var netoOtra = cfe.Monto("MntNetoIVAOtra");
        var ivaOtra = cfe.Monto("MntIVAOtra");
        if (netoOtra > 0 != ivaOtra > 0)
            h.Add(new("E05", $"MntNetoIVAOtra ({F(netoOtra)}) y MntIVAOtra ({F(ivaOtra)}) tienen que ser los dos mayores que cero o ninguno."));

        var total = cfe.MontoInformado("MntTotal");
        if (total is { } t && familia != Familia.Resguardo)
        {
            var componentes = cfe.Neto + cfe.Iva;
            if (Math.Abs(componentes - t) > Tolerancia)
                h.Add(new("E05", $"MntTotal informa {F(t)} y la suma de los montos netos, no gravados e IVA da {F(componentes)}."));
        }

        // A-C20: la cobranza propia no lleva descuentos ni recargos globales, e informa MontoNF y MntPagar.
        if (cfe.IdDoc.Texto("IndCobPropia") == "1")
        {
            if (cfe.DescuentosGlobales.Count > 0)
                h.Add(new("E05", "Con IndCobPropia no puede haber descuentos ni recargos globales."));
            if (cfe.MontoInformado("MontoNF") is null || cfe.MontoInformado("MntPagar") is null)
                h.Add(new("E05", "Con IndCobPropia tienen que estar MontoNF y MntPagar."));
        }

        if (cfe.MontoInformado("MntPagar") is { } pagar && total is { } mntTotal)
        {
            // A-C130: MntTotal + MntTotRetenido + MontoNF, o cero si esa suma es negativa. En la e-Boleta de entrada con
            // montos con IMEBA y adicionales (MntBruto 2) la retención resta.
            var resta = familia == Familia.Boleta && cfe.MontosBrutos == 2;
            var esperado = mntTotal + (resta ? -1 : 1) * cfe.Monto("MntTotRetenido") + cfe.Monto("MontoNF");
            if (Math.Abs(esperado - pagar) > Tolerancia && !(esperado < 0 && pagar == 0))
                h.Add(new("E05", $"MntPagar informa {F(pagar)} y MntTotal {(resta ? "−" : "+")} MntTotRetenido + MontoNF da {F(esperado)}."));
        }
    }

    private void Iva(CfeLeido cfe, string campoNeto, string campoTasa, string campoIva, decimal tasaVigente, List<Hallazgo> h)
    {
        var neto = cfe.Monto(campoNeto);
        var tasa = cfe.MontoInformado(campoTasa);
        var iva = cfe.MontoInformado(campoIva);
        if (neto != 0 && tasa is null)
            h.Add(new("E05", $"Hay {campoNeto} y falta {campoTasa}."));
        if (tasa is { } tt && tt != tasaVigente)
            h.Add(new("E05", $"{campoTasa} informa {F(tt)} y la tasa vigente es {F(tasaVigente)}."));
        if (neto == 0 && iva is null)
            return;
        var esperado = Redondear(neto * (tasa ?? tasaVigente) / 100m);
        if (Math.Abs(esperado - (iva ?? 0m)) > Tolerancia)
            h.Add(new("E05", $"{campoIva} informa {F(iva ?? 0m)} y {campoNeto} × {campoTasa} da {F(esperado)}."));
    }

    // --- Retenciones y percepciones -----------------------------------------------------------

    private void Retenciones(CfeLeido cfe, Familia familia, List<Hallazgo> h)
    {
        var porCodigo = new Dictionary<string, decimal>();
        var hayAnulaciones = false;
        foreach (var item in cfe.Items)
        {
            // Indicador 9 del e-Resguardo: el ítem anula retenciones de otro resguardo y resta en los totales.
            var signo = familia == Familia.Resguardo && item.Texto("IndFact") == "9" ? -1 : 1;
            hayAnulaciones |= signo < 0;
            foreach (var ret in item.Hijos("RetencPercep"))
                if (ret.Texto("CodRet") is { } codigo)
                    porCodigo[codigo] = porCodigo.GetValueOrDefault(codigo) + signo * (XmlUtil.Decimal(ret.Texto("ValRetPerc")) ?? 0m);
        }

        var totales = cfe.Totales.Hijos("RetencPercep")
            .Where(r => r.Texto("CodRet") is not null)
            .GroupBy(r => r.Texto("CodRet")!)
            .ToDictionary(g => g.Key, g => g.Sum(r => XmlUtil.Decimal(r.Texto("ValRetPerc")) ?? 0m));
        if (porCodigo.Count == 0 && totales.Count == 0)
            return;

        foreach (var codigo in porCodigo.Keys.Union(totales.Keys))
        {
            var items = porCodigo.GetValueOrDefault(codigo);
            var informado = totales.GetValueOrDefault(codigo);
            if (Math.Abs(items - informado) > Tolerancia)
                h.Add(new("E05", $"La retención o percepción {codigo} suma {F(items)} en los ítems y {F(informado)} en los totales.",
                    Confirmado: !hayAnulaciones));
        }
        // A-C125 y A-C125.1: los códigos del formulario 2181 son créditos fiscales y van en MntTotCredFisc.
        var retenido = cfe.Monto("MntTotRetenido");
        var suma = totales.Where(t => !EsCreditoFiscal(t.Key)).Sum(t => t.Value);
        if (Math.Abs(retenido - suma) > Tolerancia)
            h.Add(new("E05", $"MntTotRetenido informa {F(retenido)} y la suma de RetencPercep de los totales, sin los créditos fiscales (2181), da {F(suma)}."));
        var credito = cfe.Monto("MntTotCredFisc");
        var sumaCredito = totales.Where(t => EsCreditoFiscal(t.Key)).Sum(t => t.Value);
        if (Math.Abs(credito - sumaCredito) > Tolerancia)
            h.Add(new("E05", $"MntTotCredFisc informa {F(credito)} y la suma de los créditos fiscales (2181) de los totales da {F(sumaCredito)}."));
    }

    private static bool EsCreditoFiscal(string codigo) => codigo.StartsWith("2181", StringComparison.Ordinal);

    // --- Receptor -----------------------------------------------------------------------------

    private void Receptor(CfeLeido cfe, Familia familia, List<Hallazgo> h)
    {
        var r = cfe.Receptor;
        var tipoDoc = cfe.TipoDocReceptor;
        var doc = r.Texto("DocRecep");
        if (tipoDoc == "2" && doc is not null && !Documentos.RutValido(doc))
            h.Add(new("E05", $"El RUC del receptor {doc} no tiene un dígito verificador válido."));
        if (tipoDoc == "3" && doc is not null && !Documentos.CiValida(doc))
            h.Add(new("E05", $"La cédula del receptor {doc} no tiene un dígito verificador válido."));

        // A-C61, A-C62 y A-C62.1: el país y el número que corresponden a cada tipo de documento.
        var pais = r.Texto("CodPaisRecep");
        if (tipoDoc is "1" or "2" or "3")
        {
            if (pais is not null && pais != "UY")
                h.Add(new("E05", $"Con TipoDocRecep {tipoDoc} CodPaisRecep tiene que ser UY y es {pais}."));
            if (doc is null)
                h.Add(new("E05", $"Con TipoDocRecep {tipoDoc} falta DocRecep."));
        }
        else if (tipoDoc is "4" or "5" or "6" or "7")
        {
            if (tipoDoc == "6" && pais is not null && pais is not ("AR" or "BR" or "CL" or "PY"))
                h.Add(new("E05", $"Con TipoDocRecep 6 (DNI) CodPaisRecep tiene que ser AR, BR, CL o PY y es {pais}."));
            if (r.Texto("DocRecepExt") is null)
                h.Add(new("E05", $"Con TipoDocRecep {tipoDoc} falta DocRecepExt."));
        }

        // A-C18: con secreto profesional no se exigen los datos del receptor.
        if (cfe.IdDoc.Texto("SecProf") is "1" or "2")
            return;

        var nombre = cfe.Tipo is { } t ? TiposCfe.Nombre(t) : TiposCfe.Raiz(familia);
        var identificado = tipoDoc is not null && cfe.DocReceptor is not null;
        switch (familia)
        {
            case Familia.Factura:
                if (tipoDoc != "2" || doc is null)
                    h.Add(new("E05", $"{nombre} requiere un receptor con RUC (TipoDocRecep 2 y DocRecep)."));
                Exigir(r, nombre, h, "RznSocRecep", "DirRecep", "CiudadRecep");
                break;
            case Familia.Exportacion or Familia.RemitoExportacion:
                Exigir(r, nombre, h, "RznSocRecep", "DirRecep", "CiudadRecep", "DeptoRecep", "PaisRecep");
                break;
            case Familia.Remito or Familia.Resguardo:
                if (!identificado)
                    h.Add(new("E05", $"{nombre} requiere identificar al receptor (TipoDocRecep y documento)."));
                if (familia == Familia.Resguardo)
                    Exigir(r, nombre, h, "RznSocRecep");
                break;
            case Familia.Ticket when !identificado:
                // Tabla E: con retenciones o percepciones, o por encima del tope de 5.000 UI, se identifica al receptor.
                if (cfe.Totales.Hijos("RetencPercep").Any() || cfe.Items.Any(i => i.Hijos("RetencPercep").Any()))
                    h.Add(new("E05", $"{nombre} documenta retenciones o percepciones y no identifica al receptor."));
                else if (TopeUi(cfe) is { } tope && NetoEnPesos(cfe) > tope)
                    h.Add(new("E05", $"{nombre} supera el tope de 5.000 UI ({F(tope)} pesos netos) y no identifica al receptor."));
                break;
            case Familia.Boleta:
                // Compra de moneda extranjera para reventa (A-C21) de un receptor sin RUC, por debajo del tope: no se identifica.
                if (cfe.IdDoc.Texto("IndCompraMEReventa") is "1" or "2" && tipoDoc != "2"
                    && (TopeUi(cfe) is not { } topeBoleta || NetoEnPesos(cfe) <= topeBoleta))
                    break;
                if (!identificado)
                    h.Add(new("E05", $"{nombre} requiere identificar al receptor (TipoDocRecep y documento)."));
                Exigir(r, nombre, h, "RznSocRecep", "DirRecep", "CiudadRecep");
                break;
        }
    }

    private static void Exigir(XmlElement? receptor, string comprobante, List<Hallazgo> h, params string[] campos)
    {
        foreach (var campo in campos)
            if (string.IsNullOrWhiteSpace(receptor.Texto(campo)))
                h.Add(new("E05", $"{comprobante} requiere {campo} del receptor."));
    }

    private static decimal NetoEnPesos(CfeLeido cfe) => cfe.Neto * (cfe.TipoCambio ?? 1m);

    /// <summary>5.000 UI al valor del 31/12 del año anterior al comprobante, si está configurado.</summary>
    private decimal? TopeUi(CfeLeido cfe)
    {
        if (cfe.FechaEmision is not { } fecha)
            return null;
        var anio = (fecha.Year - 1).ToString(CultureInfo.InvariantCulture);
        return opciones.UnidadIndexada.TryGetValue(anio, out var ui) ? Redondear(5000m * ui) : null;
    }

    // --- Notas y tipo de cambio ---------------------------------------------------------------

    private static void Notas(CfeLeido cfe, List<Hallazgo> h)
    {
        if (cfe.Tipo is { } tipo && TiposCfe.EsNota(tipo) && cfe.Referencias.Count == 0)
            h.Add(new("E05", "Las notas de crédito y débito requieren la zona de referencia al CFE que corrigen."));
        // A-C20: la cobranza propia referencia los comprobantes que cobra.
        if (cfe.IdDoc.Texto("IndCobPropia") == "1" && cfe.Referencias.Count == 0)
            h.Add(new("E05", "Con IndCobPropia hay que referenciar los comprobantes que se cobran."));

        var referenciados = new HashSet<(string, string, long)>();
        foreach (var referencia in cfe.Referencias)
        {
            var linea = referencia.Texto("NroLinRef");
            if (referencia.Texto("IndGlobal") == "1")
            {
                // F-C3 a F-C5 y F-C7 a F-C10 no van en una referencia global, que explica el motivo en RazonRef (F-C6).
                var sobran = CamposDelCfeReferenciado.Where(c => referencia.Texto(c) is not null).ToList();
                if (sobran.Count > 0)
                    h.Add(new("E05", $"La referencia {linea} es global (IndGlobal) y también informa {string.Join(", ", sobran)}."));
                if (string.IsNullOrWhiteSpace(referencia.Texto("RazonRef")))
                    h.Add(new("E05", $"La referencia global {linea} requiere RazonRef."));
                continue;
            }
            if (referencia.Texto("TpoDocRef") is not { } tipoRef || referencia.Texto("Serie") is not { } serie
                || XmlUtil.Entero(referencia.Texto("NroCFERef")) is not { } nro)
                h.Add(new("E05", $"La referencia {linea} no es global y le falta el tipo, la serie o el número del CFE."));
            else if (!referenciados.Add((tipoRef, serie, nro)))
                h.Add(new("E05", $"La referencia {linea} repite el CFE {tipoRef} {serie}{nro} de otra línea."));
            // F-C10: obligatorio si el CFE referenciado no es en pesos.
            if (referencia.Texto("TpoMonedaRef") is { } moneda && moneda != "UYU" && referencia.Texto("TpoCambioRef") is null)
                h.Add(new("E05", $"La referencia {linea} es a un CFE en {moneda} y no informa TpoCambioRef."));
        }
    }

    private static readonly string[] CamposDelCfeReferenciado =
        ["TpoDocRef", "Serie", "NroCFERef", "FechaCFEref", "MntCFEref", "TpoMonedaRef", "TpoCambioRef"];

    private void TipoDeCambio(CfeLeido cfe, List<Hallazgo> h)
    {
        if (cfe.Moneda is null)
            return;
        if (cfe.Moneda == "UYU")
        {
            if (cfe.TipoCambio is { } tc && tc != 1m)
                h.Add(new("E10", $"El comprobante es en pesos y TpoCambio informa {F(tc)}.", Confirmado: false));
            return;
        }
        if (cfe.TipoCambio is not { } valor)
        {
            h.Add(new("E10", $"El comprobante es en {cfe.Moneda} y no informa TpoCambio."));
            return;
        }
        if (valor <= 0)
            h.Add(new("E10", $"TpoCambio {F(valor)} no es positivo."));
        else if (opciones.TiposDeCambio.TryGetValue(cfe.Moneda, out var rango) && (valor < rango.Minimo || valor > rango.Maximo))
            h.Add(new("E10", $"TpoCambio {valor.ToString(CultureInfo.InvariantCulture)} para {cfe.Moneda} está fuera del rango configurado ({rango.Minimo}–{rango.Maximo})."));
    }

    internal static decimal Redondear(decimal valor) => Math.Round(valor, 2, MidpointRounding.AwayFromZero);

    private static string F(decimal valor) => valor.ToString("0.00", CultureInfo.InvariantCulture);
}

/// <summary>Dígitos verificadores de los documentos uruguayos.</summary>
internal static class Documentos
{
    /// <summary>RUT: 12 dígitos, módulo 11 con pesos 4,3,2,9,8,7,6,5,4,3,2 sobre los primeros 11 (un resto de 1 no da RUT válido).</summary>
    internal static bool RutValido(string rut)
    {
        if (rut.Length != 12 || !rut.All(char.IsAsciiDigit))
            return false;
        int[] pesos = [4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2];
        var suma = pesos.Select((p, i) => p * (rut[i] - '0')).Sum();
        var dv = 11 - suma % 11;
        return dv != 10 && dv % 11 == rut[11] - '0';
    }

    /// <summary>Cédula: hasta 8 dígitos (con el verificador), pesos 2,9,8,7,6,3,4 y módulo 10.</summary>
    internal static bool CiValida(string ci)
    {
        var digitos = new string(ci.Where(char.IsAsciiDigit).ToArray());
        if (digitos.Length is < 7 or > 8 || digitos.Length != ci.Count(c => !char.IsWhiteSpace(c) && c != '.' && c != '-'))
            return false;
        digitos = digitos.PadLeft(8, '0');
        int[] pesos = [2, 9, 8, 7, 6, 3, 4];
        var suma = pesos.Select((p, i) => p * (digitos[i] - '0')).Sum();
        return (10 - suma % 10) % 10 == digitos[7] - '0';
    }
}
