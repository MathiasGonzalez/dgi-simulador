namespace DgiSimulador;

/// <summary>Configuración del simulador: la sección <c>Simulador</c> de <c>appsettings.json</c> o variables <c>Simulador__*</c>.</summary>
public sealed class OpcionesSimulador
{
    /// <summary>Carpeta del estado (base SQLite y claves). Se monta como volumen en Docker.</summary>
    public string Datos { get; set; } = "datos";

    /// <summary>Cadena de conexión SQLite; por defecto <c>{Datos}/simulador.db</c>.</summary>
    public string? BaseDeDatos { get; set; }

    /// <summary>Segundos desde la recepción de un sobre hasta que se puede consultar el resultado (<c>Fechahora</c>).</summary>
    public int DemoraConsultaSegundos { get; set; } = 2;

    /// <summary>
    /// Con <c>true</c> las advertencias (reglas inferidas, no confirmadas en la normativa) rechazan como los
    /// controles confirmados. Por defecto solo se anotan.
    /// </summary>
    public bool Estricto { get; set; }

    /// <summary>
    /// RUT que se aceptan como <c>RutReceptor</c> del sobre. 214844360018 es el de la DGI; 219999830019 es el "RUT de DGI de
    /// pruebas" que algunos productos usan en el ambiente de prueba. Cuál exige ese ambiente no está confirmado: sin
    /// configurar se aceptan los dos.
    /// </summary>
    public List<string>? RutsDgi { get; set; }

    internal IReadOnlyList<string> RutsDgiAceptados => RutsDgi is { Count: > 0 } ruts ? ruts : ["214844360018", "219999830019"];

    /// <summary>Diferencia admitida al comparar montos calculados con los informados.</summary>
    public decimal Tolerancia { get; set; } = 0.01m;

    /// <summary>Tamaño máximo de un sobre, en KB (S07). El Formato del Sobre v05 fija 2 MB.</summary>
    public int TamanoMaximoKb { get; set; } = 2_048;

    /// <summary>
    /// Tamaño máximo de un reporte diario, en KB (R01). El Formato del Reporte no fija uno: es solo una protección del
    /// simulador.
    /// </summary>
    public int TamanoMaximoReporteKb { get; set; } = 10_240;

    /// <summary>
    /// Qué certificados se aceptan: <c>Registrados</c> (emitidos por la CA del simulador o importados) o
    /// <c>Cualquiera</c> (cualquiera vigente, con el RUT en SERIALNUMBER).
    /// </summary>
    public string Confianza { get; set; } = "Registrados";

    /// <summary>
    /// Valor de la Unidad Indexada al 31/12 de cada año (clave: el año). Fija el tope de 5.000 UI de los
    /// e-Tickets del año siguiente; sin el año, el tope no se controla.
    /// </summary>
    public Dictionary<string, decimal> UnidadIndexada { get; set; } = [];

    /// <summary>Rango admitido del tipo de cambio por moneda (E10). Las monedas que no están solo exigen un valor positivo.</summary>
    public Dictionary<string, RangoTipoCambio> TiposDeCambio { get; set; } = [];

    /// <summary>Monto total en pesos a partir del que un CFE se rechaza con E06. Sin valor no se controla.</summary>
    public decimal? MontoHabitualMaximo { get; set; }

    /// <summary>Si una referencia a un CFE que el simulador no recibió rechaza (E12) o solo se advierte.</summary>
    public bool ExigirReferenciasConocidas { get; set; } = true;

    /// <summary>
    /// Códigos de retención, percepción y crédito fiscal registrados en la DGI (número de formulario más número de
    /// línea, B-C20). La DGI no publica la tabla en los formatos: sin configurar solo se controla que sea un número
    /// mayor que cero; con la lista, un código que no está rechaza (E05). Del 9999001 al 9999999 nunca se validan.
    /// </summary>
    public List<string>? CodigosDeRetencion { get; set; }

    /// <summary>Tasa mínima de IVA vigente.</summary>
    public decimal TasaMinima { get; set; } = 10m;

    /// <summary>Tasa básica de IVA vigente.</summary>
    public decimal TasaBasica { get; set; } = 22m;

    /// <summary>URL con la que se publica el simulador (para el <c>soap:address</c> de los WSDL). Por defecto, la de cada pedido.</summary>
    public string? UrlPublica { get; set; }

    /// <summary>
    /// URL a la que el receptor simulado entrega sus respuestas por correo (MIME), por ejemplo el handler de
    /// correo de <c>wrangler dev</c>: <c>http://localhost:8787/cdn-cgi/handler/email</c>.
    /// </summary>
    public string? UrlEntregaCorreo { get; set; }

    /// <summary>
    /// Clave de la API de administración. Sin valor, <c>/admin</c> queda abierto: quien llega al puerto baja las claves de
    /// los certificados de prueba, borra el estado y hace que el simulador llame a la URL que quiera. Hay que definirla
    /// si el simulador escucha fuera de la máquina o del job de CI (SECURITY.md).
    /// </summary>
    public string? ClaveAdmin { get; set; }

    /// <summary>Archivo JSON con emisores, certificados y CAE que se cargan al iniciar.</summary>
    public string? Semilla { get; set; }

    /// <summary>Archivo con el set de pruebas de homologación; por defecto el provisorio incluido.</summary>
    public string? SetDePruebas { get; set; }
}

/// <summary>Rango de un tipo de cambio.</summary>
public sealed class RangoTipoCambio
{
    public decimal Minimo { get; set; }
    public decimal Maximo { get; set; }
}
