namespace DgiSimulador.Estado;

/// <summary>Emisor electrónico del padrón. <c>simulado</c> marca las empresas que simula el propio simulador.</summary>
public sealed record Emisor(
    string Rut,
    string Denominacion,
    string? Mail,
    string? UrlServicio,
    string? MailTecnico,
    DateOnly FechaInicio,
    DateOnly? FechaFinTransicion,
    DateOnly? FechaFin,
    bool Simulado,
    DateTimeOffset Modificado);

/// <summary>Rango de numeración autorizado (CAE). <c>id</c> es el número de autorización (<c>CAE_ID</c>).</summary>
public sealed record Cae(
    long Id,
    string Rut,
    int Tipo,
    string Serie,
    long Desde,
    long Hasta,
    DateOnly Autorizacion,
    DateOnly Vencimiento,
    int? Especial,
    int? Causal);

/// <summary>Motivo de rechazo u observación con su código oficial.</summary>
public sealed record Motivo(string Codigo, string Glosa, string? Detalle = null)
{
    public override string ToString() => Detalle is null ? $"{Codigo} {Glosa}" : $"{Codigo} {Glosa}: {Detalle}";
}

/// <summary>Sobre recibido por <c>EFACRECEPCIONSOBRE</c>.</summary>
public sealed record SobreRecibido(
    long IdReceptor,
    string Rut,
    long IdEmisor,
    string Estado,
    IReadOnlyList<Motivo> Motivos,
    string? Token,
    DateTimeOffset? ConsultarDesde,
    int Cantidad,
    DateTimeOffset Recibido,
    string Xml,
    string Acuse);

/// <summary>Un CFE de un sobre aceptado, con el resultado de los controles.</summary>
public sealed record CfeRegistrado(
    long Id,
    long IdReceptor,
    int Ordinal,
    string Rut,
    int? Tipo,
    string? Serie,
    long? Nro,
    DateOnly? Fecha,
    DateTimeOffset? Firma,
    string Estado,
    IReadOnlyList<Motivo> Motivos,
    IReadOnlyList<string> Advertencias,
    string? Moneda,
    decimal? TipoCambio,
    decimal? Neto,
    decimal? Iva,
    decimal? Total,
    decimal? Retenido,
    string? RutReceptor,
    DateTimeOffset Recibido,
    string Xml)
{
    public bool Aceptado => Estado is "AE" or "CE";
}

/// <summary>Reporte diario recibido por <c>EFACRECEPCIONREPORTE</c>.</summary>
public sealed record ReporteRecibido(
    long IdReceptor,
    string Rut,
    long IdEmisor,
    DateOnly? FechaResumen,
    int? Secuencia,
    string Estado,
    IReadOnlyList<Motivo> Motivos,
    IReadOnlyList<string> Advertencias,
    DateTimeOffset Recibido,
    string Xml,
    string Acuse);

/// <summary>
/// Falla programada para las próximas <c>restantes</c> llamadas a una operación (o a todas con <c>*</c>).
/// </summary>
public sealed record Falla(long Id, string Operacion, string Tipo, string? Codigo, string? Detalle, int Restantes, int? Segundos);

/// <summary>Mensaje del intercambio entre empresas, recibido o enviado por una empresa simulada.</summary>
public sealed record MensajeIntercambio(
    long Id,
    string Direccion,
    string Canal,
    string Tipo,
    string RutEmisor,
    string RutReceptor,
    long? IdEmisor,
    string? Nombre,
    string Xml,
    string? Respuesta,
    string Estado,
    DateTimeOffset Fecha);
