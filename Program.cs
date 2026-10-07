using DatasheetAnalyzer.Blazor;
using DatasheetAnalyzer.Blazor.Services;
using DatasheetAnalyzer.Core.Options;
using DatasheetAnalyzer.Core.Services;
using DatasheetAnalyzer.Core.Util;
using DatasheetAnalyzer.Core.Config;
using System.Diagnostics;
using System.Runtime.InteropServices;

// =====================================================================
// WICHTIG fuer "Doppelklick auf .exe"-Szenario (PublishSingleFile=true):
// ContentRoot MUSS auf den Ordner der .exe zeigen, sonst wird
// appsettings.json + wwwroot/ nicht gefunden, wenn die .exe aus einem
// anderen Arbeitsverzeichnis (Verknuepfung, OneDrive, "Im Explorer suchen")
// gestartet wird.
//
// ACHTUNG: Bei PublishSingleFile=true entpackt sich die .exe in einen
// Temp-Ordner (%LOCALAPPDATA%\Temp\.net\...). AppContext.BaseDirectory
// zeigt dann auf DIESEN Temp-Ordner, nicht auf den .exe-Ordner!
// Deshalb verwenden wir Environment.ProcessPath, das immer die echte
// .exe-Datei liefert.
//
// ACHTUNG (Container/Linux): Wird die App via "dotnet App.dll" gestartet
// (Docker/Cloud), zeigt Environment.ProcessPath auf den dotnet-Muxer
// (z. B. /usr/share/dotnet/dotnet) und NICHT auf unsere App. Dann wuerde
// exeDirectory faelschlich auf /usr/share/dotnet zeigen und die App
// versuchte dort ein wwwroot/ anzulegen -> UnauthorizedAccessException.
// Deshalb: ProcessPath nur verwenden, wenn dessen Dateiname zur eigenen
// Assembly passt (echtes AppHost/.exe-Szenario). Sonst BaseDirectory (/app).
// =====================================================================
var assemblyName = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name;
var processPath = Environment.ProcessPath;
var processFileName = processPath is not null
    ? Path.GetFileNameWithoutExtension(processPath)
    : null;

var isRunViaAppHost = processFileName is not null
    && assemblyName is not null
    && string.Equals(processFileName, assemblyName, StringComparison.OrdinalIgnoreCase);

var exeDirectory = (isRunViaAppHost ? Path.GetDirectoryName(processPath) : null)
                   ?? AppContext.BaseDirectory;
var webRoot = Path.Combine(exeDirectory, "wwwroot");

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = exeDirectory,
    WebRootPath = webRoot
});

// Sicherstellen, dass appsettings*.json vom .exe-Ordner geladen werden
// (unabhaengig vom aktuellen Working-Directory beim Start).
builder.Configuration.SetBasePath(exeDirectory);
builder.Configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);
builder.Configuration.AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables();

// =====================================================================
// CONFIG / SECRETS
// ---------------------------------------------------------------------
// Alle Secrets kommen ausschliesslich aus:
//   1. Umgebungsvariablen (Container/Cloud)
//   2. User-Secrets (lokale Entwicklung, appsettings.Development.json)
//   3. appsettings.json / appsettings.yaml (keine Klartext-Secrets committen)
//
// Es gibt bewusst KEINE eingebetteten Fallback-Credentials mehr, damit
// das Container-Image ohne Secrets ausgeliefert werden kann.
// =====================================================================

var envConnectionString = Environment.GetEnvironmentVariable("AzureStorage__ConnectionString");
var envContainerName    = Environment.GetEnvironmentVariable("AzureStorage__ContainerName");

string? finalConnectionString = !string.IsNullOrWhiteSpace(envConnectionString)
    ? envConnectionString
    : builder.Configuration["AzureStorage:ConnectionString"];

string? finalContainerName = !string.IsNullOrWhiteSpace(envContainerName)
    ? envContainerName
    : builder.Configuration["AzureStorage:ContainerName"];

if (!string.IsNullOrWhiteSpace(finalConnectionString))
    builder.Configuration["AzureStorage:ConnectionString"] = finalConnectionString;
if (!string.IsNullOrWhiteSpace(finalContainerName))
    builder.Configuration["AzureStorage:ContainerName"]    = finalContainerName;

// Diagnose: AccountName aus ConnectionString extrahieren, damit man im
// Konsolenfenster sofort sieht, gegen welches Backend tatsaechlich gearbeitet wird.
string accountName = "<unbekannt>";
if (!string.IsNullOrWhiteSpace(finalConnectionString))
{
    var accountMatch = System.Text.RegularExpressions.Regex.Match(
        finalConnectionString, @"AccountName=([^;]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    if (accountMatch.Success) accountName = accountMatch.Groups[1].Value;
}

Console.ForegroundColor = ConsoleColor.DarkGray;
Console.WriteLine($"[Config] Azure Backend: account='{accountName}', container='{finalContainerName ?? "<unset>"}', "
                + $"source={(string.IsNullOrWhiteSpace(envConnectionString) ? "CONFIG" : "ENV")}");
if (string.IsNullOrWhiteSpace(finalConnectionString))
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("[Config] WARN: Kein AzureStorage:ConnectionString gesetzt. Env-Var AzureStorage__ConnectionString oder appsettings.Development.json verwenden.");
}
Console.ResetColor();

// Wenn die App als eigenstaendige .exe gestartet wird (kein ASPNETCORE_URLS gesetzt
// und nicht im Development-Modus), an einen festen HTTP-Port binden. So muss der
// Endbenutzer kein HTTPS-Dev-Zertifikat installieren und die App ist sofort
// unter http://localhost:5000 erreichbar.
const string StandaloneUrl = "http://localhost:5000";
var hasExplicitUrls = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS"))
                      || args.Any(a => a.StartsWith("--urls", StringComparison.OrdinalIgnoreCase));
var isStandalone = !builder.Environment.IsDevelopment() && !hasExplicitUrls;
if (isStandalone)
{
    builder.WebHost.UseUrls(StandaloneUrl);
}

builder.Services.Configure<AzureStorageOptions>(
    builder.Configuration.GetSection(AzureStorageOptions.SectionName));

builder.Services.Configure<DatasheetAnalyzer.Core.Config.DmsOracleOptions>(
    builder.Configuration.GetSection(DatasheetAnalyzer.Core.Config.DmsOracleOptions.SectionName));
builder.Services.Configure<DatasheetAnalyzer.Core.Config.DmsApiOptions>(
    builder.Configuration.GetSection(DatasheetAnalyzer.Core.Config.DmsApiOptions.SectionName));

// Umschaltung DMS-Backend: Default ist die neue REST-API (Interface auf
// https://dms-api-prod.cloud.rsint.net). Ueber die Konfiguration
// "DmsIntegration:Backend" = "Oracle" laesst sich der alte direkte
// Oracle-Weg reaktivieren, falls die API mal ausfaellt.
string dmsBackend = builder.Configuration["DmsIntegration:Backend"] ?? "Api";
if (string.Equals(dmsBackend, "Oracle", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<DatasheetAnalyzer.Core.Services.IDmsLookupService,
                                  DatasheetAnalyzer.Core.Services.DmsLookupService>();
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine("[Config] DMS-Backend: ORACLE (direkte DB-Anbindung)");
    Console.ResetColor();
}
else
{
    builder.Services.AddSingleton<DatasheetAnalyzer.Core.Services.IDmsLookupService,
                                  DatasheetAnalyzer.Core.Services.DmsApiLookupService>();
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine("[Config] DMS-Backend: REST-API ({0})",
        builder.Configuration["DmsApi:BaseUrl"]);
    Console.ResetColor();
}

builder.Services
    .AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddServerSideBlazor()
    .AddCircuitOptions(options =>
    {
        options.DetailedErrors = builder.Environment.IsDevelopment();
    });
    
builder.Services.AddHealthChecks();

// State Service - als Singleton, damit er über alle Circuits hinweg erhalten bleibt
builder.Services.AddScoped<ICurrentRecordService, CurrentRecordService>();
builder.Services.AddSingleton<IAnalyzeStateService, AnalyzeStateService>();
builder.Services.AddScoped<ICompareStateService, CompareStateService>();

// HttpClient Factory für Azure OpenAI - mit System-Proxy-Unterstuetzung,
// damit die App auch hinter Unternehmens-Proxys funktioniert.
builder.Services.AddHttpClient(string.Empty)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        UseProxy = true,
        Proxy = System.Net.WebRequest.GetSystemWebProxy(),
        DefaultProxyCredentials = System.Net.CredentialCache.DefaultCredentials
    });

var connectionString = builder.Configuration["AzureStorage:ConnectionString"];
if (!string.IsNullOrWhiteSpace(connectionString))
{
    // SINGLETON: Diese Services laden Daten aus Azure Blob Storage (Katalog,
    // Property-Definitionen, Prompts). Sie sind read-only nach dem Laden und
    // koennen ueber alle Browser-Sessions/Circuits hinweg geteilt werden.
    // -> Katalog wird nur EINMAL beim ersten Zugriff geladen statt bei
    //    jedem Reconnect / jeder neuen Browser-Session.
    builder.Services.AddSingleton<IRemoteConfigService, RemoteConfigService>();
    builder.Services.AddSingleton<AzureConfig>();
    builder.Services.AddSingleton<DMSCatalogService>();
    builder.Services.AddSingleton<IDmsPropertyMappingService, DmsPropertyMappingService>();

    // SCOPED bleibt: DatasheetAnalysisService haengt pro Analyse einen
    // TokenStatsCallback-Event-Handler an. Als Singleton wuerden sich diese
    // Handler ueber alle User akkumulieren (Memory-Leak + falsche Token-Counts).
    builder.Services.AddScoped<DatasheetAnalysisService>();
    builder.Services.AddScoped<PartNumberDecoderService>();

    // Chatbot-Service fuer die Ergebnisseite: nutzt dieselbe Azure-Konfiguration
    // wie die Analyse. Kein State, keine Historie -> Singleton ist ausreichend.
    builder.Services.AddSingleton<IPdfChatService, PdfChatService>();

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine("[Config] Azure-Services registriert (Container: " +
        (builder.Configuration["AzureStorage:ContainerName"] ?? "<default>") + ").");
    Console.ResetColor();
}
else
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine("============================================================");
    Console.WriteLine(" FEHLER: Azure Storage ConnectionString ist nicht konfiguriert.");
    Console.WriteLine(" Die Analyse-Funktion ist deaktiviert.");
    Console.WriteLine("============================================================");
    Console.ResetColor();
}

// Core services
builder.Services.AddScoped<PDFProcessor>();
builder.Services.AddScoped<JsonParser>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    if (!isStandalone)
    {
        // HSTS nur sinnvoll, wenn die App tatsaechlich hinter HTTPS laeuft.
        app.UseHsts();
    }
}

// Einfacher /Error-Endpunkt, damit der Endbenutzer bei einem Startfehler
// keine nackte "HTTP ERROR 500"-Browserseite sieht, sondern eine
// verstaendliche Meldung inkl. Hinweis (Azure-Zugriff, Firewall, ...).
app.MapGet("/Error", (HttpContext ctx) =>
{
    var feature = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerPathFeature>();
    var msg = feature?.Error?.Message ?? "Unbekannter Fehler";
    ctx.Response.ContentType = "text/html; charset=utf-8";
    return ctx.Response.WriteAsync($$"""
        <!doctype html><html lang="de"><head><meta charset="utf-8">
        <title>DatasheetAnalyzer – Fehler</title>
        <style>body{font-family:Segoe UI,Arial,sans-serif;max-width:760px;margin:60px auto;padding:0 24px;color:#222}
        h1{color:#003c7e} code,pre{background:#f4f6f8;padding:2px 6px;border-radius:4px}
        pre{padding:12px;overflow:auto}</style></head><body>
        <h1>DatasheetAnalyzer konnte die Seite nicht laden</h1>
        <p>Das Programm laeuft, aber beim Aufbau der Seite ist ein Fehler aufgetreten.</p>
        <p><strong>Haeufigste Ursache:</strong> der PC hat keinen Zugriff auf das
        Azure-Backend (Firewall/Proxy/kein Internet) oder die Datei
        <code>appsettings.json</code> neben der .exe enthaelt keinen gueltigen
        <code>AzureStorage:ConnectionString</code>.</p>
        <p><strong>Was tun?</strong></p>
        <ol>
            <li>Pruefen, dass <code>appsettings.json</code> im selben Ordner wie
                die .exe liegt.</li>
            <li>Internet-/VPN-Verbindung pruefen (Zugriff auf
                <code>*.blob.core.windows.net</code>).</li>
            <li>Konsolenfenster der App ansehen – dort steht die Original-Meldung.</li>
        </ol>
        <p><strong>Technische Details:</strong></p>
        <pre>{{System.Net.WebUtility.HtmlEncode(msg)}}</pre>
        </body></html>
        """);
});

// Im Standalone-Modus (lokale .exe) verzichten wir bewusst auf HTTPS-Redirect,
// da auf dem Empfaenger-PC kein vertrauenswuerdiges Dev-Zertifikat vorhanden ist.
if (!isStandalone)
{
    app.UseHttpsRedirection();
}

app.UseStaticFiles();
app.UseRouting();
app.MapHealthChecks("/health");

app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

if (isStandalone)
{
    // Browser automatisch oeffnen, sobald der Server bereit ist.
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("============================================================");
        Console.WriteLine($" DatasheetAnalyzer ist bereit unter: {StandaloneUrl}");
        Console.WriteLine(" Dieses Konsolenfenster bitte geoeffnet lassen.");
        Console.WriteLine(" Zum Beenden: Fenster schliessen oder Strg+C druecken.");
        Console.WriteLine("============================================================");
        Console.ResetColor();

        // WARMUP: Katalog (67 Komponenten + 404 Property-Gruppen) im Hintergrund
        // schon JETZT aus Azure laden, statt erst beim ersten User-Request.
        // Da DMSCatalogService Singleton ist, profitieren danach alle Sessions.
        _ = Task.Run(() =>
        {
            try
            {
                using var scope = app.Services.CreateScope();
                var catalog = scope.ServiceProvider.GetService<DMSCatalogService>();
                if (catalog != null)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    catalog.LoadCatalogData();
                    sw.Stop();
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine($"[Warmup] Katalog vorgeladen ({sw.ElapsedMilliseconds} ms) - bereit fuer Analysen.");
                    Console.ResetColor();
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[Warmup] Katalog-Vorladen fehlgeschlagen: {ex.Message}");
                Console.WriteLine("         (Wird beim ersten Request automatisch nachgeholt.)");
                Console.ResetColor();
            }
        });

        // Diagnose: Azure-Erreichbarkeit pruefen, damit der Endnutzer im
        // Konsolenfenster sofort sieht, ob die Analyse funktionieren wird.
        _ = Task.Run(async () =>
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                var resp = await http.GetAsync("https://storage3csd2rus.blob.core.windows.net/?comp=list");
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"[Diag] Azure Blob Storage erreichbar (HTTP {(int)resp.StatusCode}).");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("[Diag] Azure Blob Storage NICHT erreichbar:");
                Console.WriteLine($"       {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine("       -> Pruefen Sie VPN/Proxy/Firewall.");
                Console.ResetColor();
            }
        });

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = StandaloneUrl,
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Browser konnte nicht automatisch geoeffnet werden: {ex.Message}");
            Console.WriteLine($"Bitte oeffnen Sie manuell: {StandaloneUrl}");
        }
    });
}

app.Run();

