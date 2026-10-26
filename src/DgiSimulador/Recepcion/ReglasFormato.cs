using System.Globalization;
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
/// de las notas y documentos. Escritos a partir del formato, sin código de los productos que se prueban.
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
        foreach (var item in cfe.Items)
        {
            var linea = item.Texto("NroLinDet");
            var indicador = item.Texto("IndFact");
            var informado = XmlUtil.Decimal(item.Texto("MontoItem"));
            var cant = XmlUtil.Decimal(item.Texto("Cantidad"));
            var precio = XmlUtil.Decimal(item.Texto("PrecioUnitario"));
            if (informado is null || cant is null || precio is null)
                continue;
            // Entrega gratuita (5): el monto del ítem no se calcula con el precio.
            if (indicador == "5")
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
        "1" => "MntNoGrv",
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
            if (CampoDe(dr.Texto("IndFactDR"), familia) is not { } campo)
                continue;
            if (dr.Texto("TpoDR") == "2")
            {
                // El formato no detalla sobre qué base se aplica un porcentaje global: ese total no se controla.
                camposSinControl.Add(campo);
                continue;
            }
            var valor = XmlUtil.Decimal(dr.Texto("ValorDR")) ?? 0m;
            sumas[campo] = sumas.GetValueOrDefault(campo) + (dr.Texto("TpoMovDR") == "D" ? -valor : valor);
        }

        var brutos = cfe.MontosBrutos == 1;
        foreach (var (campo, suma) in sumas)
        {
            if (camposSinControl.Contains(campo))
                continue;
            var esperado = suma;
            var confirmado = true;
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

        var total = cfe.MontoInformado("MntTotal");
        if (total is { } t && familia != Familia.Resguardo)
        {
            var componentes = cfe.Neto + cfe.Iva;
            if (Math.Abs(componentes - t) > Tolerancia)
                h.Add(new("E05", $"MntTotal informa {F(t)} y la suma de los montos netos, no gravados e IVA da {F(componentes)}."));
        }

        if (cfe.MontoInformado("MntPagar") is { } pagar && total is { } mntTotal)
        {
            var retenido = cfe.Monto("MntTotRetenido");
            var esperado = mntTotal + retenido + cfe.Monto("MontoNF");
            if (Math.Abs(esperado - pagar) > Tolerancia)
                h.Add(new("E05", $"MntPagar informa {F(pagar)} y MntTotal + MntTotRetenido + MontoNF da {F(esperado)}.",
                    Confirmado: retenido == 0));
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
        var retenido = cfe.Monto("MntTotRetenido");
        var suma = totales.Values.Sum();
        if (Math.Abs(retenido - suma) > Tolerancia)
            h.Add(new("E05", $"MntTotRetenido informa {F(retenido)} y la suma de RetencPercep de los totales da {F(suma)}."));
    }

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

        if (familia == Familia.Factura)
        {
            if (tipoDoc != "2" || doc is null)
                h.Add(new("E05", "La e-Factura requiere un receptor con RUC (TipoDocRecep 2 y DocRecep)."));
            foreach (var campo in new[] { "RznSocRecep", "DirRecep", "CiudadRecep" })
                if (string.IsNullOrWhiteSpace(r.Texto(campo)))
                    h.Add(new("E05", $"La e-Factura requiere {campo} del receptor."));
        }

        if (familia == Familia.Exportacion && (r is null || string.IsNullOrWhiteSpace(r.Texto("RznSocRecep"))))
            h.Add(new("E05", "La e-Factura de Exportación requiere la razón social del receptor.", Confirmado: false));

        if (familia == Familia.Ticket && TopeUi(cfe) is { } tope && cfe.Neto * (cfe.TipoCambio ?? 1m) > tope && (tipoDoc is null || cfe.DocReceptor is null))
            h.Add(new("E05", $"El e-Ticket supera el tope de 5.000 UI ({F(tope)} pesos netos) y no identifica al receptor."));
    }

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

        foreach (var referencia in cfe.Referencias)
        {
            var global = referencia.Texto("IndGlobal") == "1";
            var especifica = referencia.Texto("TpoDocRef") is not null || referencia.Texto("NroCFERef") is not null;
            if (global && especifica)
                h.Add(new("E05", $"La referencia {referencia.Texto("NroLinRef")} es global (IndGlobal) y también indica un CFE."));
            if (!global && (referencia.Texto("TpoDocRef") is null || referencia.Texto("Serie") is null || referencia.Texto("NroCFERef") is null))
                h.Add(new("E05", $"La referencia {referencia.Texto("NroLinRef")} no es global y le falta el tipo, la serie o el número del CFE."));
            if (global && string.IsNullOrWhiteSpace(referencia.Texto("RazonRef")))
                h.Add(new("E05", $"La referencia global {referencia.Texto("NroLinRef")} requiere RazonRef.", Confirmado: false));
        }
    }

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
