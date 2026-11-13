using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace DgiSimulador.Admin;

/// <summary>
/// La clave opcional de la API de administración (<see cref="OpcionesSimulador.ClaveAdmin"/>). Sin clave, <c>/admin</c>
/// queda abierto, que es lo que hace falta en una máquina de desarrollo o en un job de CI. Con clave, cada pedido la
/// manda como <c>Authorization: Bearer</c> o como contraseña de HTTP Basic (con cualquier usuario), que es lo que pide
/// el navegador para ver la página <c>/admin</c>.
/// </summary>
internal static class Acceso
{
    internal static async ValueTask<object?> Exigir(EndpointFilterInvocationContext contexto, EndpointFilterDelegate siguiente)
    {
        var http = contexto.HttpContext;
        var clave = http.RequestServices.GetRequiredService<IOptions<OpcionesSimulador>>().Value.ClaveAdmin;
        if (clave is not { Length: > 0 } || Coincide(Presentada(http.Request.Headers.Authorization.ToString()), clave))
            return await siguiente(contexto);
        http.Response.Headers.WWWAuthenticate = "Basic realm=\"dgi-simulador\", charset=\"UTF-8\"";
        return Results.Json(new ErrorAdmin("Falta la clave de administración (Simulador:ClaveAdmin): Authorization: Bearer <clave>."),
            statusCode: StatusCodes.Status401Unauthorized);
    }

    /// <summary>La clave del encabezado <c>Authorization</c>: el token de <c>Bearer</c> o la contraseña de <c>Basic</c>.</summary>
    private static string? Presentada(string autorizacion)
    {
        if (autorizacion.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return autorizacion["Bearer ".Length..].Trim();
        if (!autorizacion.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            var credenciales = Encoding.UTF8.GetString(Convert.FromBase64String(autorizacion["Basic ".Length..].Trim()));
            var separador = credenciales.IndexOf(':');
            return separador < 0 ? null : credenciales[(separador + 1)..];
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Compara en tiempo constante, para que la respuesta no revele cuánto de la clave coincide.</summary>
    private static bool Coincide(string? presentada, string clave) =>
        presentada is not null && CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(presentada)), SHA256.HashData(Encoding.UTF8.GetBytes(clave)));
}
