using System.Globalization;
using System.Text;
using System.Xml;
using DgiSimulador.Xml;

namespace DgiSimulador.Tests;

/// <summary>Una zona del reporte diario de prueba (e-Ticket o e-Factura) con sus montos a tasa básica.</summary>
internal sealed record ZonaPrueba(int Tipo, string Serie, long Desde, long Hasta, decimal Neto, int? Emitidos = null, int Anulados = 0,
    (long Desde, long Hasta)? RangoAnulado = null);

/// <summary>Arma reportes diarios firmados para las pruebas (un ítem de montos por zona).</summary>
internal static class ReportesPrueba
{
    internal static string Armar(EmisorPrueba emisor, DateOnly fecha, int secuencia, IReadOnlyList<ZonaPrueba> zonas, int? cantidad = null,
        Action<XmlDocument>? modificar = null)
    {
        static string N(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        var utilizados = zonas.Sum(z => (z.Emitidos ?? (int)(z.Hasta - z.Desde + 1)) + z.Anulados);
        sb.Append($"<Reporte xmlns=\"{Ns.Cfe}\"><Caratula version=\"1.0\"><RUCEmisor>{emisor.Rut}</RUCEmisor>")
            .Append($"<FechaResumen>{Hora.Formato(fecha)}</FechaResumen><SecEnvio>{secuencia}</SecEnvio>")
            .Append($"<TmstFirmaEnv>{Hora.Formato(DateTimeOffset.UtcNow.AddSeconds(-5))}</TmstFirmaEnv><CantComprobantes>{cantidad ?? utilizados}</CantComprobantes></Caratula>");
        foreach (var z in zonas.OrderBy(z => z.Tipo))
        {
            var emitidos = z.Emitidos ?? (int)(z.Hasta - z.Desde + 1);
            var iva = Math.Round(z.Neto * 0.22m, 2);
            sb.Append($"<{(z.Tipo == 101 ? "Rsmn_Tck" : "Rsmn_Fac")}><TipoComp>{z.Tipo}</TipoComp><RsmnData><Montos><{(z.Tipo == 101 ? "Mnts_FyT_Item" : "Mnts_FyT_Item")}>")
                .Append($"<Fecha>{Hora.Formato(fecha)}</Fecha><CodSuc>1</CodSuc><TotMntIVATasaBas>{N(z.Neto)}</TotMntIVATasaBas>")
                .Append($"<MntIVATasaBas>{N(iva)}</MntIVATasaBas><IVATasaBas>22.00</IVATasaBas><TotMntTotal>{N(z.Neto + iva)}</TotMntTotal>")
                .Append("</Mnts_FyT_Item></Montos>")
                .Append($"<CantDocsUtil>{emitidos + z.Anulados}</CantDocsUtil>")
                .Append(z.Tipo == 101 ? "<CantDocsMay_topeUI>0</CantDocsMay_topeUI>" : "")
                .Append($"<CantDocsAnulados>{z.Anulados}</CantDocsAnulados><CantDocsEmi>{emitidos}</CantDocsEmi>")
                .Append($"<RngDocsUtil><RDU_Item><Serie>{z.Serie}</Serie><NroDesde>{z.Desde}</NroDesde><NroHasta>{z.Hasta}</NroHasta></RDU_Item></RngDocsUtil>");
            if (z.RangoAnulado is { } anulado)
                sb.Append($"<RngDocsAnulados><RDA_Item><Serie>{z.Serie}</Serie><NroDesde>{anulado.Desde}</NroDesde><NroHasta>{anulado.Hasta}</NroHasta></RDA_Item></RngDocsAnulados>");
            sb.Append($"</RsmnData></{(z.Tipo == 101 ? "Rsmn_Tck" : "Rsmn_Fac")}>");
        }
        sb.Append("</Reporte>");

        var doc = XmlUtil.Cargar(sb.ToString(), preservarEspacios: false);
        modificar?.Invoke(doc);
        Firmas.FirmarEnveloped(doc, emisor.Certificado);
        return doc.OuterXml;
    }
}

/// <summary>Recepción del reporte diario (ACKRepDiario, motivos R).</summary>
public sealed class ReporteTests(Simulador sim) : IClassFixture<Simulador>
{
    private async Task<(EmisorPrueba Emisor, Estado.Cae Cae)> EmisorConETicket()
    {
        var emisor = await sim.Emisor();
        var cae = sim.Cae(emisor.Rut, 101);
        await sim.Enviar(emisor, emisor.Firmar(emisor.Cfe(cae, 1)));
        return (emisor, cae);
    }

    [Fact]
    public async Task ReporteQueCoincideConLosCfe_AR()
    {
        var (emisor, _) = await EmisorConETicket();
        var acuse = await sim.EnviarReporte(emisor.Certificado, ReportesPrueba.Armar(emisor, EmisorPrueba.Hoy, 1, [new ZonaPrueba(101, "A", 1, 1, 100m)]));
        Assert.True(SobreTests.Estado(acuse) == "AR", acuse.OuterXml);
        SobreTests.ValidaContraXsd(acuse);
        var reporte = sim.Almacen.Reportes(emisor.Rut).Single();
        Assert.Empty(reporte.Advertencias);
    }

    [Fact]
    public async Task MasDeDosMegas_NoSeRechazaPorTamano()
    {
        // El tope de 2 MB es del sobre: el Formato del Reporte no fija uno.
        var emisor = await sim.Emisor();
        var acuse = await sim.EnviarReporte(emisor.Certificado, new string('x', 3 * 1024 * 1024));
        var motivo = Assert.Single(acuse.BuscarTodos("MotivosRechazo"));
        Assert.Equal("R01", motivo.Texto("Motivo"));
        Assert.StartsWith("No es XML", motivo.Texto("Detalle"));
    }

    [Fact]
    public async Task SecuenciaQueNoSigue_R05()
    {
        var (emisor, _) = await EmisorConETicket();
        var zonas = new[] { new ZonaPrueba(101, "A", 1, 1, 100m) };
        Assert.Equal("AR", SobreTests.Estado(await sim.EnviarReporte(emisor.Certificado, ReportesPrueba.Armar(emisor, EmisorPrueba.Hoy, 1, zonas))));
        var repetido = await sim.EnviarReporte(emisor.Certificado, ReportesPrueba.Armar(emisor, EmisorPrueba.Hoy, 1, zonas));
        Assert.Equal(["R05"], SobreTests.Codigos(repetido));
        Assert.Equal("AR", SobreTests.Estado(await sim.EnviarReporte(emisor.Certificado, ReportesPrueba.Armar(emisor, EmisorPrueba.Hoy, 2, zonas))));
    }

    [Fact]
    public async Task FechaPosterior_R06()
    {
        var emisor = await sim.Emisor();
        var acuse = await sim.EnviarReporte(emisor.Certificado, ReportesPrueba.Armar(emisor, EmisorPrueba.Hoy.AddDays(1), 1, []));
        Assert.Contains("R06", SobreTests.Codigos(acuse));
    }

    [Fact]
    public async Task EnviadoPorOtroRut_R02()
    {
        var emisor = await sim.Emisor();
        var otro = await sim.Emisor();
        var acuse = await sim.EnviarReporte(otro.Certificado, ReportesPrueba.Armar(emisor, EmisorPrueba.Hoy, 1, []));
        Assert.Contains("R02", SobreTests.Codigos(acuse));
    }

    [Fact]
    public async Task FirmaInvalida_R03()
    {
        var emisor = await sim.Emisor();
        var reporte = ReportesPrueba.Armar(emisor, EmisorPrueba.Hoy, 1, []).Replace("<SecEnvio>1<", "<SecEnvio>1 <");
        var acuse = await sim.EnviarReporte(emisor.Certificado, reporte);
        Assert.Contains("R03", SobreTests.Codigos(acuse));
    }

    [Fact]
    public async Task NoValidaContraElXsd_R04()
    {
        var emisor = await sim.Emisor();
        var reporte = ReportesPrueba.Armar(emisor, EmisorPrueba.Hoy, 1, [], modificar: doc => doc.DocumentElement.Buscar("CantComprobantes")!.InnerText = "x");
        Assert.Equal(["R04"], SobreTests.Codigos(await sim.EnviarReporte(emisor.Certificado, reporte)).Distinct());
    }

    [Fact]
    public async Task CantComprobantesQueNoSuma_R04()
    {
        var (emisor, _) = await EmisorConETicket();
        var reporte = ReportesPrueba.Armar(emisor, EmisorPrueba.Hoy, 1, [new ZonaPrueba(101, "A", 1, 1, 100m)], cantidad: 3);
        Assert.Equal(["R04"], SobreTests.Codigos(await sim.EnviarReporte(emisor.Certificado, reporte)));
    }

    [Fact]
    public async Task EmitidosQueNoCoincidenConLoRecibido_SeAdvierte()
    {
        var (emisor, _) = await EmisorConETicket();
        var acuse = await sim.EnviarReporte(emisor.Certificado, ReportesPrueba.Armar(emisor, EmisorPrueba.Hoy, 1, [new ZonaPrueba(101, "A", 1, 2, 200m)]));
        Assert.Equal("AR", SobreTests.Estado(acuse));
        var advertencias = sim.Almacen.Reportes(emisor.Rut).Single().Advertencias;
        Assert.Contains(advertencias, a => a.Contains("informa 2 emitidos"));
        Assert.Contains(advertencias, a => a.Contains("pesos"));
    }

    [Fact]
    public async Task RangoAnulado_DespuesEsE01()
    {
        var (emisor, cae) = await EmisorConETicket();
        // Los utilizados incluyen a los anulados: el rango va del 1 al 3, con un emitido y dos anulados.
        var zona = new ZonaPrueba(101, "A", 1, 3, 100m, Emitidos: 1, Anulados: 2, RangoAnulado: (2, 3));
        Assert.Equal("AR", SobreTests.Estado(await sim.EnviarReporte(emisor.Certificado, ReportesPrueba.Armar(emisor, EmisorPrueba.Hoy, 1, [zona]))));
        var (_, cfes) = await sim.Enviar(emisor, emisor.Firmar(emisor.Cfe(cae, 3)));
        Assert.Contains("E01", SobreTests.Codigos(cfes, "MotivosRechazoCF"));
    }

    // --- Reglas del Formato del Reporte v13.2 (R04) ----------------------------------------------

    private static readonly ZonaPrueba UnETicket = new(101, "A", 1, 1, 100m);

    /// <summary>Manda un reporte de un e-Ticket con un cambio y devuelve los detalles de los rechazos R04.</summary>
    private async Task<List<string>> Rechazos(Action<XmlDocument> modificar, ZonaPrueba? zona = null)
    {
        var (emisor, _) = await EmisorConETicket();
        var acuse = await sim.EnviarReporte(emisor.Certificado, ReportesPrueba.Armar(emisor, EmisorPrueba.Hoy, 1, [zona ?? UnETicket], modificar: modificar));
        Assert.All(SobreTests.Codigos(acuse), c => Assert.Equal("R04", c));
        return [.. sim.Almacen.Reportes(emisor.Rut).Single().Motivos.Select(m => m.Detalle ?? "")];
    }

    private static void Poner(XmlDocument doc, string campo, string valor) => doc.DocumentElement.Buscar(campo)!.InnerText = valor;

    [Fact]
    public async Task ElIvaEsElNetoPorLaTasa_R04()
    {
        Assert.Contains(await Rechazos(doc => Poner(doc, "MntIVATasaBas", "25.00")), d => d.Contains("MntIVATasaBas informa 25.00") && d.Contains("da 22.00"));
        // Al cambiar el IVA tampoco cierra el total, que es la suma de los netos y los IVA.
        Assert.Contains(await Rechazos(doc => Poner(doc, "MntIVATasaBas", "25.00")), d => d.Contains("TotMntTotal informa 122.00"));
    }

    [Fact]
    public async Task LaTasaTieneQueSerLaVigente_R04() =>
        Assert.Contains(await Rechazos(doc => Poner(doc, "IVATasaBas", "23.00")), d => d.Contains("la tasa vigente es 22"));

    [Fact]
    public async Task ElTotalEsLaSumaDeLosConceptos_R04() =>
        Assert.Contains(await Rechazos(doc => Poner(doc, "TotMntTotal", "130.00")), d => d.Contains("TotMntTotal informa 130.00") && d.Contains("da 122.00"));

    [Fact]
    public async Task LosUtilizadosSonLosNumerosDeSusRangos_R04()
    {
        Assert.Contains(await Rechazos(doc => Poner(doc, "NroHasta", "5")), d => d.Contains("los rangos de RngDocsUtil suman 5 números"));
        Assert.Contains(await Rechazos(_ => { }, new ZonaPrueba(101, "A", 1, 3, 100m, Emitidos: 1, Anulados: 2)),
            d => d.Contains("CantDocsAnulados es 2 y los rangos de RngDocsAnulados suman 0"));
    }

    [Fact]
    public async Task RangoConElFinalMenorQueElInicial_R04() =>
        Assert.Contains(await Rechazos(doc => { Poner(doc, "NroDesde", "9"); Poner(doc, "NroHasta", "3"); }), d => d.Contains("el final mayor o igual que el inicial"));

    [Fact]
    public async Task FechaDelComprobanteMuyAdelante_R04() =>
        Assert.Contains(await Rechazos(doc => doc.DocumentElement.Buscar("Mnts_FyT_Item")!.Hijo("Fecha")!.InnerText = Hora.Formato(EmisorPrueba.Hoy.AddDays(90))),
            d => d.Contains("no pasar de hoy + 60 días"));

    [Fact]
    public async Task LaTablaDeMontosNoRepiteFechaYSucursal_R04() =>
        Assert.Contains(await Rechazos(doc =>
        {
            var linea = doc.DocumentElement.Buscar("Mnts_FyT_Item")!;
            linea.ParentNode!.AppendChild(linea.CloneNode(true));
        }), d => d.Contains("repite la fecha"));

    [Fact]
    public async Task ElRedondeoDeCadaComprobanteNoRechaza()
    {
        // Tres comprobantes de 33,33: el IVA de cada uno es 7,33 (suman 21,99) y la fórmula sobre el neto da 22,00.
        var (emisor, _) = await EmisorConETicket();
        var acuse = await sim.EnviarReporte(emisor.Certificado, ReportesPrueba.Armar(emisor, EmisorPrueba.Hoy, 1, [new ZonaPrueba(101, "A", 1, 3, 99.99m)],
            modificar: doc =>
            {
                Poner(doc, "MntIVATasaBas", "21.99");
                Poner(doc, "TotMntTotal", "121.98");
            }));
        Assert.Equal("AR", SobreTests.Estado(acuse));
    }
}
