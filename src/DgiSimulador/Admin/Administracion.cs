using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using DgiSimulador.Certificados;
using DgiSimulador.Estado;
using DgiSimulador.Recepcion;
using DgiSimulador.Xml;
using Microsoft.Extensions.Options;

namespace DgiSimulador.Admin;

/// <summary>Alta de un emisor del padrón. Con <c>simulado</c> es una empresa que simula el simulador.</summary>
public sealed class NuevoEmisor
{
    public required string Rut { get; init; }
    public string? Denominacion { get; init; }
    public string? Mail { get; init; }
    public string? UrlServicio { get; init; }
    public string? MailTecnico { get; init; }
    public DateOnly? FechaInicio { get; init; }
    public DateOnly? FechaFinTransicion { get; init; }
    public DateOnly? FechaFin { get; init; }
    public bool Simulado { get; init; }

    /// <summary>Emitir un certificado de prueba para el RUT (por defecto sí).</summary>
    public bool EmitirCertificado { get; init; } = true;

    /// <summary>
    /// Tipos de CFE para los que se autoriza un CAE (serie A, del 1 al 100000), para dejar el emisor listo en una sola llamada.
    /// Un tipo que ya tiene CAE no recibe otro.
    /// </summary>
    public List<int> Tipos { get; init; } = [];
}

/// <summary>El CAE con los nombres de <c>UruFactura.Models.Cae</c>, para cargarlo en un producto .NET.</summary>
public sealed record DatosCae(
    string NumeroAutorizacion,
    int TipoCfe,
    string Serie,
    long RangoDesde,
    long RangoHasta,
    DateOnly FechaAutorizacion,
    DateOnly FechaVencimiento,
    int? Especial,
    int? CausalEspecial)
{
    internal static DatosCae De(Cae cae) =>
        new(XmlUtil.Numero(cae.Id), cae.Tipo, cae.Serie, cae.Desde, cae.Hasta, cae.Autorizacion, cae.Vencimiento, cae.Especial, cae.Causal);
}

/// <summary>Certificado que se importa (PEM o DER en base64, o un archivo en la semilla). Con <c>raiz</c> es una CA de confianza.</summary>
public sealed class NuevoCertificado
{
    public string? Pem { get; init; }
    public string? Base64 { get; init; }
    public string? Archivo { get; init; }
    public string? Rut { get; init; }
    public bool Raiz { get; init; }
}

/// <summary>CAE que se autoriza. Sin <c>id</c> se asigna un número de autorización.</summary>
public sealed class NuevoCae
{
    public required string Rut { get; init; }
    public required int Tipo { get; init; }
    public string Serie { get; init; } = "A";
    public long Desde { get; init; } = 1;
    public long Hasta { get; init; } = 100_000;
    public DateOnly? Autorizacion { get; init; }
    public DateOnly? Vencimiento { get; init; }
    public int? Especial { get; init; }
    public int? Causal { get; init; }
    public long? Id { get; init; }
}

/// <summary>El estado inicial que se carga al arrancar (<c>Simulador:Semilla</c>).</summary>
public sealed class Semilla
{
    public List<NuevoEmisor> Emisores { get; init; } = [];
    public List<NuevoCertificado> Certificados { get; init; } = [];
    public List<NuevoCae> Caes { get; init; } = [];
}

/// <summary>Las altas que usan la API de administración y la semilla.</summary>
internal sealed class Administracion(Almacen almacen, Autoridad autoridad, IOptions<OpcionesSimulador> opciones, TimeProvider reloj, ILogger<Administracion> log)
{
    internal (Emisor Emisor, CertificadoEmitido? Certificado, IReadOnlyList<Cae> Caes) CrearEmisor(NuevoEmisor nuevo)
    {
        if (!Documentos.RutValido(nuevo.Rut))
            throw new ArgumentException($"{nuevo.Rut} no es un RUT válido (12 dígitos con dígito verificador).");
        if (nuevo.Tipos.Where(t => !TiposCfe.Existe(t)).ToList() is { Count: > 0 } invalidos)
            throw new ArgumentException($"No son tipos de CFE: {string.Join(", ", invalidos)}.");
        var ahora = reloj.GetUtcNow();
        var emisor = new Emisor(nuevo.Rut, nuevo.Denominacion ?? $"Empresa {nuevo.Rut}", nuevo.Mail, nuevo.UrlServicio, nuevo.MailTecnico,
            nuevo.FechaInicio ?? Hora.Fecha(ahora), nuevo.FechaFinTransicion, nuevo.FechaFin, nuevo.Simulado, ahora);
        almacen.GuardarEmisor(emisor);
        var certificado = nuevo.EmitirCertificado ? autoridad.EmitirExportable(emisor.Rut, emisor.Denominacion) : null;
        var existentes = almacen.Caes(emisor.Rut);
        var caes = nuevo.Tipos.Distinct()
            .Select(t => existentes.FirstOrDefault(c => c.Tipo == t) ?? CrearCae(new NuevoCae { Rut = emisor.Rut, Tipo = t }))
            .ToList();
        return (emisor, certificado, caes);
    }

    internal string ImportarCertificado(NuevoCertificado nuevo)
    {
        byte[] der;
        if (nuevo.Archivo is { Length: > 0 } archivo)
            der = X509CertificateLoader.LoadCertificateFromFile(archivo).RawData;
        else if (nuevo.Pem is { Length: > 0 } pem)
            der = X509Certificate2.CreateFromPem(pem).RawData;
        else if (nuevo.Base64 is { Length: > 0 } base64)
            der = Convert.FromBase64String(base64);
        else
            throw new ArgumentException("Indique 'pem', 'base64' o 'archivo'.");

        using var certificado = X509CertificateLoader.LoadCertificate(der);
        var rut = nuevo.Rut ?? Autoridad.RutDe(certificado);
        if (!nuevo.Raiz && rut is null)
            throw new ArgumentException("El certificado no tiene SERIALNUMBER con RUC + RUT: indique 'rut'.");
        almacen.GuardarCertificado(certificado.Thumbprint, rut, der, nuevo.Raiz);
        if (!nuevo.Raiz && rut is not null && almacen.Emisor(rut) is null)
            almacen.GuardarEmisor(new Emisor(rut, certificado.GetNameInfo(X509NameType.SimpleName, false) ?? rut, null, null, null,
                Hora.Fecha(reloj.GetUtcNow()), null, null, false, reloj.GetUtcNow()));
        return certificado.Thumbprint;
    }

    internal Cae CrearCae(NuevoCae nuevo)
    {
        if (!TiposCfe.Existe(nuevo.Tipo))
            throw new ArgumentException($"{nuevo.Tipo} no es un tipo de CFE.");
        if (nuevo.Desde < 1 || nuevo.Hasta < nuevo.Desde || nuevo.Hasta > 9_999_999)
            throw new ArgumentException("El rango tiene que estar entre 1 y 9999999, con desde ≤ hasta.");
        var hoy = Hora.Fecha(reloj.GetUtcNow());
        var cae = new Cae(nuevo.Id ?? 90_000_000_000 + almacen.Siguiente("cae"), nuevo.Rut, nuevo.Tipo, nuevo.Serie, nuevo.Desde, nuevo.Hasta,
            nuevo.Autorizacion ?? hoy.AddDays(-1), nuevo.Vencimiento ?? hoy.AddYears(1), nuevo.Especial, nuevo.Causal);
        almacen.GuardarCae(cae);
        return cae;
    }

    internal CaeAutorizado Autorizado(Cae cae) => new(DatosCae.De(cae), Constancia(cae));

    /// <summary>La constancia de CAE como la descarga el portal de la DGI (<c>CAE/DA</c>, firmada).</summary>
    internal string Constancia(Cae cae) => Firmas.FirmarEnveloped(XmlUtil.Escribir(w =>
    {
        w.WriteStartDocument();
        w.WriteStartElement("ns0", "CAE", Ns.Cfe);
        w.WriteAttributeString("version", "1.0");
        w.WriteElementString("ns0", "TmstFrma", Ns.Cfe, Hora.Formato(reloj.GetUtcNow()));
        w.WriteStartElement("ns0", "DA", Ns.Cfe);
        w.WriteElementString("ns0", "RucE", Ns.Cfe, cae.Rut);
        w.WriteElementString("ns0", "TCFE", Ns.Cfe, XmlUtil.Numero(cae.Tipo));
        w.WriteElementString("ns0", "Serie", Ns.Cfe, cae.Serie);
        w.WriteElementString("ns0", "DNro", Ns.Cfe, XmlUtil.Numero(cae.Desde));
        w.WriteElementString("ns0", "HNro", Ns.Cfe, XmlUtil.Numero(cae.Hasta));
        w.WriteElementString("ns0", "NA", Ns.Cfe, XmlUtil.Numero(cae.Id));
        w.WriteElementString("ns0", "FA", Ns.Cfe, Hora.Formato(cae.Autorizacion));
        w.WriteElementString("ns0", "FVD", Ns.Cfe, Hora.Formato(cae.Vencimiento));
        if (cae.Especial is { } especial)
            w.WriteElementString("ns0", "CAEEspecial", Ns.Cfe, XmlUtil.Numero(especial));
        if (cae.Causal is { } causal)
            w.WriteElementString("ns0", "CausalCAEEsp", Ns.Cfe, XmlUtil.Numero(causal));
        w.WriteEndElement();
        w.WriteEndElement();
    }), autoridad.Dgi);

    /// <summary>Carga la semilla configurada. Lo que ya existe se actualiza, así que se puede aplicar en cada arranque.</summary>
    internal void AplicarSemilla()
    {
        if (opciones.Value.Semilla is not { Length: > 0 } archivo)
            return;
        var semilla = JsonSerializer.Deserialize<Semilla>(File.ReadAllText(archivo), new JsonSerializerOptions(JsonSerializerDefaults.Web))
                      ?? new Semilla();
        var directorio = Path.GetDirectoryName(Path.GetFullPath(archivo))!;
        // Un elemento con problemas (un certificado que no está, un RUT inválido) se informa y no impide arrancar.
        int emisores = 0, certificados = 0, caes = 0;
        bool Aplicar(string que, Action accion)
        {
            try
            {
                accion();
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or FormatException or System.Security.Cryptography.CryptographicException)
            {
                log.LogWarning("Semilla {Archivo}: no se cargó {Que}: {Error}", archivo, que, ex.Message);
                return false;
            }
        }
        foreach (var emisor in semilla.Emisores)
        {
            // Un emisor que ya tiene certificado no recibe otro en cada arranque.
            var conCertificado = emisor.EmitirCertificado && almacen.Certificados().All(c => c.Rut != emisor.Rut);
            if (Aplicar($"el emisor {emisor.Rut}", () => caes += CrearEmisor(new NuevoEmisor
                {
                    Rut = emisor.Rut, Denominacion = emisor.Denominacion, Mail = emisor.Mail, UrlServicio = emisor.UrlServicio, MailTecnico = emisor.MailTecnico,
                    FechaInicio = emisor.FechaInicio, FechaFinTransicion = emisor.FechaFinTransicion, FechaFin = emisor.FechaFin, Simulado = emisor.Simulado,
                    EmitirCertificado = conCertificado, Tipos = emisor.Tipos,
                }).Caes.Count))
                emisores++;
        }
        foreach (var certificado in semilla.Certificados)
        {
            var archivoCertificado = certificado.Archivo is { Length: > 0 } a ? Path.Combine(directorio, a) : null;
            if (Aplicar($"el certificado {certificado.Archivo ?? certificado.Rut}", () => ImportarCertificado(new NuevoCertificado
                {
                    Archivo = archivoCertificado, Pem = certificado.Pem, Base64 = certificado.Base64, Rut = certificado.Rut, Raiz = certificado.Raiz,
                })))
                certificados++;
        }
        foreach (var cae in semilla.Caes)
        {
            var existe = cae.Id is null && almacen.Caes(cae.Rut).Any(c => c.Tipo == cae.Tipo && c.Serie == cae.Serie && c.Desde == cae.Desde && c.Hasta == cae.Hasta);
            if (existe || Aplicar($"el CAE {cae.Id?.ToString() ?? $"{cae.Tipo} {cae.Serie}"} de {cae.Rut}", () => CrearCae(cae)))
                caes++;
        }
        log.LogInformation("Semilla {Archivo}: {Emisores} emisores, {Certificados} certificados y {Caes} CAE", archivo, emisores, certificados, caes);
    }
}
