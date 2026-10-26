using DgiSimulador.Certificados;
using DgiSimulador.Estado;
using DgiSimulador.Xml;
using Microsoft.Extensions.Options;

namespace DgiSimulador.Recepcion;

/// <summary>Resultado de controlar un CFE: AE, BE o CE, con los motivos y las advertencias (reglas no confirmadas).</summary>
internal sealed record ResultadoCfe(CfeLeido Cfe, string Estado, IReadOnlyList<Motivo> Motivos, IReadOnlyList<string> Advertencias);

/// <summary>Lo que se sabe de los CFE anteriores del mismo sobre: los repetidos son E02 y las referencias a ellos valen.</summary>
internal sealed class ContextoSobre
{
    internal HashSet<(int, string, long)> Vistos { get; } = [];
    internal HashSet<(int, string, long)> Aceptados { get; } = [];
}

/// <summary>
/// Los controles de la DGI sobre cada CFE de un sobre, en el orden del acuse: firma (E04), formato (E05), anulado
/// (E01), duplicado (E02), CAE (E03, E07, E09, E11, E13), montos habituales (E06), complemento fiscal (E08), tipo
/// de cambio (E10) y referencias (E12).
/// </summary>
internal sealed class ControlCfe(Almacen almacen, Autoridad autoridad, IOptions<OpcionesSimulador> opciones, TimeProvider reloj)
{
    private readonly OpcionesSimulador _opciones = opciones.Value;

    internal ResultadoCfe Controlar(CfeLeido cfe, Firma firma, IReadOnlyList<string> erroresXsd, ContextoSobre contexto, string? rechazoForzado)
    {
        var hallazgos = new List<Hallazgo>();
        FirmaDelCfe(cfe, firma, hallazgos);
        Formato(cfe, erroresXsd, hallazgos);

        if (cfe is { Tipo: { } tipo, Serie: { } serie, Nro: { } nro } && cfe.RutEmisor is { } rut)
        {
            var clave = (tipo, serie, nro);
            if (almacen.EstaAnulado(rut, tipo, serie, nro))
                hallazgos.Add(new("E01", $"{cfe.Identificacion} se informó como anulado en un reporte diario."));
            if (!contexto.Vistos.Add(clave))
                hallazgos.Add(new("E02", $"{cfe.Identificacion} está repetido en el sobre."));
            else if (almacen.ExisteCfe(rut, tipo, serie, nro))
                hallazgos.Add(new("E02", $"La DGI ya recibió {cfe.Identificacion}."));
            Cae(cfe, rut, tipo, serie, nro, hallazgos);
        }

        hallazgos.AddRange(new ReglasFormato(_opciones).Controlar(cfe));
        MontoHabitual(cfe, hallazgos);
        ComplementoFiscal(cfe, hallazgos);
        Referencias(cfe, contexto, hallazgos);
        if (rechazoForzado is not null)
            hallazgos.Add(new(Motivos.Cfe.ContainsKey(rechazoForzado) ? rechazoForzado : "E14", "Rechazo programado en el simulador (POST /admin/fallas)."));

        var rechazos = hallazgos.Where(x => x.Confirmado || _opciones.Estricto).ToList();
        var advertencias = hallazgos.Where(x => !x.Confirmado && !_opciones.Estricto).Select(x => $"{x.Codigo}: {x.Detalle}").ToList();
        var motivos = rechazos.Select(x => Motivos.DeCfe(x.Codigo, x.Detalle)).ToList();
        var estado = motivos.Count == 0 ? "AE"
            // Un comprobante de contingencia ya se entregó: con problemas queda observado si todos tienen observación equivalente.
            : cfe.EsContingencia && motivos.All(m => Motivos.Contingencia.ContainsKey(m.Codigo)) ? "CE"
            : "BE";
        if (estado != "BE" && cfe is { Tipo: { } t, Serie: { } s, Nro: { } n })
            contexto.Aceptados.Add((t, s, n));
        return new ResultadoCfe(cfe, estado, motivos, advertencias);
    }

    private void FirmaDelCfe(CfeLeido cfe, Firma firma, List<Hallazgo> h)
    {
        if (!firma.Valida)
        {
            h.Add(new("E04", firma.Error ?? "Firma inválida."));
            return;
        }
        var momento = cfe.Firma ?? reloj.GetUtcNow();
        var control = autoridad.Controlar(firma.Certificado!, momento);
        if (!control.Valido)
            h.Add(new("E04", control.Error!));
        else if (cfe.RutEmisor is { } rut && control.Rut != rut)
            h.Add(new("E04", $"El certificado de la firma es del RUT {control.Rut} y el CFE es del {rut}."));
    }

    private static void Formato(CfeLeido cfe, IReadOnlyList<string> erroresXsd, List<Hallazgo> h)
    {
        if (erroresXsd.Count > 0)
            h.Add(new("E05", $"No valida contra el XSD: {erroresXsd[0]}" + (erroresXsd.Count > 1 ? $" (y {erroresXsd.Count - 1} errores más)" : "")));
        if (cfe.Familia is not { } familia)
        {
            h.Add(new("E05", "El CFE no contiene ningún comprobante del formato (eTck, eFact, eFact_Exp, eRem, eRem_Exp, eResg o eBoleta)."));
            return;
        }
        if (cfe.Tipo is not { } tipo)
            return;
        if (!TiposCfe.Existe(tipo))
            h.Add(new("E05", $"TipoCFE {tipo} no es un tipo de CFE."));
        else if (TiposCfe.FamiliaDe(tipo) != familia)
            h.Add(new("E05", $"TipoCFE {tipo} ({TiposCfe.Nombre(tipo)}) no va en {TiposCfe.Raiz(familia)}."));
    }

    private void Cae(CfeLeido cfe, string rut, int tipo, string serie, long nro, List<Hallazgo> h)
    {
        var datos = cfe.CaeData;
        if (XmlUtil.Entero(datos.Texto("CAE_ID")) is not { } id)
            return;
        var cae = almacen.Cae(id);
        if (cae is null)
        {
            h.Add(new("E09", $"El CAE {id} no existe en el simulador (se crea con POST /admin/cae)."));
            return;
        }
        if (cae.Rut != rut || cae.Tipo != tipo)
        {
            h.Add(new("E09", $"El CAE {id} es del RUT {cae.Rut} para el tipo {cae.Tipo}, no del {rut} para el tipo {tipo}."));
            return;
        }
        if (cae.Serie != serie || nro < cae.Desde || nro > cae.Hasta)
            h.Add(new("E03", $"{serie}{nro} no está en el rango {cae.Serie}{cae.Desde}–{cae.Serie}{cae.Hasta} del CAE {id}."));
        if (XmlUtil.Entero(datos.Texto("DNro")) != cae.Desde || XmlUtil.Entero(datos.Texto("HNro")) != cae.Hasta)
            h.Add(new("E03", $"CAEData informa el rango {datos.Texto("DNro")}–{datos.Texto("HNro")} y el CAE {id} autoriza {cae.Desde}–{cae.Hasta}."));
        if (Hora.LeerFecha(datos.Texto("FecVenc")) != cae.Vencimiento)
            h.Add(new("E03", $"CAEData informa el vencimiento {datos.Texto("FecVenc")} y el CAE {id} vence el {Hora.Formato(cae.Vencimiento)}."));

        if (cfe.Firma is { } firma)
        {
            var dia = Hora.Fecha(firma);
            if (dia > cae.Vencimiento)
                h.Add(new("E07", $"Se firmó el {Hora.Formato(dia)}, después del vencimiento del CAE ({Hora.Formato(cae.Vencimiento)})."));
            else if (dia < cae.Autorizacion)
                h.Add(new("E07", $"Se firmó el {Hora.Formato(dia)}, antes de la autorización del CAE ({Hora.Formato(cae.Autorizacion)})."));
            if (firma > reloj.GetUtcNow().AddMinutes(10))
                h.Add(new("E07", $"La fecha de firma {Hora.Formato(firma)} es posterior a la recepción.", Confirmado: false));
        }

        var especial = XmlUtil.Entero(datos.Texto("CAEEspecial"));
        if (especial != cae.Especial)
            h.Add(new("E11", $"CAEData informa CAEEspecial {especial?.ToString() ?? "(ninguno)"} y el CAE {id} es {cae.Especial?.ToString() ?? "común"}."));
        var causal = XmlUtil.Entero(datos.Texto("CausalCAEEsp"));
        if (cae.Especial is not null && causal != cae.Causal)
            h.Add(new("E13", $"CAEData informa la causal {causal?.ToString() ?? "(ninguna)"} y el CAE {id} tiene la causal {cae.Causal?.ToString() ?? "(ninguna)"}."));
    }

    private void MontoHabitual(CfeLeido cfe, List<Hallazgo> h)
    {
        if (_opciones.MontoHabitualMaximo is not { } maximo || cfe.Total is not { } total)
            return;
        var pesos = total * (cfe.TipoCambio ?? 1m);
        if (pesos > maximo)
            h.Add(new("E06", $"El total ({ReglasFormato.Redondear(pesos)} pesos) supera el monto habitual configurado ({maximo})."));
    }

    private static void ComplementoFiscal(CfeLeido cfe, List<Hallazgo> h)
    {
        var datos = cfe.ComplFiscal.Hijo("Compl_Fiscal_Data");
        if (datos?.Texto("RUCEmisor") is { } rut && rut != cfe.RutEmisor)
            h.Add(new("E08", $"Compl_Fiscal informa el RUC {rut} y el emisor del CFE es {cfe.RutEmisor}."));
        if (cfe.Tipo is { } tipo && TiposCfe.EsCuentaAjena(tipo) && cfe.ComplFiscal is null)
            h.Add(new("E05", "Los comprobantes de venta por cuenta ajena requieren el complemento fiscal (Compl_Fiscal) con el mandante."));
    }

    private void Referencias(CfeLeido cfe, ContextoSobre contexto, List<Hallazgo> h)
    {
        foreach (var referencia in cfe.Referencias)
        {
            if (referencia.Texto("IndGlobal") == "1"
                || XmlUtil.Entero(referencia.Texto("TpoDocRef")) is not { } tipo
                || referencia.Texto("Serie") is not { } serie
                || XmlUtil.Entero(referencia.Texto("NroCFERef")) is not { } nro
                || cfe.RutEmisor is not { } rut)
                continue;
            if (contexto.Aceptados.Contains(((int)tipo, serie, nro)) || almacen.CfeAceptado(rut, (int)tipo, serie, nro) is not null)
                continue;
            h.Add(new("E12", $"La referencia {referencia.Texto("NroLinRef")} indica {TiposCfe.Nombre((int)tipo)} {serie}{nro}, que la DGI no tiene aceptado.",
                _opciones.ExigirReferenciasConocidas));
        }
    }
}
