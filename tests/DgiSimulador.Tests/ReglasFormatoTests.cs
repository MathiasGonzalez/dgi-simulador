using System.Xml;
using DgiSimulador.Estado;
using DgiSimulador.Intercambio;
using DgiSimulador.Recepcion;
using DgiSimulador.Xml;

namespace DgiSimulador.Tests;

/// <summary>Reglas del Formato de los CFE v25.2 (E05) sobre CFE del generador, modificados para cada caso.</summary>
public sealed class ReglasFormatoTests
{
    private const string Rut = "211003420017";

    private static GeneradorCfe Cfe(int tipo, params LineaCfe[] lineas) => new()
    {
        Tipo = tipo,
        Fecha = EmisorPrueba.Hoy,
        Firma = DateTimeOffset.UtcNow,
        RutEmisor = Rut,
        Receptor = TiposCfe.FamiliaDe(tipo) == Familia.Factura ? new ReceptorCfe("2", "UY", Rut, "Cliente S.A.") : null,
        Lineas = lineas.Length > 0 ? lineas : [new("Servicio", 1, 100m)],
        Cae = new Cae(1, Rut, tipo, "A", 1, 1000, EmisorPrueba.Hoy, EmisorPrueba.Hoy.AddYears(1), null, null),
    };

    private static List<Hallazgo> Controlar(GeneradorCfe cfe)
    {
        var doc = XmlUtil.Cargar(cfe.Xml(), preservarEspacios: false);
        cfe.Modificar?.Invoke(doc);
        return new ReglasFormato(new OpcionesSimulador()).Controlar(CfeLeido.Leer(doc.DocumentElement!));
    }

    private static void SinHallazgos(List<Hallazgo> hallazgos) =>
        Assert.True(hallazgos.Count == 0, string.Join(Environment.NewLine, hallazgos.Select(x => $"{x.Codigo}: {x.Detalle}")));

    private static void Rechaza(List<Hallazgo> hallazgos, string texto) =>
        Assert.True(hallazgos.Any(x => x.Confirmado && x.Codigo == "E05" && x.Detalle.Contains(texto)),
            $"Falta un E05 con \"{texto}\":{Environment.NewLine}" + string.Join(Environment.NewLine, hallazgos.Select(x => $"{x.Codigo}: {x.Detalle}")));

    /// <summary>Pone el texto de un hijo, creándolo al final si no está.</summary>
    private static XmlElement Poner(XmlElement padre, string nombre, string valor)
    {
        var elemento = padre.Hijo(nombre) ?? (XmlElement)padre.AppendChild(padre.OwnerDocument.CreateElement(nombre, Ns.Cfe))!;
        elemento.InnerText = valor;
        return elemento;
    }

    private static XmlElement Agregar(XmlElement padre, string nombre, params (string Nombre, string Valor)[] hijos)
    {
        var elemento = (XmlElement)padre.AppendChild(padre.OwnerDocument.CreateElement(nombre, Ns.Cfe))!;
        foreach (var (hijo, valor) in hijos)
            Poner(elemento, hijo, valor);
        return elemento;
    }

    private static XmlElement Comprobante(XmlDocument doc) => (XmlElement)doc.DocumentElement!.FirstChild!;

    private static XmlElement IdDoc(XmlDocument doc) => doc.DocumentElement.Buscar("IdDoc")!;

    private static XmlElement Totales(XmlDocument doc) => doc.DocumentElement.Buscar("Totales")!;

    private static XmlElement PrimeraReferencia(XmlDocument doc) => doc.DocumentElement.Buscar("Referencia")!.Hijo("Referencia")!;

    private static void Retencion(XmlDocument doc, string codigo, decimal valor)
    {
        Agregar(doc.DocumentElement.Buscar("Item")!, "RetencPercep", ("CodRet", codigo), ("ValRetPerc", XmlUtil.Numero(valor)));
        Agregar(Totales(doc), "RetencPercep", ("CodRet", codigo), ("ValRetPerc", XmlUtil.Numero(valor)));
    }

    // --- Ítems y totales ------------------------------------------------------------------------

    [Fact]
    public void EntregaGratuita_PrecioYMontoEnCero()
    {
        SinHallazgos(Controlar(Cfe(101, new("Servicio", 1, 100m), new("Regalo", 1, 0m, Indicador: 5))));
        var hallazgos = Controlar(Cfe(101, new("Servicio", 1, 100m), new("Regalo", 1, 10m, Indicador: 5)));
        Rechaza(hallazgos, "tiene PrecioUnitario 0");
        Rechaza(hallazgos, "tiene MontoItem 0");
    }

    [Fact]
    public void PrecioCero_E05() => Rechaza(Controlar(Cfe(101, new LineaCfe("Servicio", 1, 0m))), "tiene que ser mayor que cero");

    [Fact]
    public void Indicador16_SumaEnMntNoGrv()
    {
        var cfe = Cfe(101, new LineaCfe("Venta de monotributista", 1, 100m, Indicador: 16)) with
        {
            Modificar = doc =>
            {
                Poner(Totales(doc), "MntNoGrv", "100.00");
                Poner(Totales(doc), "MntTotal", "100.00");
                Poner(Totales(doc), "MntPagar", "100.00");
            },
        };
        SinHallazgos(Controlar(cfe));
    }

    [Fact]
    public void DescuentoGlobalSobreNoFacturableNegativo_SumaEnMontoNF()
    {
        // A-C129: −50 del ítem con indicador 7, más el descuento global de 10 asignado a ese indicador.
        var cfe = Cfe(111, new("Servicio", 1, 100m), new("Seña", 1, 50m, Indicador: 7)) with
        {
            Modificar = doc =>
            {
                Agregar(Agregar(Comprobante(doc), "DscRcgGlobal"), "DRG_Item", ("NroLinDR", "1"), ("TpoMovDR", "D"), ("TpoDR", "1"),
                    ("GlosaDR", "Descuento"), ("ValorDR", "10.00"), ("IndFactDR", "7"));
                Poner(Totales(doc), "MontoNF", "-40.00");
                Poner(Totales(doc), "MntPagar", "82.00");
            },
        };
        SinHallazgos(Controlar(cfe));
    }

    [Fact]
    public void IvaOtraTasaSinNeto_E05() =>
        Rechaza(Controlar(Cfe(101) with { Modificar = doc => Poner(Totales(doc), "MntIVAOtra", "5.00") }), "MntNetoIVAOtra");

    [Fact]
    public void MntPagarConRetenciones_SeControla()
    {
        GeneradorCfe Con(string pagar) => Cfe(111) with
        {
            Modificar = doc =>
            {
                Retencion(doc, "2183211", 2m);
                Poner(Totales(doc), "MntTotRetenido", "2.00");
                Poner(Totales(doc), "MntPagar", pagar);
            },
        };
        SinHallazgos(Controlar(Con("124.00")));
        Rechaza(Controlar(Con("122.00")), "MntPagar");
    }

    [Fact]
    public void MntPagarNegativo_AdmiteCero()
    {
        var cfe = Cfe(101, new("Servicio", 1, 10m), new("Devolución de seña", 1, 50m, Indicador: 7)) with
        {
            Modificar = doc =>
            {
                Poner(Totales(doc), "MontoNF", "-50.00");
                Poner(Totales(doc), "MntPagar", "0.00");
            },
        };
        SinHallazgos(Controlar(cfe));
    }

    [Fact]
    public void CreditoFiscal_VaEnMntTotCredFisc()
    {
        var cfe = Cfe(111) with
        {
            Modificar = doc =>
            {
                Retencion(doc, "2181410", 5m);
                Poner(Totales(doc), "MntTotCredFisc", "5.00");
            },
        };
        SinHallazgos(Controlar(cfe));
    }

    [Fact]
    public void CobranzaPropia_SoloNoFacturablesConReferencia()
    {
        var hallazgos = Controlar(Cfe(111) with { Modificar = doc => Poner(IdDoc(doc), "IndCobPropia", "1") });
        Rechaza(hallazgos, "tiene que ser 6 o 7");
        Rechaza(hallazgos, "referenciar los comprobantes");
        Rechaza(hallazgos, "MontoNF y MntPagar");
    }

    // --- Receptor -------------------------------------------------------------------------------

    [Fact]
    public void SecretoProfesional_NoExigeReceptor() =>
        SinHallazgos(Controlar(Cfe(111) with { Receptor = null, Modificar = doc => Poner(IdDoc(doc), "SecProf", "1") }));

    [Fact]
    public void ETicketConRetenciones_ExigeReceptor()
    {
        var cfe = Cfe(101) with
        {
            Modificar = doc =>
            {
                Retencion(doc, "2183211", 2m);
                Poner(Totales(doc), "MntTotRetenido", "2.00");
                Poner(Totales(doc), "MntPagar", "124.00");
            },
        };
        Rechaza(Controlar(cfe), "retenciones o percepciones y no identifica al receptor");
    }

    [Fact]
    public void DniDeUnPaisFueraDeLaRegion_E05() =>
        Rechaza(Controlar(Cfe(101) with { Receptor = new ReceptorCfe("6", "US", "12345678", "Cliente") }), "AR, BR, CL o PY");

    [Fact]
    public void Exportacion_ExigeDepartamentoYPais()
    {
        var cfe = Cfe(111) with
        {
            Modificar = doc =>
            {
                var viejo = Comprobante(doc);
                var nuevo = doc.CreateElement("eFact_Exp", Ns.Cfe);
                while (viejo.HasChildNodes)
                    nuevo.AppendChild(viejo.FirstChild!);
                doc.DocumentElement!.ReplaceChild(nuevo, viejo);
                Poner(IdDoc(doc), "TipoCFE", "121");
            },
        };
        var hallazgos = Controlar(cfe);
        Rechaza(hallazgos, "DeptoRecep");
        Rechaza(hallazgos, "PaisRecep");
    }

    // --- Referencias ----------------------------------------------------------------------------

    [Fact]
    public void ReferenciaRepetida_E05() =>
        Rechaza(Controlar(Cfe(102) with { Referencias = [new(101, "A", 5), new(101, "A", 5)] }), "repite el CFE");

    [Fact]
    public void ReferenciaGlobalSinRazon_E05() =>
        Rechaza(Controlar(Cfe(102) with { Referencias = [new(null, null, null)] }), "requiere RazonRef");

    [Fact]
    public void ReferenciaGlobalConDatosDeUnCfe_E05()
    {
        var cfe = Cfe(102) with
        {
            Referencias = [ReferenciaCfe.Global("Ajuste de precios del mes")],
            Modificar = doc => Poner(PrimeraReferencia(doc), "FechaCFEref", Hora.Formato(EmisorPrueba.Hoy)),
        };
        Rechaza(Controlar(cfe), "también informa FechaCFEref");
    }

    [Fact]
    public void ReferenciaEnMonedaExtranjeraSinTipoDeCambio_E05()
    {
        var cfe = Cfe(102) with
        {
            Referencias = [new(101, "A", 5)],
            Modificar = doc => Poner(PrimeraReferencia(doc), "TpoMonedaRef", "USD"),
        };
        Rechaza(Controlar(cfe), "no informa TpoCambioRef");
    }
}
