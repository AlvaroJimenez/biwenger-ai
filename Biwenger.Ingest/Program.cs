using Biwenger.Ingest;
using Microsoft.Extensions.Configuration;

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

var asOf = ParseAsOf(args);
Log($"Ingesta Biwenger — as_of = {asOf:yyyy-MM-dd}");

var config = new ConfigurationBuilder()
    .AddUserSecrets<Program>(optional: true)
    .AddEnvironmentVariables()
    .Build();

// La primera variable de entorno o argumento decide el destino.
// Por defecto, local: ejecutar contra Azure debe ser una decisión explícita.
var target = Environment.GetEnvironmentVariable("BIWENGER_TARGET") ?? "Biwenger";
var connectionString = config.GetConnectionString(target);
if (string.IsNullOrWhiteSpace(connectionString))
{
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