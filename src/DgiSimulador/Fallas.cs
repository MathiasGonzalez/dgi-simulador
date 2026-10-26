using DgiSimulador.Estado;
using DgiSimulador.Soap;

namespace DgiSimulador;

/// <summary>
/// Fallas programadas (<c>POST /admin/fallas</c>) para probar cómo reacciona el producto ante lo que la DGI real no
/// deja provocar. Las de transporte reemplazan la respuesta; las de negocio cambian el resultado.
/// </summary>
internal sealed class Fallas(Almacen almacen)
{
    /// <summary>Tipos de falla y lo que hacen.</summary>
    internal static readonly IReadOnlyDictionary<string, string> Tipos = new Dictionary<string, string>
    {
        ["timeout"] = "No responde durante 'segundos' (por defecto 120) y después devuelve HTTP 504.",
        ["http500"] = "HTTP 500 sin SOAP.",
        ["http503"] = "HTTP 503 (servicio no disponible).",
        ["html502"] = "HTTP 502 con una página HTML, como la de un proxy.",
        ["soapfault"] = "SOAP Fault con 'detalle' como faultstring.",
        ["rechazo"] = "Rechaza con 'codigo': S.. el sobre, E.. cada CFE del sobre o R.. el reporte.",
        ["demora"] = "El resultado del sobre recién se puede consultar después de 'segundos'.",
    };

    private static readonly string[] DeTransporte = ["timeout", "http500", "http503", "html502", "soapfault"];

    /// <summary>Si hay una falla de transporte para la operación, la aplica y devuelve la respuesta que reemplaza a la normal.</summary>
    internal async Task<IResult?> Transporte(string operacion, CancellationToken cancelacion)
    {
        if (almacen.TomarFalla(operacion, DeTransporte) is not { } falla)
            return null;
        switch (falla.Tipo)
        {
            case "timeout":
                await Task.Delay(TimeSpan.FromSeconds(falla.Segundos ?? 120), cancelacion);
                return Results.Content("Gateway Timeout", "text/plain", statusCode: StatusCodes.Status504GatewayTimeout);
            case "http500":
                return Results.Content("Internal Server Error", "text/plain", statusCode: StatusCodes.Status500InternalServerError);
            case "http503":
                return Results.Content("Service Unavailable", "text/plain", statusCode: StatusCodes.Status503ServiceUnavailable);
            case "html502":
                return Results.Content("<html><head><title>502 Bad Gateway</title></head><body><h1>502 Bad Gateway</h1></body></html>",
                    "text/html", statusCode: StatusCodes.Status502BadGateway);
            default:
                return Results.Content(MensajeSoap.Fault(falla.Detalle ?? "Falla programada en el simulador"), "text/xml; charset=utf-8",
                    statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>Toma una falla de negocio de ese tipo para la operación, si hay.</summary>
    internal Falla? Negocio(string operacion, string tipo) => almacen.TomarFalla(operacion, [tipo]);

    /// <summary>Toma un rechazo programado cuyo código empieza con <paramref name="prefijo"/> (S, E o R).</summary>
    internal string? Rechazo(string operacion, char prefijo)
    {
        var falla = almacen.Fallas().FirstOrDefault(f => f.Tipo == "rechazo" && (f.Operacion == operacion || f.Operacion == "*")
                                                         && f.Codigo is { Length: > 0 } c && char.ToUpperInvariant(c[0]) == prefijo);
        if (falla is null)
            return null;
        almacen.TomarFallaPorId(falla.Id);
        return falla.Codigo!.ToUpperInvariant();
    }
}
