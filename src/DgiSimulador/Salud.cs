using System.Reflection;

namespace DgiSimulador;

/// <summary>Estado del simulador: su versión y el commit con el que se compiló.</summary>
public sealed record EstadoSalud(string Estado, string Version, string? Revision);

/// <summary>
/// Versión del simulador y control de salud: <c>GET /salud</c> y <c>--salud</c>, que usa el <c>HEALTHCHECK</c> de la
/// imagen para que los servicios de GitHub Actions y Docker Compose esperen a que esté listo.
/// </summary>
internal static class Salud
{
    /// <summary>Versión semántica del simulador: <c>Version</c> del proyecto.</summary>
    internal static readonly string Version;

    /// <summary>Commit con el que se compiló, si se conoce.</summary>
    internal static readonly string? Revision;

    static Salud()
    {
        var informativa = typeof(Salud).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var mas = informativa.IndexOf('+');
        Version = mas < 0 ? informativa : informativa[..mas];
        Revision = mas < 0 ? null : informativa[(mas + 1)..];
    }

    internal static void Mapear(WebApplication app) =>
        app.MapGet("/salud", () => new EstadoSalud("ok", Version, Revision))
            .WithTags("Salud").WithSummary("Responde cuando el simulador está listo, con su versión");

    /// <summary>
    /// Pide <c>/salud</c> al simulador que corre en este contenedor, en la dirección de <c>Urls</c>. Devuelve el código
    /// de salida del <c>HEALTHCHECK</c>: 0 si respondió.
    /// </summary>
    internal static async Task<int> Comprobar()
    {
        var urls = Environment.GetEnvironmentVariable("Urls") ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://localhost:8080";
        var url = urls.Split(';')[0].Replace("0.0.0.0", "127.0.0.1").Replace("+", "127.0.0.1").Replace("*", "127.0.0.1").TrimEnd('/');
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        try
        {
            using var respuesta = await http.GetAsync($"{url}/salud");
            return respuesta.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            return 1;
        }
    }
}
