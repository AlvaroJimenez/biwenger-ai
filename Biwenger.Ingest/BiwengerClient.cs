using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Biwenger.Ingest;

// =============================================================================
// RESULTADO DE UNA DESCARGA
// El JSON crudo viaja SIEMPRE junto al DTO parseado: se persiste antes de
// mapear, para que un error de mapeo sea un bug reparable y no datos perdidos.
// =============================================================================

public sealed record RawFetch(
    string Json,
    string Sha256,
    string SourceUrl,
    int ScoreMode,
    int HttpStatus,
    DateTimeOffset FetchedAt,
    DateTimeOffset? SourceUpdatedAt,
    CompetitionResponseDto Parsed);


// =============================================================================
// CLIENTE HTTP
// =============================================================================

public sealed class BiwengerClient
{
    private const string BaseUrl = "https://cf.biwenger.com/api/v2/competitions";

    /// <summary>Fijado a 1 (Diario AS). Cambiarlo rompe la comparabilidad del histórico.</summary>
    public const int ScoreMode = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private readonly HttpClient _http;
    private readonly string _competitionSlug;

    public BiwengerClient(HttpClient http, string competitionSlug = "la-liga")
    {
        _http = http;
        _competitionSlug = competitionSlug;

        // La API no está documentada y es la que consume su propia web.
        // Un User-Agent identificable es cortesía básica hacia el operador.
        if (!_http.DefaultRequestHeaders.Contains("User-Agent"))
        {
            _http.DefaultRequestHeaders.Add(
                "User-Agent", "biwenger-ai/0.1 (proyecto personal de aprendizaje)");
        }

        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public string BuildUrl() =>
        $"{BaseUrl}/{_competitionSlug}/data?score={ScoreMode}&lang=es";

    public async Task<RawFetch> FetchAsync(CancellationToken ct = default)
    {
        var url = BuildUrl();
        var fetchedAt = DateTimeOffset.UtcNow;

        using var response = await _http.GetAsync(url, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new BiwengerFetchException(
                $"Biwenger respondió {(int)response.StatusCode}. Cuerpo: {Truncate(json, 500)}");
        }

        // Se parsea DESPUÉS de tener el crudo en la mano.
        CompetitionResponseDto? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<CompetitionResponseDto>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new BiwengerFetchException(
                "El payload no se pudo deserializar: la forma del JSON ha cambiado. " +
                "Guarda el crudo y revisa Models.cs.", ex);
        }

        if (parsed?.Data is null)
        {
            throw new BiwengerFetchException("El payload no trae el nodo 'data'.");
        }

        if (parsed.Data.ScoreId != ScoreMode)
        {
            throw new BiwengerFetchException(
                $"scoreID esperado {ScoreMode} pero llegó {parsed.Data.ScoreId}. " +
                "Guardar esto rompería la comparabilidad del histórico.");
        }

        return new RawFetch(
            Json: json,
            Sha256: ComputeSha256(json),
            SourceUrl: url,
            ScoreMode: ScoreMode,
            HttpStatus: (int)response.StatusCode,
            FetchedAt: fetchedAt,
            SourceUpdatedAt: FromUnixSeconds(parsed.Data.Update),
            Parsed: parsed);
    }

    private static string ComputeSha256(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    internal static DateTimeOffset? FromUnixSeconds(long? seconds) =>
        seconds is null or 0 ? null : DateTimeOffset.FromUnixTimeSeconds(seconds.Value);

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}

public sealed class BiwengerFetchException : Exception
{
    public BiwengerFetchException(string message) : base(message) { }
    public BiwengerFetchException(string message, Exception inner) : base(message, inner) { }
}


// =============================================================================
// MAPPER — CAPA ANTICORRUPCIÓN
// Único punto del sistema que conoce la forma del JSON externo.
// Si Biwenger cambia el contrato, se toca este fichero y nada más.
// =============================================================================

public sealed record MapResult(
    IReadOnlyList<Team> Teams,
    IReadOnlyList<Player> Players,
    IReadOnlyList<PlayerSnapshot> Snapshots,
    IReadOnlyList<string> Warnings)
{
    public int Skipped => Warnings.Count;
}

public static class BiwengerMapper
{
    /// <summary>
    /// Canario: la liga entera no cabe en menos de 100 jugadores.
    /// Si baja de aquí, algo ha cambiado en el origen y no se debe escribir.
    /// </summary>
    public const int MinExpectedPlayers = 100;

    public static MapResult Map(CompetitionDataDto data, DateOnly asOf)
    {
        var warnings = new List<string>();

        var teams = MapTeams(data, warnings);
        var (players, snapshots) = MapPlayers(data, asOf, warnings);

        if (snapshots.Count < MinExpectedPlayers)
        {
            throw new BiwengerFetchException(
                $"Canario: solo {snapshots.Count} jugadores mapeados (mínimo esperado " +
                $"{MinExpectedPlayers}). No se escribe nada. Revisa el payload crudo.");
        }

        return new MapResult(teams, players, snapshots, warnings);
    }

    private static List<Team> MapTeams(CompetitionDataDto data, List<string> warnings)
    {
        var teams = new List<Team>();
        if (data.Teams is null) return teams;

        foreach (var (key, dto) in data.Teams)
        {
            if (dto is null || dto.Id == 0 || string.IsNullOrWhiteSpace(dto.Name))
            {
                warnings.Add($"Equipo '{key}' descartado: id o nombre ausente.");
                continue;
            }

            teams.Add(new Team(dto.Id, dto.Name.Trim(), dto.Slug));
        }

        return teams;
    }

    private static (List<Player>, List<PlayerSnapshot>) MapPlayers(
        CompetitionDataDto data, DateOnly asOf, List<string> warnings)
    {
        var players = new List<Player>();
        var snapshots = new List<PlayerSnapshot>();

        if (data.Players is null) return (players, snapshots);

        // Equipos conocidos: un teamID que no esté aquí violaría la FK.
        var knownTeams = data.Teams?.Values
            .Where(t => t is not null && t.Id != 0)
            .Select(t => t.Id)
            .ToHashSet() ?? new HashSet<int>();

        foreach (var (key, dto) in data.Players)
        {
            try
            {
                if (dto is null || dto.Id == 0 || string.IsNullOrWhiteSpace(dto.Name))
                {
                    warnings.Add($"Jugador '{key}' descartado: id o nombre ausente.");
                    continue;
                }

                // NULLABLE de verdad: hay jugadores sin equipo (traspasados, sin asignar).
                var teamId = dto.TeamId;
                if (teamId is not null && !knownTeams.Contains(teamId.Value))
                {
                    warnings.Add(
                        $"Jugador {dto.Id} ({dto.Name}) apunta al equipo {teamId} " +
                        "que no está en el payload. Se guarda sin equipo.");
                    teamId = null;
                }

                // 1..5 (5 = entrenador). Cualquier otro valor es desconocido:
                // se anula en lugar de tumbar la inserción. El crudo conserva la verdad.
                var position = dto.Position;
                if (position is not null && (position < 1 || position > 5))
                {
                    warnings.Add(
                        $"Jugador {dto.Id} ({dto.Name}) trae posición {position}, " +
                        "fuera del rango conocido 1-5. Se guarda como nula.");
                    position = null;
                }

                players.Add(new Player(
                    PlayerId: dto.Id,
                    TeamId: teamId,
                    Name: dto.Name.Trim(),
                    Slug: dto.Slug,
                    Position: position));

                snapshots.Add(new PlayerSnapshot(
                    PlayerId: dto.Id,
                    AsOf: asOf,
                    TeamId: teamId,
                    Position: position,
                    AltPositions: NormalizeAltPositions(dto.AltPositions),
                    MarketValue: dto.Price,
                    FantasyPrice: dto.FantasyPrice,
                    PriceIncrement: dto.PriceIncrement,
                    ShirtNumber: dto.Number,
                    Status: string.IsNullOrWhiteSpace(dto.Status) ? null : dto.Status.Trim(),
                    StatusInfo: string.IsNullOrWhiteSpace(dto.StatusInfo) ? null : dto.StatusInfo.Trim(),
                    StatusChangedAt: BiwengerClient.FromUnixSeconds(dto.StatusChange),
                    FitnessJson: SerializeFitness(dto.Fitness),
                    Points: dto.Points,
                    PointsHome: dto.PointsHome,
                    PointsAway: dto.PointsAway,
                    PlayedHome: dto.PlayedHome,
                    PlayedAway: dto.PlayedAway,
                    PointsLastSeason: dto.PointsLastSeason));
            }
            catch (Exception ex)
            {
                // Un jugador con forma inesperada se descarta y se registra.
                // No tumba la ingesta: perder una fila es peor que perder el día.
                warnings.Add($"Jugador '{key}' descartado por excepción: {ex.Message}");
            }
        }

        return (players, snapshots);
    }

    private static int[]? NormalizeAltPositions(int[]? alt) =>
        alt is null || alt.Length == 0
            ? null
            : alt.Where(p => p is >= 1 and <= 5).Distinct().ToArray() is { Length: > 0 } clean
                ? clean
                : null;

    /// <summary>
    /// fitness llega vacío a principio de temporada y su forma futura es incierta
    /// (puede traer números o nulls). Se guarda como JSON crudo y ya se decidirá.
    /// </summary>
    private static string? SerializeFitness(JsonElement? fitness)
    {
        if (fitness is null) return null;
        if (fitness.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;

        var raw = fitness.Value.GetRawText();
        return raw is "[]" or "{}" ? null : raw;
    }
}