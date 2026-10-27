using System.Globalization;

namespace DgiSimulador;

/// <summary>Fechas y horas de Uruguay (UTC-3 todo el año) en el formato de los mensajes de la DGI.</summary>
internal static class Hora
{
    internal static readonly TimeSpan Offset = TimeSpan.FromHours(-3);

    internal static DateTimeOffset Uruguay(DateTimeOffset momento) => momento.ToOffset(Offset);

    internal static DateOnly Fecha(DateTimeOffset momento) => DateOnly.FromDateTime(Uruguay(momento).DateTime);

    /// <summary><c>FechaHoraType</c>: <c>2026-10-26T14:30:00-03:00</c>.</summary>
    internal static string Formato(DateTimeOffset momento) =>
        Uruguay(momento).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    internal static string Formato(DateOnly fecha) => fecha.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>El momento sin la fracción de segundo (los mensajes de la DGI no llevan fracciones).</summary>
    internal static DateTimeOffset AlSegundo(DateTimeOffset momento) => momento.AddTicks(-(momento.Ticks % TimeSpan.TicksPerSecond));

    /// <summary>Una fecha y hora de un mensaje; sin offset es hora de Uruguay.</summary>
    internal static DateTimeOffset? Leer(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto)
            || !DateTime.TryParse(texto, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var valor))
            return null;
        return valor.Kind == DateTimeKind.Unspecified
            ? new DateTimeOffset(valor, Offset)
            : DateTimeOffset.Parse(texto, CultureInfo.InvariantCulture);
    }

    internal static DateOnly? LeerFecha(string? texto) =>
        DateOnly.TryParseExact(texto?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fecha)
            ? fecha
            : null;
}
