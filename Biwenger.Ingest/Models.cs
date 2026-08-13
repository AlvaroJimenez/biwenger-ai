using System.Text.Json;
using System.Text.Json.Serialization;

namespace Biwenger.Ingest;

// =============================================================================
// DTOs — BORDE EXTERIOR
// Reflejan el JSON de Biwenger TAL CUAL. No se usan fuera del mapper.
// Verificados contra el payload real de cf.biwenger.com (score=1).
// Todo nullable a propósito: la API no tiene contrato ni compromiso de
// estabilidad, y un campo ausente no debe tumbar la ingesta.
// =============================================================================

public sealed class CompetitionResponseDto
{
    [JsonPropertyName("status")] public int Status { get; set; }
    [JsonPropertyName("data")]   public CompetitionDataDto? Data { get; set; }
}

public sealed class CompetitionDataDto
{
    [JsonPropertyName("id")]       public int Id { get; set; }
    [JsonPropertyName("name")]     public string? Name { get; set; }
    [JsonPropertyName("slug")]     public string? Slug { get; set; }
    [JsonPropertyName("currency")] public string? Currency { get; set; }

    /// <summary>Sistema de puntuación. Debe ser siempre 1 o el histórico deja de ser comparable.</summary>
    [JsonPropertyName("scoreID")]  public int ScoreId { get; set; }

    /// <summary>Epoch (segundos) del último recálculo de Biwenger. Sirve para detectar payload rancio.</summary>
    [JsonPropertyName("update")]   public long Update { get; set; }

    // OJO: DICCIONARIOS indexados por id, NO arrays. Deserializar como List<> falla.
    [JsonPropertyName("players")]  public Dictionary<string, PlayerDto>? Players { get; set; }
    [JsonPropertyName("teams")]    public Dictionary<string, TeamDto>? Teams { get; set; }
}

public sealed class TeamDto
{
    [JsonPropertyName("id")]   public int Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("slug")] public string? Slug { get; set; }
    // No hay shortName en este endpoint.
}

public sealed class PlayerDto
{
    [JsonPropertyName("id")]     public long Id { get; set; }
    [JsonPropertyName("name")]   public string? Name { get; set; }
    [JsonPropertyName("slug")]   public string? Slug { get; set; }

    /// <summary>NULLABLE: hay jugadores sin equipo (traspasados, sin asignar).</summary>
    [JsonPropertyName("teamID")] public int? TeamId { get; set; }

    /// <summary>1 POR · 2 DEF · 3 MED · 4 DEL · 5 ENTRENADOR. Los entrenadores vienen aquí mezclados.</summary>
    [JsonPropertyName("position")]     public int? Position { get; set; }
    [JsonPropertyName("altPositions")] public int[]? AltPositions { get; set; }

    /// <summary>Valor de mercado en euros. ESTA es la serie temporal que interesa.</summary>
    [JsonPropertyName("price")]          public long? Price { get; set; }

    /// <summary>Valoración distinta y mucho mayor. Se guarda, pero no es el precio de mercado.</summary>
    [JsonPropertyName("fantasyPrice")]   public long? FantasyPrice { get; set; }

    /// <summary>Delta diario de <see cref="Price"/>.</summary>
    [JsonPropertyName("priceIncrement")] public long? PriceIncrement { get; set; }

    [JsonPropertyName("number")] public int? Number { get; set; }

    /// <summary>ok · injured · doubt · sanctioned · discarded · unknown (lista abierta).</summary>
    [JsonPropertyName("status")]       public string? Status { get; set; }

    /// <summary>Texto libre del parte médico o sanción. Materia prima para la fase 3.</summary>
    [JsonPropertyName("statusInfo")]   public string? StatusInfo { get; set; }

    /// <summary>Epoch (segundos) del último cambio de estado.</summary>
    [JsonPropertyName("statusChange")] public long? StatusChange { get; set; }

    /// <summary>Últimas puntuaciones. Vacío a principio de temporada y puede contener nulls.</summary>
    [JsonPropertyName("fitness")] public JsonElement? Fitness { get; set; }

    [JsonPropertyName("points")]           public int? Points { get; set; }
    [JsonPropertyName("pointsHome")]       public int? PointsHome { get; set; }
    [JsonPropertyName("pointsAway")]       public int? PointsAway { get; set; }
    [JsonPropertyName("playedHome")]       public int? PlayedHome { get; set; }
    [JsonPropertyName("playedAway")]       public int? PlayedAway { get; set; }
    [JsonPropertyName("pointsLastSeason")] public int? PointsLastSeason { get; set; }
}


// =============================================================================
// DOMINIO — lo que circula por dentro de la aplicación.
// Nombres propios, tipos propios. Si Biwenger cambia el JSON, esto no se toca.
// =============================================================================

public sealed record Team(
    int     TeamId,
    string  Name,
    string? Slug);

public sealed record Player(
    long    PlayerId,
    int?    TeamId,
    string  Name,
    string? Slug,
    int?    Position);

public sealed record PlayerSnapshot(
    long            PlayerId,
    DateOnly        AsOf,
    int?            TeamId,
    int?            Position,
    int[]?          AltPositions,
    long?           MarketValue,
    long?           FantasyPrice,
    long?           PriceIncrement,
    int?            ShirtNumber,
    string?         Status,
    string?         StatusInfo,
    DateTimeOffset? StatusChangedAt,
    string?         FitnessJson,
    int?            Points,
    int?            PointsHome,
    int?            PointsAway,
    int?            PlayedHome,
    int?            PlayedAway,
    int?            PointsLastSeason);


// =============================================================================
// CONSTANTES DEL DOMINIO
// =============================================================================

public static class Positions
{
    public const int Goalkeeper = 1;
    public const int Defender   = 2;
    public const int Midfielder = 3;
    public const int Forward    = 4;
    public const int Coach      = 5;

    public static bool IsCoach(int? position) => position == Coach;

    public static string Abbr(int? position) => position switch
    {
        Goalkeeper => "PT",
        Defender   => "DF",
        Midfielder => "MC",
        Forward    => "DL",
        Coach      => "E",
        _          => "?"
    };
}

/// <summary>
/// Estados observados. Es una lista ABIERTA: Biwenger puede añadir valores sin
/// avisar, así que se guarda el string tal cual y esto solo sirve de referencia.
/// </summary>
public static class PlayerStatuses
{
    public const string Ok         = "ok";
    public const string Injured    = "injured";
    public const string Doubt      = "doubt";
    public const string Sanctioned = "sanctioned";
    public const string Discarded  = "discarded";
    public const string Unknown    = "unknown";

    /// <summary>Estados en los que el jugador no es alineable con confianza.</summary>
    public static bool IsUnavailable(string? status) =>
        status is Injured or Sanctioned or Discarded;
}
