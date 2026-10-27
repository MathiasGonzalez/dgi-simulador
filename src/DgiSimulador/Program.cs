using DgiSimulador;
using DgiSimulador.Admin;
using DgiSimulador.Certificados;
using DgiSimulador.Consultas;
using DgiSimulador.Estado;
using DgiSimulador.Homologacion;
using DgiSimulador.Intercambio;
using DgiSimulador.Recepcion;
using DgiSimulador.Soap;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<OpcionesSimulador>(builder.Configuration.GetSection("Simulador"));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<Almacen>();
builder.Services.AddSingleton<Autoridad>();
builder.Services.AddSingleton<Fallas>();
builder.Services.AddSingleton<ControlCfe>();
builder.Services.AddSingleton<RecepcionSobre>();
builder.Services.AddSingleton<RecepcionReporte>();
builder.Services.AddSingleton<ServicioConsultas>();
builder.Services.AddSingleton<ClienteIntercambio>();
builder.Services.AddSingleton<ReceptorSimulado>();
builder.Services.AddSingleton<EmisorSimulado>();
builder.Services.AddSingleton<SetDePruebas>();
builder.Services.AddSingleton<Administracion>();
builder.Services.AddHttpClient("intercambio", c => c.Timeout = TimeSpan.FromSeconds(30));
// Solo la API de administración: el contrato de los servicios SOAP es el WSDL oficial (?wsdl).
builder.Services.AddOpenApi(o =>
{
    o.ShouldInclude = d => d.RelativePath?.StartsWith("admin/", StringComparison.Ordinal) == true;
    o.AddDocumentTransformer((documento, _, _) =>
    {
        documento.Info.Title = "dgi-simulador: administración";
        documento.Info.Description = "Padrón, certificados y CAE de prueba, inspección de lo recibido, fallas programadas, intercambio y homologación.";
        return Task.CompletedTask;
    });
});
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.WriteIndented = true);
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 64 * 1024 * 1024);

var app = builder.Build();
app.Services.GetRequiredService<Administracion>().AplicarSemilla();
app.MapGet("/", () => Results.Redirect("/admin"));
ServiciosSoap.Mapear(app);
AdminApi.Mapear(app);
app.MapOpenApi();
app.Run();

/// <summary>Punto de entrada; parcial para las pruebas con <c>WebApplicationFactory</c>.</summary>
public partial class Program;
