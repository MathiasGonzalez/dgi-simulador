using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DgiSimulador.Estado;
using Microsoft.Extensions.Options;

namespace DgiSimulador.Certificados;

/// <summary>Resultado de controlar un certificado: el RUT de su titular o el motivo por el que no se acepta.</summary>
internal sealed record ControlCertificado(string? Rut, string? Error)
{
    internal bool Valido => Error is null;
}

/// <summary>Certificado emitido por la CA del simulador, con la clave privada.</summary>
public sealed record CertificadoEmitido(string Rut, string Huella, byte[] Pfx, string CertificadoPem, string ClavePem);

/// <summary>
/// La CA de prueba del simulador y el certificado con el que "la DGI" firma sus acuses. Se crean la primera vez y se
/// guardan en la base: los certificados que emite siguen valiendo después de reiniciar.
/// </summary>
internal sealed class Autoridad
{
    internal const string RutDgi = "214844360018";
    private const string OidSerialNumber = "2.5.4.5";

    private readonly Almacen _almacen;
    private readonly OpcionesSimulador _opciones;
    private readonly TimeProvider _reloj;

    public Autoridad(Almacen almacen, IOptions<OpcionesSimulador> opciones, TimeProvider reloj)
    {
        _almacen = almacen;
        _opciones = opciones.Value;
        _reloj = reloj;
        Ca = Cargar("ca") ?? Guardar("ca", CrearCa());
        Dgi = Cargar("dgi") ?? Guardar("dgi", Emitir(RutDgi, "DGI - Simulador", años: 20));
    }

    /// <summary>CA de prueba, con clave privada.</summary>
    internal X509Certificate2 Ca { get; }

    /// <summary>Certificado de "la DGI" (RUT 214844360018), con clave privada.</summary>
    internal X509Certificate2 Dgi { get; }

    /// <summary>Emite un certificado para <paramref name="rut"/> con SERIALNUMBER <c>RUC</c>+RUT, como los de las CA acreditadas.</summary>
    internal X509Certificate2 Emitir(string rut, string nombre, int años = 3)
    {
        using var rsa = RSA.Create(2048);
        var subject = new X500DistinguishedNameBuilder();
        subject.AddCountryOrRegion("UY");
        subject.AddOrganizationName(nombre);
        subject.Add(OidSerialNumber, "RUC" + rut, UniversalTagNumber.PrintableString);
        subject.AddCommonName(nombre);

        var pedido = new CertificateRequest(subject.Build(), rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        pedido.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        pedido.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation | X509KeyUsageFlags.KeyEncipherment, true));
        pedido.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(pedido.PublicKey, false));
        pedido.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(Ca, true, false));

        var ahora = _reloj.GetUtcNow();
        var serie = RandomNumberGenerator.GetBytes(16);
        serie[0] &= 0x7F;
        using var publico = pedido.Create(Ca, ahora.AddDays(-1), ahora.AddYears(años), serie);
        using var conClave = publico.CopyWithPrivateKey(rsa);
        return Recargar(conClave.Export(X509ContentType.Pfx));
    }

    /// <summary>Emite un certificado y devuelve también los PEM y el PFX (sin contraseña) para configurar el producto.</summary>
    internal CertificadoEmitido EmitirExportable(string rut, string nombre)
    {
        using var certificado = Emitir(rut, nombre);
        using var rsa = certificado.GetRSAPrivateKey()!;
        var pfx = certificado.Export(X509ContentType.Pfx);
        _almacen.GuardarCertificado(certificado.Thumbprint, rut, certificado.RawData, raiz: false, pfx);
        return new CertificadoEmitido(rut, certificado.Thumbprint, pfx, certificado.ExportCertificatePem(), rsa.ExportPkcs8PrivateKeyPem());
    }

    /// <summary>El certificado con clave de una empresa simulada (el último que se le emitió), emitiéndolo si no tiene.</summary>
    internal X509Certificate2 CertificadoDe(string rut, string nombre)
    {
        if (_almacen.PfxDe(rut) is { } pfx)
            return Recargar(pfx);
        var emitido = EmitirExportable(rut, nombre);
        return Recargar(emitido.Pfx);
    }

    /// <summary>
    /// Controla que el certificado esté vigente en <paramref name="momento"/>, identifique un RUT y sea de confianza:
    /// emitido por la CA del simulador o por una raíz importada, o importado él mismo.
    /// </summary>
    internal ControlCertificado Controlar(X509Certificate2 certificado, DateTimeOffset momento)
    {
        var rut = RutDe(certificado);
        if (rut is null)
            return new(null, "El certificado no identifica un RUT (SERIALNUMBER = RUC + RUT en el titular).");
        if (momento.UtcDateTime < certificado.NotBefore.ToUniversalTime() || momento.UtcDateTime > certificado.NotAfter.ToUniversalTime())
            return new(rut, $"El certificado no estaba vigente el {Hora.Formato(momento)} (vale del {certificado.NotBefore:yyyy-MM-dd} al {certificado.NotAfter:yyyy-MM-dd}).");
        if (string.Equals(_opciones.Confianza, "Cualquiera", StringComparison.OrdinalIgnoreCase))
            return new(rut, null);
        if (_almacen.CertificadoRegistrado(certificado.Thumbprint))
            return new(rut, null);

        using var cadena = new X509Chain();
        cadena.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        cadena.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        cadena.ChainPolicy.VerificationTime = momento.UtcDateTime;
        cadena.ChainPolicy.CustomTrustStore.Add(Ca);
        foreach (var raiz in _almacen.Raices())
            cadena.ChainPolicy.CustomTrustStore.Add(X509CertificateLoader.LoadCertificate(raiz));
        return cadena.Build(certificado)
            ? new(rut, null)
            : new(rut, "El certificado no es de una CA de confianza del simulador: importelo o importe su raíz (POST /admin/certificados).");
    }

    /// <summary>El RUT del titular: el SERIALNUMBER del subject, sin el prefijo <c>RUC</c>.</summary>
    internal static string? RutDe(X509Certificate2 certificado)
    {
        foreach (var rdn in certificado.SubjectName.EnumerateRelativeDistinguishedNames())
        {
            if (rdn.HasMultipleElements || rdn.GetSingleElementType().Value != OidSerialNumber)
                continue;
            var digitos = new string((rdn.GetSingleElementValue() ?? "").Where(char.IsAsciiDigit).ToArray());
            if (digitos.Length == 12)
                return digitos;
        }
        return null;
    }

    private X509Certificate2 CrearCa()
    {
        using var rsa = RSA.Create(2048);
        var pedido = new CertificateRequest("CN=Simulador DGI - CA de prueba, O=dgi-simulador, C=UY", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        pedido.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        pedido.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        pedido.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(pedido.PublicKey, false));
        var ahora = _reloj.GetUtcNow();
        using var ca = pedido.CreateSelfSigned(ahora.AddDays(-1), ahora.AddYears(30));
        return Recargar(ca.Export(X509ContentType.Pfx));
    }

    private X509Certificate2? Cargar(string nombre) => _almacen.Clave(nombre) is { } pfx ? Recargar(pfx) : null;

    private X509Certificate2 Guardar(string nombre, X509Certificate2 certificado)
    {
        _almacen.GuardarClave(nombre, certificado.Export(X509ContentType.Pfx));
        return certificado;
    }

    /// <summary>Carga un PFX con la clave exportable (en macOS no existe el conjunto de claves efímero).</summary>
    internal static X509Certificate2 Recargar(byte[] pfx) =>
        X509CertificateLoader.LoadPkcs12(pfx, null, OperatingSystem.IsMacOS()
            ? X509KeyStorageFlags.DefaultKeySet | X509KeyStorageFlags.Exportable
            : X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
}
