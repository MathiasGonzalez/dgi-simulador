using System.Xml;
using DgiSimulador.Intercambio;
using DgiSimulador.Xml;

namespace DgiSimulador.Tests;

/// <summary>Controles de la DGI sobre cada CFE (motivos E y observaciones de contingencia).</summary>
public sealed class CfeTests(Simulador sim) : IClassFixture<Simulador>
{
    private static XmlElement Detalle(XmlDocument cfes, int ordinal = 1) =>
        cfes.BuscarTodos("ACKCFE_Det").Single(d => d.Texto("Nro_ordinal") == ordinal.ToString());

    private static string[] Motivos(XmlElement detalle) => SobreTests.Codigos(detalle, "MotivosRechazoCF");

    private async Task<XmlElement> Resultado(EmisorPrueba emisor, GeneradorCfe cfe)
    {
        var (_, cfes) = await sim.Enviar(emisor, emisor.Firmar(cfe));
        SobreTests.ValidaContraXsd(cfes);
        return Detalle(cfes);
    }

    [Fact]
    public async Task EFacturaValida_AE()
    {
        var emisor = await sim.Emisor();
        var cae = sim.Cae(emisor.Rut, 111);
        var cfe = emisor.Cfe(cae, 1) with { Lineas = [new("Servicio", 3, 120.5m), new("Exento", 1, 50m, Indicador: 1), new("Mínima", 2, 10m, Indicador: 2)] };
        var detalle = await Resultado(emisor, cfe);
        Assert.Equal("AE", detalle.Texto("Estado"));
    }

    [Fact]
    public async Task FirmaQueNoCoincide_E04()
    {
        var emisor = await sim.Emisor();
        var cae = sim.Cae(emisor.Rut, 101);
        var cfe = emisor.Firmar(emisor.Cfe(cae, 1)).Replace("Servicio de prueba", "Servicio alterado");
        var (_, cfes) = await sim.Enviar(emisor, cfe);
        Assert.Contains("E04", Motivos(Detalle(cfes)));
        Assert.Equal("BE", Detalle(cfes).Texto("Estado"));
    }

    [Fact]
    public async Task NumeroDeSerieDelCertificadoDe38Digitos_NoEsErrorDeFormato()
    {
        // Como lo informan algunos productos: KeyInfo con X509IssuerSerial (fuera de lo firmado). El validador de .NET no
        // admite enteros de más de 29 dígitos, el XSD sí.
        var emisor = await sim.Emisor();
        var cae = sim.Cae(emisor.Rut, 101);
        var cfe = emisor.Firmar(emisor.Cfe(cae, 1)).Replace("</X509Certificate>",
            "</X509Certificate><X509IssuerSerial><X509IssuerName>CN=CA</X509IssuerName>" +
            "<X509SerialNumber>118544958188699769883490684470227466835</X509SerialNumber></X509IssuerSerial>");
        var (_, cfes) = await sim.Enviar(emisor, cfe);
        Assert.Equal("AE", Detalle(cfes).Texto("Estado"));
    }

    [Fact]
    public async Task NoValidaContraElXsd_E05()
    {
        var emisor = await sim.Emisor();
        var cae = sim.Cae(emisor.Rut, 101);
        var cfe = emisor.Cfe(cae, 1) with { Modificar = doc => doc.DocumentElement.Buscar("FmaPago")!.InnerText = "9" };
        var detalle = await Resultado(emisor, cfe);
        Assert.Equal(["E05"], Motivos(detalle));
        Assert.Contains("XSD", detalle.Buscar("Detalle")!.InnerText);
    }

    [Fact]
    public async Task TotalQueNoCoincideConLosItems_E05()
    {
        var emisor = await sim.Emisor();
        var cae = sim.Cae(emisor.Rut, 101);
        var cfe = emisor.Cfe(cae, 1) with { Modificar = doc => doc.DocumentElement.Buscar("MntIVATasaBasica")!.InnerText = "21.00" };
        var detalle = await Resultado(emisor, cfe);
        Assert.Contains("E05", Motivos(detalle));
        Assert.Contains(detalle.BuscarTodos("Detalle"), d => d.InnerText.Contains("MntIVATasaBasica"));
    }

    [Fact]
    public async Task MontoItemMalCalculado_E05()
    {
        var emisor = await sim.Emisor();
        var cae = sim.Cae(emisor.Rut, 101);
        var cfe = emisor.Cfe(cae, 1) with { Modificar = doc => doc.DocumentElement.Buscar("MontoItem")!.InnerText = "99.00" };
        Assert.Contains("E05", Motivos(await Resultado(emisor, cfe)));
    }

    [Fact]
    public async Task ReceptorConRutInvalido_E05()
    {
        var emisor = await sim.Emisor();
        var cae = sim.Cae(emisor.Rut, 111);
        var cfe = emisor.Cfe(cae, 1) with { Receptor = new ReceptorCfe("2", "UY", "211003420018", "Cliente") };
        var detalle = await Resultado(emisor, cfe);
        Assert.Contains("E05", Motivos(detalle));
        Assert.Contains(detalle.BuscarTodos("Detalle"), d => d.InnerText.Contains("dígito verificador"));
    }

    [Fact]
    public async Task NumeroRepetido_E02()
    {
        var emisor = await sim.Emisor();
        var cae = sim.Cae(emisor.Rut, 101);
        Assert.Equal("AE", (await Resultado(emisor, emisor.Cfe(cae, 1))).Texto("Estado"));
        Assert.Equal(["E02"], Motivos(await Resultado(emisor, emisor.Cfe(cae, 1))));
    }

    [Fact]
    public async Task RepetidoEnElMismoSobre_E02()
    {
        var emisor = await sim.Emisor();
        var cae = sim.Cae(emisor.Rut, 101);
        var cfe = emisor.Firmar(emisor.Cfe(cae, 1));
        var (_, cfes) = await sim.Enviar(emisor, cfe, cfe);
        Assert.Equal("AE", Detalle(cfes, 1).Texto("Estado"));
        Assert.Equal(["E02"], Motivos(Detalle(cfes, 2)));
    }

    [Fact]
    public async Task FueraDelRangoDelCae_E03()
    {
        var emisor = await sim.Emisor();
        var cae = sim.Cae(emisor.Rut, 101, desde: 1, hasta: 10);
        Assert.Equal(["E03"], Motivos(await Resultado(emisor, emisor.Cfe(cae, 11))));
    }

    [Fact]
    public async Task CaeDeOtroTipo_E09()
    {
        var emisor = await sim.Emisor();
        var cae = sim.Cae(emisor.Rut, 111);
        var cfe = emisor.Cfe(cae, 1) with { Tipo = 101, Receptor = null };
        Assert.Equal(["E09"], Motivos(await Resultado(emisor, cfe)));
    }

    [Fact]
    public async Task CaeQueNoExiste_E09()
    {
        var emisor = await sim.Emisor();
        var inexistente = new Estado.Cae(12_345_678_901, emisor.Rut, 101, "A", 1, 1000, EmisorPrueba.Hoy, EmisorPrueba.Hoy.AddYears(1), null, null);
        Assert.Equal(["E09"], Motivos(await Resultado(emisor, emisor.Cfe(inexistente, 1))));
    }

    [Fact]
    public async Task FirmadoDespuesDelVencimientoDelCae_E07()
    {
        var emisor = await sim.Emisor();
        var cae = sim.Cae(emisor.Rut, 101, autorizacion: EmisorPrueba.Hoy.AddYears(-2), vencimiento: EmisorPrueba.Hoy.AddDays(-1));
        Assert.Equal(["E07"], Motivos(await Resultado(emisor, emisor.Cfe(cae, 1))));
    }

    [Fact]
    public async Task CaeEspecialQueNoCoincide_E11()
    {
        var emisor = await sim.Emisor();
        var cae = sim.Cae(emisor.Rut, 101, especial: 2);
        var cfe = emisor.Cfe(cae with { Especial = null }, 1);
        Assert.Equal(["E11"], Motivos(await Resultado(emisor, cfe)));
    }

    [Fact]
    public async Task MonedaExtranjeraSinTipoDeCambio_E10()
    {
        var emisor = await sim.Emisor();
        var cae = sim.Cae(emisor.Rut, 101);
        Assert.Equal(["E10"], Motivos(await Resultado(emisor, emisor.Cfe(cae, 1) with { Moneda = "USD" })));
        Assert.Equal("AE", (await Resultado(emisor, emisor.Cfe(cae, 2) with { Moneda = "USD", TipoCambio = 40.125m })).Texto("Estado"));
    }

    [Fact]
    public async Task TipoDeCambioFueraDeRango_E10()
    {
        var emisor = await sim.Emisor();
        var cae = sim.Cae(emisor.Rut, 101);
        Assert.Equal(["E10"], Motivos(await Resultado(emisor, emisor.Cfe(cae, 1) with { Moneda = "USD", TipoCambio = 400m })));
    }

    [Fact]
    public async Task NotaSinReferencia_E05_YConReferenciaDesconocida_E12()
    {
        var emisor = await sim.Emisor();
        var caeNota = sim.Cae(emisor.Rut, 102);
        Assert.Equal(["E05"], Motivos(await Resultado(emisor, emisor.Cfe(caeNota, 1))));
        var conReferencia = emisor.Cfe(caeNota, 2) with { Referencias = [new ReferenciaCfe(101, "A", 999)] };
        Assert.Equal(["E12"], Motivos(await Resultado(emisor, conReferencia)));
    }

    [Fact]
    public async Task NotaConReferenciaAUnCfeAceptado_AE()
    {
        var emisor = await sim.Emisor();
        var cae = sim.Cae(emisor.Rut, 101);
        var caeNota = sim.Cae(emisor.Rut, 102);
        Assert.Equal("AE", (await Resultado(emisor, emisor.Cfe(cae, 5))).Texto("Estado"));
        var nota = emisor.Cfe(caeNota, 1) with { Referencias = [new ReferenciaCfe(101, "A", 5, "Devolución")] };
        Assert.Equal("AE", (await Resultado(emisor, nota)).Texto("Estado"));
        var global = emisor.Cfe(caeNota, 2) with { Referencias = [ReferenciaCfe.Global("Descuento por volumen del mes")] };
        Assert.Equal("AE", (await Resultado(emisor, global)).Texto("Estado"));
    }

    [Fact]
    public async Task ContingenciaConProblemas_QuedaObservada()
    {
        var emisor = await sim.Emisor();
        var cae = sim.Cae(emisor.Rut, 201);
        var cfe = emisor.Cfe(cae, 1) with { Modificar = doc => doc.DocumentElement.Buscar("MntIVATasaBasica")!.InnerText = "21.00" };
        var (_, cfes) = await sim.Enviar(emisor, emisor.Firmar(cfe));
        var detalle = Detalle(cfes);
        Assert.Equal("CE", detalle.Texto("Estado"));
        Assert.Equal(["C05"], SobreTests.Codigos(detalle, "ObsCFC").Distinct());
        Assert.Equal("1", cfes.Buscar("CantCFCObservados")!.InnerText);
        SobreTests.ValidaContraXsd(cfes);
    }

    [Fact]
    public async Task NumeroInformadoComoAnulado_E01()
    {
        var emisor = await sim.Emisor();
        var cae = sim.Cae(emisor.Rut, 101);
        sim.Almacen.GuardarAnulados(emisor.Rut, 101, "A", 7, 9, EmisorPrueba.Hoy);
        Assert.Contains("E01", Motivos(await Resultado(emisor, emisor.Cfe(cae, 8))));
    }

    [Fact]
    public async Task CfeDeOtroRutEnElSobre_S02()
    {
        var emisor = await sim.Emisor();
        var otro = await sim.Emisor();
        var cae = sim.Cae(otro.Rut, 101);
        var acuse = await sim.EnviarSobre(emisor.Certificado, emisor.Sobre(otro.Firmar(otro.Cfe(cae, 1))));
        Assert.Contains("S02", SobreTests.Codigos(acuse));
    }

    [Fact]
    public async Task PreciosConIvaIncluido_ElRedondeoSeAdvierteYNoRechaza()
    {
        var emisor = await sim.Emisor();
        var cae = sim.Cae(emisor.Rut, 101);
        // Con MntBruto=1 el neto es el bruto / 1,22; el generador lo informa como si fuera neto: no coincide.
        var cfe = emisor.Cfe(cae, 1) with
        {
            Modificar = doc =>
            {
                var idDoc = doc.DocumentElement.Buscar("IdDoc")!;
                var bruto = doc.CreateElement("MntBruto", Ns.Cfe);
                bruto.InnerText = "1";
                idDoc.InsertBefore(bruto, idDoc.Hijo("FmaPago"));
            },
        };
        var detalle = await Resultado(emisor, cfe);
        Assert.Equal("AE", detalle.Texto("Estado"));
        var registrado = sim.Almacen.Cfe(emisor.Rut, 101, "A", 1)!;
        Assert.Contains(registrado.Advertencias, a => a.Contains("MntNetoIVATasaBasica"));
    }
}

/// <summary>En modo estricto las advertencias rechazan.</summary>
public sealed class EstrictoTests(SimuladorEstricto sim) : IClassFixture<SimuladorEstricto>
{
    [Fact]
    public async Task PreciosConIvaIncluidoMalRedondeados_Rechaza()
    {
        var emisor = await sim.Emisor();
        var cae = sim.Cae(emisor.Rut, 101);
        var cfe = emisor.Cfe(cae, 1) with
        {
            Modificar = doc =>
            {
                var idDoc = doc.DocumentElement.Buscar("IdDoc")!;
                var bruto = doc.CreateElement("MntBruto", Ns.Cfe);
                bruto.InnerText = "1";
                idDoc.InsertBefore(bruto, idDoc.Hijo("FmaPago"));
            },
        };
        var (_, cfes) = await sim.Enviar(emisor, emisor.Firmar(cfe));
        Assert.Equal("BE", cfes.Buscar("ACKCFE_Det")!.Texto("Estado"));
    }
}
