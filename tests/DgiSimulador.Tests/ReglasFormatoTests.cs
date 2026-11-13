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

    private static List<Hallazgo> Controlar(GeneradorCfe cfe, OpcionesSimulador? opciones = null)
    {
        var doc = XmlUtil.Cargar(cfe.Xml(), preservarEspacios: false);
        cfe.Modificar?.Invoke(doc);
        return new ReglasFormato(opciones ?? new OpcionesSimulador()).Controlar(CfeLeido.Leer(doc.DocumentElement!));
    }

    private static void Advierte(List<Hallazgo> hallazgos, string texto) =>
        Assert.True(hallazgos.Any(x => !x.Confirmado && x.Detalle.Contains(texto)),
            $"Falta una advertencia con \"{texto}\":{Environment.NewLine}" + string.Join(Environment.NewLine, hallazgos.Select(x => $"{x.Codigo}: {x.Detalle}")));

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
        var item = doc.DocumentElement.Buscar("Item")!;
        Poner(item, "IndAgenteResp", "R");
        Agregar(item, "RetencPercep", ("CodRet", codigo), ("ValRetPerc", XmlUtil.Numero(valor)));
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

    [Theory]
    [InlineData("4006381333931", true)]
    [InlineData("96385074", true)]
    [InlineData("036000291452", true)]
    [InlineData("10012345678902", true)]
    [InlineData("4006381333932", false)]
    [InlineData("400638133393", false)]
    [InlineData("40063813339A1", false)]
    public void Gtin_DigitoVerificadorDeGs1(string gtin, bool valido) => Assert.Equal(valido, Documentos.GtinValido(gtin));

    [Fact]
    public void GtinConDigitoVerificadorInvalido_RechazaConLosTiposDelFormato()
    {
        GeneradorCfe ConCodigo(string tipo, string gtin) => Cfe(101) with
        {
            Modificar = doc => Agregar(doc.DocumentElement.Buscar("Item")!, "CodItem", ("TpoCod", tipo), ("Cod", gtin)),
        };
        SinHallazgos(Controlar(ConCodigo("GTIN13", "4006381333931")));
        SinHallazgos(Controlar(ConCodigo("INT1", "4006381333932")));
        Rechaza(Controlar(ConCodigo("GTIN13", "4006381333932")), "GTIN13 4006381333932");
        // "GTIN" y "EAN" son nombres anteriores a los cuatro del formato: solo se advierte.
        var hallazgo = Assert.Single(Controlar(ConCodigo("GTIN", "4006381333932")));
        Assert.False(hallazgo.Confirmado);
    }

    [Fact]
    public void Indicador16_SumaEnMntNoGrv()
    {
        var cfe = Cfe(101, new LineaCfe("Venta de monotributista", 1, 100m, Indicador: 16)) with
        {
            Cae = new Cae(1, Rut, 101, "A", 1, 1000, EmisorPrueba.Hoy, EmisorPrueba.Hoy.AddYears(1), 3, null),
            Modificar = doc =>
            {
                Monotributo(doc);
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

    // --- Indicadores de facturación y CAE especial (A-C10, B-C4 y G-C5) --------------------------

    /// <summary>MntBruto va en IdDoc después de FchEmis: acá alcanza con que exista.</summary>
    private static void Monotributo(XmlDocument doc) => Poner(IdDoc(doc), "MntBruto", "3");

    private static Cae CaeEspecial(int tipo, int especial, int? causal = null) =>
        new(1, Rut, tipo, "A", 1, 1000, EmisorPrueba.Hoy, EmisorPrueba.Hoy.AddYears(1), especial, causal);

    [Fact]
    public void MntBruto3_NoAdmiteItemsGravadosYExigeCaeEspecial()
    {
        var hallazgos = Controlar(Cfe(101) with { Modificar = Monotributo });
        Rechaza(hallazgos, "no se puede usar el indicador de facturación 3");
        Rechaza(hallazgos, "exige un CAE especial");
    }

    [Fact]
    public void CaeDeMonotributo_ExigeMntBruto3YNoVaEnCuentaAjena()
    {
        Rechaza(Controlar(Cfe(101) with { Cae = CaeEspecial(101, 3) }), "MntBruto tiene que ser 3");
        Rechaza(Controlar(Cfe(131) with { Cae = CaeEspecial(131, 2) }), "no puede ser de venta por cuenta ajena");
    }

    [Fact]
    public void CaeExonerado_LlevaCausalYNoAdmiteItemsGravados()
    {
        var hallazgos = Controlar(Cfe(101) with { Cae = CaeEspecial(101, 1) });
        Rechaza(hallazgos, "falta CausalCAEEsp");
        Rechaza(hallazgos, "con CAEEspecial 1 (exonerado) no se puede usar el indicador de facturación 3");
        SinHallazgos(Controlar(Cfe(101, new LineaCfe("Exento", 1, 100m, Indicador: 1)) with { Cae = CaeEspecial(101, 1, 5) }));
    }

    [Fact]
    public void CausalSinCaeExonerado_E05() =>
        Rechaza(Controlar(Cfe(101) with { Modificar = doc => Poner(doc.DocumentElement.Buscar("CAEData")!, "CausalCAEEsp", "5") }),
            "CausalCAEEsp solo va con CAEEspecial 1");

    [Fact]
    public void IvaAlDiaDelMandanteYSecretoDeCuentaAjena_SoloEnCuentaAjena()
    {
        Rechaza(Controlar(Cfe(111) with { Modificar = doc => Poner(IdDoc(doc), "IVAalDia", "2") }), "tiene que ser de venta por cuenta ajena");
        Rechaza(Controlar(Cfe(111) with { Modificar = doc => Poner(IdDoc(doc), "SecProf", "2") }), "SecProf 2 es de una venta por cuenta ajena");
    }

    [Fact]
    public void Indicador16SinMntBruto3_SeAdvierte()
    {
        var cfe = Cfe(101, new LineaCfe("Venta", 1, 100m, Indicador: 16)) with
        {
            Modificar = doc =>
            {
                Poner(Totales(doc), "MntNoGrv", "100.00");
                Poner(Totales(doc), "MntTotal", "100.00");
                Poner(Totales(doc), "MntPagar", "100.00");
            },
        };
        var hallazgo = Assert.Single(Controlar(cfe));
        Assert.False(hallazgo.Confirmado);
        Assert.Contains("indicador de facturación 16", hallazgo.Detalle);
    }

    /// <summary>Una e-Boleta de entrada de un ítem con ese indicador, sin totales: solo para las reglas de B-C4.</summary>
    private static List<Hallazgo> Boleta(string indicador, string tipoDocVendedor, string idDoc = "")
    {
        var xml = $"""
            <CFE xmlns="{Ns.Cfe}" version="1.0"><eBoleta>
              <Encabezado>
                <IdDoc><TipoCFE>151</TipoCFE><Serie>A</Serie><Nro>1</Nro><FchEmis>{Hora.Formato(EmisorPrueba.Hoy)}</FchEmis>{idDoc}</IdDoc>
                <Emisor><RUCEmisor>{Rut}</RUCEmisor></Emisor>
                <Receptor><TipoDocRecep>{tipoDocVendedor}</TipoDocRecep><CodPaisRecep>UY</CodPaisRecep><DocRecep>{(tipoDocVendedor == "2" ? Rut : "12345672")}</DocRecep><RznSocRecep>Vendedor</RznSocRecep><DirRecep>Ruta 5</DirRecep><CiudadRecep>Durazno</CiudadRecep></Receptor>
                <Totales><TpoMoneda>UYU</TpoMoneda><CantLinDet>1</CantLinDet></Totales>
              </Encabezado>
              <Detalle><Item><NroLinDet>1</NroLinDet><IndFact>{indicador}</IndFact><NomItem>Lana</NomItem></Item></Detalle>
            </eBoleta></CFE>
            """;
        return new ReglasFormato(new OpcionesSimulador()).Controlar(CfeLeido.Leer(XmlUtil.Cargar(xml, preservarEspacios: false).DocumentElement!));
    }

    private static void NoRechazaPor(List<Hallazgo> hallazgos, string texto) => Assert.DoesNotContain(hallazgos, x => x.Detalle.Contains(texto));

    [Fact]
    public void BoletaDeEntrada_ElIndicadorDependeDelVendedorYDeLaCompraParaReventa()
    {
        Rechaza(Boleta("13", "2"), "el indicador 13 es de un vendedor no contribuyente");
        NoRechazaPor(Boleta("13", "3"), "indicador 13");
        Rechaza(Boleta("14", "3"), "el indicador 14 es de un vendedor contribuyente");
        Rechaza(Boleta("15", "3"), "el indicador 15 es de un vendedor contribuyente");
        NoRechazaPor(Boleta("14", "2"), "indicador 14");

        Rechaza(Boleta("17", "2"), "el indicador 17 exige IndCompraMEReventa");
        NoRechazaPor(Boleta("17", "2", "<IndCompraMEReventa>1</IndCompraMEReventa>"), "indicador 17");
        Rechaza(Boleta("14", "2", "<IndCompraMEReventa>1</IndCompraMEReventa>"), "solo se puede usar el indicador 17");
        var porCuentaAjena = Boleta("17", "2", "<SecProf>1</SecProf><IndCompraMEReventa>2</IndCompraMEReventa>");
        Rechaza(porCuentaAjena, "no puede existir SecProf");
        Rechaza(porCuentaAjena, "requiere el complemento fiscal");
    }

    // --- Propietario de la mercadería de un remito (A-C6.1 a A-C6.6) -----------------------------

    private static List<Hallazgo> Remito(string propietario)
    {
        var xml = $"""
            <CFE xmlns="{Ns.Cfe}" version="1.0"><eRem>
              <Encabezado>
                <IdDoc><TipoCFE>181</TipoCFE><Serie>A</Serie><Nro>1</Nro><FchEmis>{Hora.Formato(EmisorPrueba.Hoy)}</FchEmis><TipoTraslado>1</TipoTraslado>{propietario}</IdDoc>
                <Emisor><RUCEmisor>{Rut}</RUCEmisor></Emisor>
                <Receptor><TipoDocRecep>2</TipoDocRecep><CodPaisRecep>UY</CodPaisRecep><DocRecep>{Rut}</DocRecep><RznSocRecep>Cliente S.A.</RznSocRecep><DirRecep>Rincón 500</DirRecep><CiudadRecep>Montevideo</CiudadRecep></Receptor>
                <Totales><CantLinDet>1</CantLinDet></Totales>
              </Encabezado>
              <Detalle><Item><NroLinDet>1</NroLinDet><NomItem>Cajas</NomItem><Cantidad>1</Cantidad><UniMed>N/A</UniMed></Item></Detalle>
            </eRem></CFE>
            """;
        return new ReglasFormato(new OpcionesSimulador()).Controlar(CfeLeido.Leer(XmlUtil.Cargar(xml, preservarEspacios: false).DocumentElement!));
    }

    [Fact]
    public void RemitoDeMercaderiaDeTerceros_IdentificaAlPropietario()
    {
        SinHallazgos(Remito(""));
        SinHallazgos(Remito($"<IndPropiedad>1</IndPropiedad><TipoDocProp>2</TipoDocProp><CodPaisProp>UY</CodPaisProp><DocProp>{Rut}</DocProp><RznSocProp>Dueño S.A.</RznSocProp>"));
        SinHallazgos(Remito("<IndPropiedad>1</IndPropiedad><TipoDocProp>6</TipoDocProp><CodPaisProp>AR</CodPaisProp><DocPropExt>30111222</DocPropExt><RznSocProp>Dueño</RznSocProp>"));

        var sinDatos = Remito("<IndPropiedad>1</IndPropiedad>");
        Rechaza(sinDatos, "falta TipoDocProp");
        Rechaza(sinDatos, "falta RznSocProp");
    }

    [Fact]
    public void PropietarioDelRemito_PaisYDocumentoSegunElTipo()
    {
        var rucArgentino = Remito("<IndPropiedad>1</IndPropiedad><TipoDocProp>2</TipoDocProp><CodPaisProp>AR</CodPaisProp><DocProp>211003420018</DocProp><RznSocProp>Dueño</RznSocProp>");
        Rechaza(rucArgentino, "CodPaisProp tiene que ser UY");
        Rechaza(rucArgentino, "El RUC del propietario 211003420018 no tiene un dígito verificador válido");
        Rechaza(Remito("<IndPropiedad>1</IndPropiedad><TipoDocProp>3</TipoDocProp><CodPaisProp>UY</CodPaisProp><DocProp>12345678</DocProp><RznSocProp>Dueño</RznSocProp>"),
            "La cédula del propietario");
        Rechaza(Remito("<IndPropiedad>1</IndPropiedad><TipoDocProp>6</TipoDocProp><CodPaisProp>US</CodPaisProp><DocPropExt>1</DocPropExt><RznSocProp>Dueño</RznSocProp>"),
            "tiene que ser AR, BR, CL o PY");
        Rechaza(Remito("<IndPropiedad>1</IndPropiedad><TipoDocProp>5</TipoDocProp><CodPaisProp>US</CodPaisProp><RznSocProp>Dueño</RznSocProp>"),
            "tienen que existir CodPaisProp y DocProp o DocPropExt");
        Rechaza(Remito("<CodPaisProp>UY</CodPaisProp>"), "requieren TipoDocProp");
    }

    // --- Agente o responsable y códigos de retención (B-C6 y B-C20) -------------------------------

    [Fact]
    public void AgenteResponsableYTablaDeRetenciones_VanJuntos()
    {
        Rechaza(Controlar(Cfe(111) with { Modificar = doc => Poner(doc.DocumentElement.Buscar("Item")!, "IndAgenteResp", "R") }),
            "tiene que existir la tabla de retenciones");
        var sinAgente = Cfe(111) with
        {
            Modificar = doc =>
            {
                Retencion(doc, "2183211", 2m);
                var item = doc.DocumentElement.Buscar("Item")!;
                item.RemoveChild(item.Hijo("IndAgenteResp")!);
                Poner(Totales(doc), "MntTotRetenido", "2.00");
                Poner(Totales(doc), "MntPagar", "124.00");
            },
        };
        Rechaza(Controlar(sinAgente), "requiere IndAgenteResp");
    }

    [Fact]
    public void CodigoDeRetencion_NumeroMayorQueCeroYRegistradoSiHayLista()
    {
        GeneradorCfe Con(string codigo) => Cfe(111) with
        {
            Modificar = doc =>
            {
                Retencion(doc, codigo, 2m);
                Poner(Totales(doc), "MntTotRetenido", "2.00");
                Poner(Totales(doc), "MntPagar", "124.00");
            },
        };
        Rechaza(Controlar(Con("21832AB")), "tiene que ser un número mayor que cero");
        Rechaza(Controlar(Con("0")), "tiene que ser un número mayor que cero");

        // La DGI no publica la tabla en los formatos: solo se controla contra la lista si se configura.
        SinHallazgos(Controlar(Con("2183299")));
        var conLista = new OpcionesSimulador { CodigosDeRetencion = ["2183211"] };
        SinHallazgos(Controlar(Con("2183211"), conLista));
        SinHallazgos(Controlar(Con("9999123"), conLista));
        Rechaza(Controlar(Con("2183299"), conLista), "no es un código de retención");
    }

    // --- Complemento fiscal: el mandante (zona K) ------------------------------------------------

    private const string RutMandante = "211234560019";

    private static Action<XmlDocument> ConMandante(string tipo = "2", string pais = "UY", string documento = RutMandante, string nombre = "Mandante S.A.") =>
        doc => Agregar(Agregar(Comprobante(doc), "Compl_Fiscal"), "Compl_Fiscal_Data",
            ("RUCEmisor", Rut), ("TipoDocMdte", tipo), ("Pais", pais), ("DocMdte", documento), ("NombreMdte", nombre));

    [Fact]
    public void CuentaAjena_RequiereElMandanteSalvoConSecretoProfesional()
    {
        Rechaza(Controlar(Cfe(131)), "requieren el complemento fiscal");
        SinHallazgos(Controlar(Cfe(131) with { Modificar = doc => Poner(IdDoc(doc), "SecProf", "2") }));
        SinHallazgos(Controlar(Cfe(131) with { Modificar = ConMandante() }));
        SinHallazgos(Controlar(Cfe(131) with { Modificar = ConMandante("3", "UY", "12345672") }));
    }

    [Fact]
    public void Mandante_DocumentoPaisYNombre()
    {
        Rechaza(Controlar(Cfe(131) with { Modificar = ConMandante(documento: Rut) }), "no puede ser el emisor");
        Rechaza(Controlar(Cfe(131) with { Modificar = ConMandante(pais: "AR") }), "Pais tiene que ser UY");
        Rechaza(Controlar(Cfe(131) with { Modificar = ConMandante(documento: "211234560018") }), "El RUC del mandante");
        Rechaza(Controlar(Cfe(131) with { Modificar = ConMandante("3", "UY", "12345678") }), "La cédula del mandante");
        Rechaza(Controlar(Cfe(131) with { Modificar = ConMandante("6", "US", "123") }), "tiene que ser AR, BR, CL o PY");
        Rechaza(Controlar(Cfe(131) with { Modificar = ConMandante(nombre: "") }), "Falta NombreMdte");
    }

    [Fact]
    public void ComplementoFiscalFueraDeCuentaAjena_SeAdvierte() =>
        Advierte(Controlar(Cfe(101) with { Modificar = ConMandante() }), "solo va en los comprobantes de venta por cuenta ajena");
}
