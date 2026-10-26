using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace DgiSimulador.Estado;

/// <summary>
/// El estado del simulador en SQLite: claves, padrón, certificados, CAE, sobres, CFE, reportes, fallas e intercambio.
/// Cada operación abre su conexión (el pool las reutiliza); los montos se guardan como texto para no perder precisión.
/// </summary>
public sealed class Almacen
{
    private readonly string _conexion;

    public Almacen(IOptions<OpcionesSimulador> opciones)
    {
        var o = opciones.Value;
        if (o.BaseDeDatos is { Length: > 0 } cadena)
        {
            _conexion = cadena;
        }
        else
        {
            Directory.CreateDirectory(o.Datos);
            _conexion = new SqliteConnectionStringBuilder { DataSource = Path.Combine(o.Datos, "simulador.db"), DefaultTimeout = 30 }.ToString();
        }
        Crear();
    }

    private const string Esquema = """
        PRAGMA journal_mode = WAL;
        CREATE TABLE IF NOT EXISTS claves (nombre TEXT PRIMARY KEY, pfx BLOB NOT NULL);
        CREATE TABLE IF NOT EXISTS secuencias (nombre TEXT PRIMARY KEY, valor INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS emisores (
            rut TEXT PRIMARY KEY, denominacion TEXT NOT NULL, mail TEXT, url_servicio TEXT, mail_tecnico TEXT,
            fecha_inicio TEXT NOT NULL, fecha_fin_transicion TEXT, fecha_fin TEXT, simulado INTEGER NOT NULL, modificado TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS certificados (huella TEXT PRIMARY KEY, rut TEXT, der BLOB NOT NULL, raiz INTEGER NOT NULL, pfx BLOB, creado TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS cae (
            id INTEGER PRIMARY KEY, rut TEXT NOT NULL, tipo INTEGER NOT NULL, serie TEXT NOT NULL, desde INTEGER NOT NULL,
            hasta INTEGER NOT NULL, autorizacion TEXT NOT NULL, vencimiento TEXT NOT NULL, especial INTEGER, causal INTEGER);
        CREATE TABLE IF NOT EXISTS sobres (
            id_receptor INTEGER PRIMARY KEY, rut TEXT NOT NULL, id_emisor INTEGER NOT NULL, estado TEXT NOT NULL, motivos TEXT NOT NULL,
            token TEXT, consultar_desde TEXT, cantidad INTEGER NOT NULL, recibido TEXT NOT NULL, xml TEXT NOT NULL, acuse TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS sobres_emisor ON sobres (rut, id_emisor);
        CREATE TABLE IF NOT EXISTS cfes (
            id INTEGER PRIMARY KEY AUTOINCREMENT, id_receptor INTEGER NOT NULL, ordinal INTEGER NOT NULL, rut TEXT NOT NULL,
            tipo INTEGER, serie TEXT, nro INTEGER, fecha TEXT, firma TEXT, estado TEXT NOT NULL, motivos TEXT NOT NULL,
            advertencias TEXT NOT NULL, moneda TEXT, tipo_cambio TEXT, neto TEXT, iva TEXT, total TEXT, retenido TEXT,
            rut_receptor TEXT, recibido TEXT NOT NULL, xml TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS cfes_clave ON cfes (rut, tipo, serie, nro);
        CREATE INDEX IF NOT EXISTS cfes_receptor ON cfes (rut_receptor);
        CREATE TABLE IF NOT EXISTS anulados (rut TEXT NOT NULL, tipo INTEGER NOT NULL, serie TEXT NOT NULL, desde INTEGER NOT NULL, hasta INTEGER NOT NULL, fecha TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS reportes (
            id_receptor INTEGER PRIMARY KEY, rut TEXT NOT NULL, id_emisor INTEGER NOT NULL, fecha_resumen TEXT, secuencia INTEGER,
            estado TEXT NOT NULL, motivos TEXT NOT NULL, advertencias TEXT NOT NULL, recibido TEXT NOT NULL, xml TEXT NOT NULL, acuse TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS fallas (
            id INTEGER PRIMARY KEY AUTOINCREMENT, operacion TEXT NOT NULL, tipo TEXT NOT NULL, codigo TEXT, detalle TEXT,
            restantes INTEGER NOT NULL, segundos INTEGER);
        CREATE TABLE IF NOT EXISTS intercambio (
            id INTEGER PRIMARY KEY AUTOINCREMENT, direccion TEXT NOT NULL, canal TEXT NOT NULL, tipo TEXT NOT NULL,
            rut_emisor TEXT NOT NULL, rut_receptor TEXT NOT NULL, id_emisor INTEGER, nombre TEXT, xml TEXT NOT NULL,
            respuesta TEXT, estado TEXT NOT NULL, fecha TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS homologacion_manual (rut TEXT NOT NULL, caso TEXT NOT NULL, fecha TEXT NOT NULL, PRIMARY KEY (rut, caso));
        """;

    private void Crear() => Ejecutar(Esquema);

    /// <summary>Borra lo recibido (sobres, CFE, reportes, intercambio, fallas). Con <paramref name="todo"/> también el padrón, los certificados y los CAE.</summary>
    public void Limpiar(bool todo)
    {
        Ejecutar("DELETE FROM sobres; DELETE FROM cfes; DELETE FROM anulados; DELETE FROM reportes; DELETE FROM fallas; DELETE FROM intercambio; DELETE FROM homologacion_manual;");
        if (todo)
            Ejecutar("DELETE FROM emisores; DELETE FROM certificados; DELETE FROM cae; DELETE FROM secuencias;");
    }

    // --- Claves y secuencias ------------------------------------------------------------

    public byte[]? Clave(string nombre) => Uno("SELECT pfx FROM claves WHERE nombre = $n", r => (byte[])r[0], ("$n", nombre));

    public void GuardarClave(string nombre, byte[] pfx) =>
        Ejecutar("INSERT OR REPLACE INTO claves (nombre, pfx) VALUES ($n, $p)", ("$n", nombre), ("$p", pfx));

    /// <summary>Siguiente número de una secuencia (empieza en 1).</summary>
    public long Siguiente(string secuencia)
    {
        using var c = Abrir();
        using var t = c.BeginTransaction();
        var cmd = Comando(c, "INSERT INTO secuencias (nombre, valor) VALUES ($n, 1) ON CONFLICT (nombre) DO UPDATE SET valor = valor + 1 RETURNING valor", ("$n", secuencia));
        cmd.Transaction = t;
        var valor = (long)cmd.ExecuteScalar()!;
        t.Commit();
        return valor;
    }

    // --- Padrón y certificados ----------------------------------------------------------

    public void GuardarEmisor(Emisor e) => Ejecutar("""
        INSERT OR REPLACE INTO emisores (rut, denominacion, mail, url_servicio, mail_tecnico, fecha_inicio, fecha_fin_transicion, fecha_fin, simulado, modificado)
        VALUES ($rut, $d, $m, $u, $mt, $fi, $ft, $ff, $s, $mod)
        """, ("$rut", e.Rut), ("$d", e.Denominacion), ("$m", e.Mail), ("$u", e.UrlServicio), ("$mt", e.MailTecnico),
        ("$fi", Fecha(e.FechaInicio)), ("$ft", Fecha(e.FechaFinTransicion)), ("$ff", Fecha(e.FechaFin)), ("$s", e.Simulado ? 1 : 0),
        ("$mod", Momento(e.Modificado)));

    public Emisor? Emisor(string rut) => Uno("SELECT * FROM emisores WHERE rut = $rut", LeerEmisor, ("$rut", rut));

    public IReadOnlyList<Emisor> Emisores() => Lista("SELECT * FROM emisores ORDER BY rut", LeerEmisor);

    public void BorrarEmisor(string rut) => Ejecutar("DELETE FROM emisores WHERE rut = $rut", ("$rut", rut));

    private static Emisor LeerEmisor(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), Texto(r, 2), Texto(r, 3), Texto(r, 4), LeerFecha(r, 5)!.Value, LeerFecha(r, 6), LeerFecha(r, 7),
        r.GetInt64(8) == 1, LeerMomento(r, 9)!.Value);

    public void GuardarCertificado(string huella, string? rut, byte[] der, bool raiz, byte[]? pfx = null) => Ejecutar(
        "INSERT OR REPLACE INTO certificados (huella, rut, der, raiz, pfx, creado) VALUES ($h, $r, $d, $raiz, $p, $c)",
        ("$h", huella), ("$r", rut), ("$d", der), ("$raiz", raiz ? 1 : 0), ("$p", pfx), ("$c", Momento(DateTimeOffset.UtcNow)));

    public bool CertificadoRegistrado(string huella) =>
        Uno("SELECT 1 FROM certificados WHERE huella = $h AND raiz = 0", _ => true, ("$h", huella));

    public IReadOnlyList<byte[]> Raices() => Lista("SELECT der FROM certificados WHERE raiz = 1", r => (byte[])r[0]);

    /// <summary>El último certificado con clave que el simulador emitió para el RUT.</summary>
    public byte[]? PfxDe(string rut) =>
        Uno("SELECT pfx FROM certificados WHERE rut = $r AND pfx IS NOT NULL ORDER BY creado DESC LIMIT 1", r => (byte[])r[0], ("$r", rut));

    public IReadOnlyList<(string Huella, string? Rut, bool Raiz, bool ConClave)> Certificados() => Lista(
        "SELECT huella, rut, raiz, pfx IS NOT NULL FROM certificados ORDER BY creado",
        r => (r.GetString(0), Texto(r, 1), r.GetInt64(2) == 1, r.GetInt64(3) == 1));

    // --- CAE ----------------------------------------------------------------------------

    public void GuardarCae(Cae c) => Ejecutar("""
        INSERT OR REPLACE INTO cae (id, rut, tipo, serie, desde, hasta, autorizacion, vencimiento, especial, causal)
        VALUES ($id, $rut, $t, $s, $d, $h, $a, $v, $e, $c)
        """, ("$id", c.Id), ("$rut", c.Rut), ("$t", c.Tipo), ("$s", c.Serie), ("$d", c.Desde), ("$h", c.Hasta), ("$a", Fecha(c.Autorizacion)),
        ("$v", Fecha(c.Vencimiento)), ("$e", c.Especial), ("$c", c.Causal));

    public Cae? Cae(long id) => Uno("SELECT * FROM cae WHERE id = $id", LeerCae, ("$id", id));

    public IReadOnlyList<Cae> Caes(string? rut = null) => rut is null
        ? Lista("SELECT * FROM cae ORDER BY rut, tipo, serie, desde", LeerCae)
        : Lista("SELECT * FROM cae WHERE rut = $rut ORDER BY tipo, serie, desde", LeerCae, ("$rut", rut));

    private static Cae LeerCae(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.GetString(3), r.GetInt64(4), r.GetInt64(5), LeerFecha(r, 6)!.Value,
        LeerFecha(r, 7)!.Value, r.IsDBNull(8) ? null : r.GetInt32(8), r.IsDBNull(9) ? null : r.GetInt32(9));

    // --- Sobres y CFE -------------------------------------------------------------------

    public void GuardarSobre(SobreRecibido s) => Ejecutar("""
        INSERT INTO sobres (id_receptor, rut, id_emisor, estado, motivos, token, consultar_desde, cantidad, recibido, xml, acuse)
        VALUES ($id, $rut, $ie, $e, $m, $t, $cd, $c, $r, $x, $a)
        """, ("$id", s.IdReceptor), ("$rut", s.Rut), ("$ie", s.IdEmisor), ("$e", s.Estado), ("$m", Json(s.Motivos)), ("$t", s.Token),
        ("$cd", Momento(s.ConsultarDesde)), ("$c", s.Cantidad), ("$r", Momento(s.Recibido)), ("$x", s.Xml), ("$a", s.Acuse));

    public SobreRecibido? Sobre(long idReceptor) => Uno("SELECT * FROM sobres WHERE id_receptor = $id", LeerSobre, ("$id", idReceptor));

    /// <summary>Si ya se recibió (AS) un sobre del RUT con ese número de envío (S08).</summary>
    public bool ExisteSobre(string rut, long idEmisor) =>
        Uno("SELECT 1 FROM sobres WHERE rut = $rut AND id_emisor = $ie AND estado = 'AS'", _ => true, ("$rut", rut), ("$ie", idEmisor));

    public IReadOnlyList<SobreRecibido> Sobres(string? rut = null, int limite = 200) => rut is null
        ? Lista("SELECT * FROM sobres ORDER BY id_receptor DESC LIMIT $l", LeerSobre, ("$l", limite))
        : Lista("SELECT * FROM sobres WHERE rut = $rut ORDER BY id_receptor DESC LIMIT $l", LeerSobre, ("$rut", rut), ("$l", limite));

    private static SobreRecibido LeerSobre(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetString(3), DeJson<List<Motivo>>(r.GetString(4)), Texto(r, 5), LeerMomento(r, 6),
        r.GetInt32(7), LeerMomento(r, 8)!.Value, r.GetString(9), r.GetString(10));

    public void GuardarCfe(CfeRegistrado c) => Ejecutar("""
        INSERT INTO cfes (id_receptor, ordinal, rut, tipo, serie, nro, fecha, firma, estado, motivos, advertencias, moneda, tipo_cambio,
                          neto, iva, total, retenido, rut_receptor, recibido, xml)
        VALUES ($ir, $o, $rut, $t, $s, $n, $f, $fi, $e, $m, $a, $mo, $tc, $ne, $iva, $to, $re, $rr, $r, $x)
        """, ("$ir", c.IdReceptor), ("$o", c.Ordinal), ("$rut", c.Rut), ("$t", c.Tipo), ("$s", c.Serie), ("$n", c.Nro), ("$f", Fecha(c.Fecha)),
        ("$fi", Momento(c.Firma)), ("$e", c.Estado), ("$m", Json(c.Motivos)), ("$a", Json(c.Advertencias)), ("$mo", c.Moneda),
        ("$tc", Monto(c.TipoCambio)), ("$ne", Monto(c.Neto)), ("$iva", Monto(c.Iva)), ("$to", Monto(c.Total)), ("$re", Monto(c.Retenido)),
        ("$rr", c.RutReceptor), ("$r", Momento(c.Recibido)), ("$x", c.Xml));

    /// <summary>Si la DGI ya tiene el CFE (cualquier estado): un reenvío es E02.</summary>
    public bool ExisteCfe(string rut, int tipo, string serie, long nro) =>
        Uno("SELECT 1 FROM cfes WHERE rut = $rut AND tipo = $t AND serie = $s AND nro = $n LIMIT 1", _ => true,
            ("$rut", rut), ("$t", tipo), ("$s", serie), ("$n", nro));

    /// <summary>El último registro del CFE, aceptado o no.</summary>
    public CfeRegistrado? Cfe(string rut, int tipo, string serie, long nro) => Uno(
        "SELECT * FROM cfes WHERE rut = $rut AND tipo = $t AND serie = $s AND nro = $n ORDER BY id DESC LIMIT 1", LeerCfe,
        ("$rut", rut), ("$t", tipo), ("$s", serie), ("$n", nro));

    /// <summary>El primer registro aceptado (AE o CE) del CFE, que es el que vale.</summary>
    public CfeRegistrado? CfeAceptado(string rut, int tipo, string serie, long nro) => Uno(
        "SELECT * FROM cfes WHERE rut = $rut AND tipo = $t AND serie = $s AND nro = $n AND estado IN ('AE', 'CE') ORDER BY id LIMIT 1", LeerCfe,
        ("$rut", rut), ("$t", tipo), ("$s", serie), ("$n", nro));

    public CfeRegistrado? CfePorId(long id) => Uno("SELECT * FROM cfes WHERE id = $id", LeerCfe, ("$id", id));

    public IReadOnlyList<CfeRegistrado> CfesDeSobre(long idReceptor) =>
        Lista("SELECT * FROM cfes WHERE id_receptor = $ir ORDER BY ordinal", LeerCfe, ("$ir", idReceptor));

    /// <summary>Los CFE aceptados de un emisor con fecha de comprobante <paramref name="fecha"/> (para el reporte diario).</summary>
    public IReadOnlyList<CfeRegistrado> CfesAceptadosDelDia(string rut, DateOnly fecha) => Lista(
        "SELECT * FROM cfes WHERE rut = $rut AND fecha = $f AND estado IN ('AE', 'CE') ORDER BY tipo, serie, nro", LeerCfe,
        ("$rut", rut), ("$f", Fecha(fecha)));

    /// <summary>CFE recibidos por la DGI cuyo receptor es <paramref name="rutReceptor"/>.</summary>
    public IReadOnlyList<CfeRegistrado> CfesParaReceptor(string rutReceptor) =>
        Lista("SELECT * FROM cfes WHERE rut_receptor = $rr ORDER BY id", LeerCfe, ("$rr", rutReceptor));

    /// <summary>Los sobres aceptados que contienen el CFE.</summary>
    public IReadOnlyList<SobreRecibido> SobresConCfe(string rut, int tipo, string serie, long nro) => Lista("""
        SELECT s.* FROM sobres s WHERE s.id_receptor IN
            (SELECT id_receptor FROM cfes WHERE rut = $rut AND tipo = $t AND serie = $s AND nro = $n) ORDER BY s.id_receptor
        """, LeerSobre, ("$rut", rut), ("$t", tipo), ("$s", serie), ("$n", nro));

    public IReadOnlyList<CfeRegistrado> Cfes(string? rut = null, int limite = 200) => rut is null
        ? Lista("SELECT * FROM cfes ORDER BY id DESC LIMIT $l", LeerCfe, ("$l", limite))
        : Lista("SELECT * FROM cfes WHERE rut = $rut ORDER BY id DESC LIMIT $l", LeerCfe, ("$rut", rut), ("$l", limite));

    private static CfeRegistrado LeerCfe(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1), r.GetInt32(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetInt32(4), Texto(r, 5),
        r.IsDBNull(6) ? null : r.GetInt64(6), LeerFecha(r, 7), LeerMomento(r, 8), r.GetString(9), DeJson<List<Motivo>>(r.GetString(10)),
        DeJson<List<string>>(r.GetString(11)), Texto(r, 12), LeerMonto(r, 13), LeerMonto(r, 14), LeerMonto(r, 15), LeerMonto(r, 16),
        LeerMonto(r, 17), Texto(r, 18), LeerMomento(r, 19)!.Value, r.GetString(20));

    // --- Anulados y reportes ------------------------------------------------------------

    public void GuardarAnulados(string rut, int tipo, string serie, long desde, long hasta, DateOnly fecha) => Ejecutar(
        "INSERT INTO anulados (rut, tipo, serie, desde, hasta, fecha) VALUES ($rut, $t, $s, $d, $h, $f)",
        ("$rut", rut), ("$t", tipo), ("$s", serie), ("$d", desde), ("$h", hasta), ("$f", Fecha(fecha)));

    /// <summary>Si el número se informó como anulado en un reporte diario aceptado (E01).</summary>
    public bool EstaAnulado(string rut, int tipo, string serie, long nro) => Uno(
        "SELECT 1 FROM anulados WHERE rut = $rut AND tipo = $t AND serie = $s AND $n BETWEEN desde AND hasta LIMIT 1", _ => true,
        ("$rut", rut), ("$t", tipo), ("$s", serie), ("$n", nro));

    public void GuardarReporte(ReporteRecibido r) => Ejecutar("""
        INSERT INTO reportes (id_receptor, rut, id_emisor, fecha_resumen, secuencia, estado, motivos, advertencias, recibido, xml, acuse)
        VALUES ($id, $rut, $ie, $f, $s, $e, $m, $a, $r, $x, $ac)
        """, ("$id", r.IdReceptor), ("$rut", r.Rut), ("$ie", r.IdEmisor), ("$f", Fecha(r.FechaResumen)), ("$s", r.Secuencia), ("$e", r.Estado),
        ("$m", Json(r.Motivos)), ("$a", Json(r.Advertencias)), ("$r", Momento(r.Recibido)), ("$x", r.Xml), ("$ac", r.Acuse));

    public ReporteRecibido? Reporte(long idReceptor) => Uno("SELECT * FROM reportes WHERE id_receptor = $id", LeerReporte, ("$id", idReceptor));

    public IReadOnlyList<ReporteRecibido> Reportes(string? rut = null, DateOnly? fecha = null, int limite = 200) => Lista("""
        SELECT * FROM reportes WHERE ($rut IS NULL OR rut = $rut) AND ($f IS NULL OR fecha_resumen = $f) ORDER BY id_receptor DESC LIMIT $l
        """, LeerReporte, ("$rut", rut), ("$f", Fecha(fecha)), ("$l", limite));

    /// <summary>La mayor secuencia (<c>SecEnvio</c>) aceptada para el RUT y la fecha, o 0.</summary>
    public int UltimaSecuencia(string rut, DateOnly fecha) => Uno(
        "SELECT COALESCE(MAX(secuencia), 0) FROM reportes WHERE rut = $rut AND fecha_resumen = $f AND estado = 'AR'", r => r.GetInt32(0),
        ("$rut", rut), ("$f", Fecha(fecha)));

    private static ReporteRecibido LeerReporte(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetInt64(2), LeerFecha(r, 3), r.IsDBNull(4) ? null : r.GetInt32(4), r.GetString(5),
        DeJson<List<Motivo>>(r.GetString(6)), DeJson<List<string>>(r.GetString(7)), LeerMomento(r, 8)!.Value, r.GetString(9), r.GetString(10));

    // --- Fallas -------------------------------------------------------------------------

    public long GuardarFalla(Falla f) => Uno(
        "INSERT INTO fallas (operacion, tipo, codigo, detalle, restantes, segundos) VALUES ($o, $t, $c, $d, $r, $s) RETURNING id",
        r => r.GetInt64(0), ("$o", f.Operacion), ("$t", f.Tipo), ("$c", f.Codigo), ("$d", f.Detalle), ("$r", f.Restantes), ("$s", f.Segundos));

    /// <summary>Toma la falla más antigua programada para la operación (o para todas) y descuenta una vez.</summary>
    public Falla? TomarFalla(string operacion, IReadOnlyCollection<string> tipos)
    {
        using var c = Abrir();
        using var t = c.BeginTransaction();
        var lista = string.Join(", ", tipos.Select((_, i) => "$t" + i));
        var parametros = new List<(string, object?)> { ("$o", operacion) };
        parametros.AddRange(tipos.Select((tipo, i) => ("$t" + i, (object?)tipo)));
        var cmd = Comando(c, $"SELECT * FROM fallas WHERE (operacion = $o OR operacion = '*') AND restantes > 0 AND tipo IN ({lista}) ORDER BY id LIMIT 1",
            [.. parametros]);
        cmd.Transaction = t;
        Falla? falla = null;
        using (var r = cmd.ExecuteReader())
        {
            if (r.Read())
                falla = LeerFalla(r);
        }
        if (falla is not null)
        {
            var baja = Comando(c, "UPDATE fallas SET restantes = restantes - 1 WHERE id = $id", ("$id", falla.Id));
            baja.Transaction = t;
            baja.ExecuteNonQuery();
        }
        t.Commit();
        return falla;
    }

    /// <summary>Descuenta una vez la falla indicada.</summary>
    public void TomarFallaPorId(long id) => Ejecutar("UPDATE fallas SET restantes = restantes - 1 WHERE id = $id AND restantes > 0", ("$id", id));

    public IReadOnlyList<Falla> Fallas() => Lista("SELECT * FROM fallas WHERE restantes > 0 ORDER BY id", LeerFalla);

    public void BorrarFallas() => Ejecutar("DELETE FROM fallas");

    private static Falla LeerFalla(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetString(2), Texto(r, 3), Texto(r, 4), r.GetInt32(5), r.IsDBNull(6) ? null : r.GetInt32(6));

    // --- Intercambio y homologación -----------------------------------------------------

    public long GuardarIntercambio(MensajeIntercambio m) => Uno("""
        INSERT INTO intercambio (direccion, canal, tipo, rut_emisor, rut_receptor, id_emisor, nombre, xml, respuesta, estado, fecha)
        VALUES ($d, $c, $t, $re, $rr, $ie, $n, $x, $r, $e, $f) RETURNING id
        """, r => r.GetInt64(0), ("$d", m.Direccion), ("$c", m.Canal), ("$t", m.Tipo), ("$re", m.RutEmisor), ("$rr", m.RutReceptor),
        ("$ie", m.IdEmisor), ("$n", m.Nombre), ("$x", m.Xml), ("$r", m.Respuesta), ("$e", m.Estado), ("$f", Momento(m.Fecha)));

    public void ActualizarIntercambio(long id, string estado, string? respuesta) => Ejecutar(
        "UPDATE intercambio SET estado = $e, respuesta = $r WHERE id = $id", ("$id", id), ("$e", estado), ("$r", respuesta));

    public MensajeIntercambio? Intercambio(long id) => Uno("SELECT * FROM intercambio WHERE id = $id", LeerIntercambio, ("$id", id));

    public IReadOnlyList<MensajeIntercambio> Intercambios(string? rut = null, int limite = 200) => Lista("""
        SELECT * FROM intercambio WHERE $rut IS NULL OR rut_emisor = $rut OR rut_receptor = $rut ORDER BY id DESC LIMIT $l
        """, LeerIntercambio, ("$rut", rut), ("$l", limite));

    /// <summary>Si el receptor ya recibió ese sobre del emisor (S20).</summary>
    public bool ExisteSobreIntercambio(string rutEmisor, string rutReceptor, long idEmisor) => Uno("""
        SELECT 1 FROM intercambio WHERE direccion = 'recibido' AND tipo = 'SOBRE' AND rut_emisor = $re AND rut_receptor = $rr
            AND id_emisor = $ie AND estado = 'AS' LIMIT 1
        """, _ => true, ("$re", rutEmisor), ("$rr", rutReceptor), ("$ie", idEmisor));

    private static MensajeIntercambio LeerIntercambio(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetInt64(6),
        Texto(r, 7), r.GetString(8), Texto(r, 9), r.GetString(10), LeerMomento(r, 11)!.Value);

    public void MarcarCasoManual(string rut, string caso, bool cumplido)
    {
        if (cumplido)
            Ejecutar("INSERT OR REPLACE INTO homologacion_manual (rut, caso, fecha) VALUES ($r, $c, $f)",
                ("$r", rut), ("$c", caso), ("$f", Momento(DateTimeOffset.UtcNow)));
        else
            Ejecutar("DELETE FROM homologacion_manual WHERE rut = $r AND caso = $c", ("$r", rut), ("$c", caso));
    }

    public IReadOnlySet<string> CasosManuales(string rut) =>
        Lista("SELECT caso FROM homologacion_manual WHERE rut = $r", r => r.GetString(0), ("$r", rut)).ToHashSet();

    public IReadOnlyList<ReporteRecibido> ReportesAceptados(string rut) =>
        Lista("SELECT * FROM reportes WHERE rut = $rut AND estado = 'AR' ORDER BY id_receptor", LeerReporte, ("$rut", rut));

    public IReadOnlyList<CfeRegistrado> CfesAceptados(string rut) =>
        Lista("SELECT * FROM cfes WHERE rut = $rut AND estado IN ('AE', 'CE') ORDER BY id", LeerCfe, ("$rut", rut));

    // --- Acceso ---------------------------------------------------------------------------

    private SqliteConnection Abrir()
    {
        var c = new SqliteConnection(_conexion);
        c.Open();
        return c;
    }

    private static SqliteCommand Comando(SqliteConnection c, string sql, params (string Nombre, object? Valor)[] parametros)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (nombre, valor) in parametros)
            cmd.Parameters.AddWithValue(nombre, valor ?? DBNull.Value);
        return cmd;
    }

    private void Ejecutar(string sql, params (string, object?)[] parametros)
    {
        using var c = Abrir();
        Comando(c, sql, parametros).ExecuteNonQuery();
    }

    private T? Uno<T>(string sql, Func<SqliteDataReader, T> leer, params (string, object?)[] parametros)
    {
        using var c = Abrir();
        using var r = Comando(c, sql, parametros).ExecuteReader();
        return r.Read() ? leer(r) : default;
    }

    private List<T> Lista<T>(string sql, Func<SqliteDataReader, T> leer, params (string, object?)[] parametros)
    {
        using var c = Abrir();
        using var r = Comando(c, sql, parametros).ExecuteReader();
        var lista = new List<T>();
        while (r.Read())
            lista.Add(leer(r));
        return lista;
    }

    private static readonly JsonSerializerOptions OpcionesJson = new(JsonSerializerDefaults.Web);

    private static string Json<T>(T valor) => JsonSerializer.Serialize(valor, OpcionesJson);

    private static T DeJson<T>(string json) => JsonSerializer.Deserialize<T>(json, OpcionesJson)!;

    private static string? Texto(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static string? Fecha(DateOnly? f) => f?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateOnly? LeerFecha(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : DateOnly.ParseExact(r.GetString(i), "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string? Momento(DateTimeOffset? m) => m?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset? LeerMomento(SqliteDataReader r, int i) =>
        r.IsDBNull(i) ? null : DateTimeOffset.Parse(r.GetString(i), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string? Monto(decimal? m) => m?.ToString(CultureInfo.InvariantCulture);

    private static decimal? LeerMonto(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : decimal.Parse(r.GetString(i), CultureInfo.InvariantCulture);
}
