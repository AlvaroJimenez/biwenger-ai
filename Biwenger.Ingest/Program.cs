using Biwenger.Ingest;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

// =============================================================================
// PUNTO DE ENTRADA
//
//   dotnet run                 → captura de hoy
//   dotnet run -- 2026-08-13   → captura etiquetada con esa fecha
//
// Códigos de salida: 0 correcto, 1 fallo. Importa para el cron.
// =============================================================================

const int ExitOk = 0;
const int ExitFail = 1;

var config = new ConfigurationBuilder()
    .AddUserSecrets<Program>(optional: true)
    .AddEnvironmentVariables()
    .Build();

// -----------------------------------------------------------------------------
// MODO EXPLORACIÓN: vuelca payloads autenticados a samples/ y termina.
// No toca la base de datos. Temporal, hasta conocer la forma de las respuestas.
//     dotnet run -- dump-auth
// -----------------------------------------------------------------------------
if (args.Length > 0 && args[0] == "dump-auth")
{
    return await DumpAuthAsync(config);
}

if (args.Length > 0 && args[0] == "ingest-squad")
{
    return await IngestSquadAsync(config);
}

if (args.Length > 0 && args[0] == "ingest-market")
{
    return await IngestMarketAsync(config);
}
var asOf = ParseAsOf(args);
Log($"Ingesta Biwenger — as_of = {asOf:yyyy-MM-dd}");

var target = Environment.GetEnvironmentVariable("BIWENGER_TARGET") ?? "Biwenger";
var connectionString = config.GetConnectionString(target);
if (string.IsNullOrWhiteSpace(connectionString))
{
    Log($"ERROR: falta la cadena de conexión 'ConnectionStrings:{target}'.");
    Log($"       dotnet user-secrets set \"ConnectionStrings:{target}\" \"Host=...\"");
    return ExitFail;
}
Log($"Destino: {target}");

var repo = new SnapshotRepository(connectionString);

long runId;
try
{
    runId = await repo.StartRunAsync(asOf);
}
catch (Exception ex)
{
    // Si ni siquiera se puede abrir la conexión, no hay dónde registrar el fallo.
    Log($"ERROR: no se pudo conectar a Postgres. {ex.Message}");
    return ExitFail;
}

var seen = 0;
var written = 0;
var skipped = 0;

try
{
    using var http = new HttpClient();
    var client = new BiwengerClient(http);

    Log($"Descargando {client.BuildUrl()}");
    var fetch = await client.FetchAsync();
    seen = fetch.Parsed.Data!.Players?.Count ?? 0;

    Log($"Payload recibido: {fetch.Json.Length:N0} caracteres, " +
        $"{seen:N0} jugadores, sha256 {fetch.Sha256[..12]}…");

    if (fetch.SourceUpdatedAt is { } updated)
    {
        var age = DateTimeOffset.UtcNow - updated;
        Log($"Biwenger recalculó hace {age.TotalHours:F1} h ({updated:yyyy-MM-dd HH:mm} UTC)");
    }

    var map = BiwengerMapper.Map(fetch.Parsed.Data!, asOf);
    written = map.Snapshots.Count;
    skipped = map.Skipped;

    if (map.Warnings.Count > 0)
    {
        Log($"{map.Warnings.Count} aviso(s) durante el mapeo:");
        foreach (var w in map.Warnings.Take(10)) Log($"  · {w}");
        if (map.Warnings.Count > 10) Log($"  · … y {map.Warnings.Count - 10} más.");
    }

    var rawId = await repo.SaveAsync(fetch, map, asOf);

    var total = await repo.CountSnapshotsAsync(asOf);
    Log($"OK. raw_payload id={rawId}, {map.Teams.Count} equipos, " +
        $"{written:N0} jugadores. Filas en player_daily para {asOf:yyyy-MM-dd}: {total:N0}");

    await repo.FinishRunAsync(runId, seen, written, skipped, ok: true,
        notes: Summarize(map.Warnings));

    return ExitOk;
}
catch (Exception ex)
{
    Log($"ERROR: {ex.Message}");
    if (ex.InnerException is not null) Log($"       ↳ {ex.InnerException.Message}");

    await SafeFinishAsync(repo, runId, seen, written, skipped, ex);
    return ExitFail;
}

// -----------------------------------------------------------------------------

static DateOnly ParseAsOf(string[] args)
{
    if (args.Length == 0) return DateOnly.FromDateTime(DateTime.UtcNow);

    if (DateOnly.TryParse(args[0], out var parsed)) return parsed;

    Log($"Fecha '{args[0]}' no reconocida, se usa hoy. Formato esperado: yyyy-MM-dd");
    return DateOnly.FromDateTime(DateTime.UtcNow);
}

static string? Summarize(IReadOnlyList<string> warnings)
{
    if (warnings.Count == 0) return null;
    var text = string.Join(" | ", warnings.Take(20));
    return text.Length > 4000 ? text[..4000] + "…" : text;
}

static async Task SafeFinishAsync(
    SnapshotRepository repo, long runId, int seen, int written, int skipped, Exception ex)
{
    try
    {
        await repo.FinishRunAsync(runId, seen, written, skipped, ok: false,
            notes: ex.Message.Length > 4000 ? ex.Message[..4000] : ex.Message);
    }
    catch (Exception inner)
    {
        Log($"       (además falló el registro del error: {inner.Message})");
    }
}

static void Log(string message) =>
    Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] {message}");

static async Task<int> DumpAuthAsync(IConfiguration config)
{

    var options = BuildAuthOptions(config);
    if (options is null) return ExitFail;

    using var http = new HttpClient();
    using var auth = new BiwengerAuthClient(http, options);

    Directory.CreateDirectory("samples");
    var ok = true;

    // Cada volcado por separado: si uno falla, el otro sigue.
    // Así se distingue "el login no va" de "la ruta que inventé no existe".
    foreach (var (name, fetch) in new (string, Func<Task<string>>)[]
    {
        ("account", () => auth.GetAccountRawAsync()),
        ("squad",   () => auth.GetSquadRawAsync()),
        ("market",  () => auth.GetMarketRawAsync())

    })
    {
        try
        {
            var json = await fetch();
            var path = Path.Combine("samples", $"{name}.json");
            await File.WriteAllTextAsync(path, json);
            Log($"OK  {name,-8} → {path} ({json.Length:N0} caracteres)");
        }
        catch (Exception ex)
        {
            ok = false;
            Log($"FAIL {name,-8} → {ex.Message}");
        }
    }

    return ok ? ExitOk : ExitFail;
}

static async Task<int> IngestSquadAsync(IConfiguration config)
{
    var asOf = DateOnly.FromDateTime(DateTime.UtcNow);

    var target = Environment.GetEnvironmentVariable("BIWENGER_TARGET") ?? "Biwenger";
    var connectionString = config.GetConnectionString(target);
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        Log($"ERROR: falta 'ConnectionStrings:{target}'.");
        return ExitFail;
    }
    Log($"Ingesta de plantilla — as_of = {asOf:yyyy-MM-dd}, destino {target}");

    var options = BuildAuthOptions(config);
    if (options is null) return ExitFail;

    try
    {
        using var http = new HttpClient();
        using var auth = new BiwengerAuthClient(http, options);

        var response = await auth.GetSquadAsync();
        if (response.Data is null)
        {
            Log("ERROR: la respuesta no traía bloque 'data'.");
            return ExitFail;
        }

        var map = SquadMapper.Map(response.Data, options.LeagueId, asOf);

        foreach (var w in map.Warnings) Log($"  · {w}");

        var repo = new SquadRepository(connectionString);
        var squadValue = await repo.SaveAsync(map);

        var stored = await repo.CountSquadAsync(asOf);
        Log($"OK. {map.Players.Count} jugadores mapeados, {stored} filas en user_squad.");
        Log($"    Valor plantilla: {squadValue:N0} € · Saldo: {map.Account.Balance:N0} € " +
            $"· Total: {(squadValue ?? 0) + map.Account.Balance:N0} €");

        return ExitOk;
    }
    catch (Exception ex)
    {
        Log($"ERROR: {ex.Message}");
        if (ex.InnerException is not null) Log($"       ↳ {ex.InnerException.Message}");
        return ExitFail;
    }
}

static BiwengerAuthOptions? BuildAuthOptions(IConfiguration config)
{
    var missing = new[] { "Email", "Password", "LeagueId", "UserId" }
        .Where(k => string.IsNullOrWhiteSpace(config[$"Biwenger:{k}"]))
        .ToList();

    if (missing.Count > 0)
    {
        Log($"ERROR: faltan secrets: {string.Join(", ", missing.Select(m => $"Biwenger:{m}"))}");
        return null;
    }

    return new BiwengerAuthOptions
    {
        Email = config["Biwenger:Email"]!,
        Password = config["Biwenger:Password"]!,
        LeagueId = long.Parse(config["Biwenger:LeagueId"]!),
        UserId = long.Parse(config["Biwenger:UserId"]!)
    };
}

static async Task<int> IngestMarketAsync(IConfiguration config)
{
    var asOf = DateOnly.FromDateTime(DateTime.UtcNow);

    var target = Environment.GetEnvironmentVariable("BIWENGER_TARGET") ?? "Biwenger";
    var connectionString = config.GetConnectionString(target);
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        Log($"ERROR: falta 'ConnectionStrings:{target}'.");
        return ExitFail;
    }
    Log($"Ingesta de mercado — as_of = {asOf:yyyy-MM-dd}, destino {target}");

    var options = BuildAuthOptions(config);
    if (options is null) return ExitFail;

    try
    {
        using var http = new HttpClient();
        using var auth = new BiwengerAuthClient(http, options);

        var json = await auth.GetMarketRawAsync();
        var response = JsonSerializer.Deserialize<MarketResponse>(json);

        if (response?.Data is null)
        {
            Log("ERROR: la respuesta no traía bloque 'data'.");
            return ExitFail;
        }

        var map = MarketMapper.Map(response.Data, asOf);
        foreach (var w in map.Warnings) Log($"  · {w}");

        var (sales, offers) = await new MarketRepository(connectionString).SaveAsync(map, asOf);

        var system = map.Sales.Count(s => s.IsSystem);
        var incoming = map.Offers.Count(o => o.ToUserId is not null);

        Log($"OK. {sales} ventas ({system} del sistema, {sales - system} de managers), {offers} ofertas.");
        Log($"    Ofertas entrantes: {incoming} · Salientes: {offers - incoming}" +
            (map.MaximumBid is { } mb ? $" · Puja máxima: {mb:N0} €" : ""));

        return ExitOk;
    }
    catch (Exception ex)
    {
        Log($"ERROR: {ex.Message}");
        if (ex.InnerException is not null) Log($"       ↳ {ex.InnerException.Message}");
        return ExitFail;
    }
}