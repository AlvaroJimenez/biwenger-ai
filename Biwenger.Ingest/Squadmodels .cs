using System.Text.Json.Serialization;

namespace Biwenger.Ingest;

// =============================================================================
// Modelos de la respuesta de /api/v2/user?fields=*,players(id,owner,price)
//
// Se modelan tal como llega el JSON; el aplanado ocurre en el mapper.
// =============================================================================

public sealed class UserResponse
{
    [JsonPropertyName("status")] public int Status { get; set; }
    [JsonPropertyName("data")] public UserData? Data { get; set; }
}

public sealed class UserData
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("points")] public int? Points { get; set; }

    /// <summary>Saldo disponible en euros.</summary>
    [JsonPropertyName("balance")] public long Balance { get; set; }

    [JsonPropertyName("players")] public List<UserPlayer>? Players { get; set; }
}

public sealed class UserPlayer
{
    [JsonPropertyName("id")] public long Id { get; set; }

    /// <summary>Ausente si el jugador no es del usuario; presente en la plantilla propia.</summary>
    [JsonPropertyName("owner")] public UserPlayerOwner? Owner { get; set; }
}

public sealed class UserPlayerOwner
{
    /// <summary>Epoch en segundos de cuándo pasó a ser del usuario.</summary>
    [JsonPropertyName("date")] public long? Date { get; set; }

    /// <summary>
    /// Precio pagado. AUSENTE en los jugadores del draft inicial: no se compraron.
    /// Por eso es nullable y no se sustituye por cero.
    /// </summary>
    [JsonPropertyName("price")] public long? Price { get; set; }

    /// <summary>Cláusula de rescisión.</summary>
    [JsonPropertyName("clause")] public long? Clause { get; set; }

    /// <summary>Epoch hasta el que el jugador no se puede vender.</summary>
    [JsonPropertyName("lockedUntil")] public long? LockedUntil { get; set; }
}

// =============================================================================
// Filas destinadas a la base de datos
// =============================================================================

public sealed record SquadRow(
    DateOnly AsOf,
    long PlayerId,
    DateTimeOffset? AcquiredAt,
    long? PurchasePrice,
    long? Clause,
    DateTimeOffset? LockedUntil);

public sealed record AccountRow(
    DateOnly AsOf,
    long UserId,
    long LeagueId,
    string? TeamName,
    long Balance,
    int? Points,
    long? SquadValue,
    int SquadSize);

public sealed record SquadMapResult(
    AccountRow Account,
    IReadOnlyList<SquadRow> Players,
    IReadOnlyList<string> Warnings);

// =============================================================================
// Mapper
// =============================================================================

public static class SquadMapper
{
    public static SquadMapResult Map(UserData data, long leagueId, DateOnly asOf)
    {
        var warnings = new List<string>();
        var rows = new List<SquadRow>();

        foreach (var player in data.Players ?? [])
        {
            if (player.Owner is null)
            {
                // No debería ocurrir consultando la propia plantilla: si pasa,
                // es que la forma de la respuesta cambió.
                warnings.Add($"Jugador {player.Id} sin bloque 'owner'; se omite.");
                continue;
            }

            rows.Add(new SquadRow(
                AsOf: asOf,
                PlayerId: player.Id,
                AcquiredAt: FromEpoch(player.Owner.Date),
                PurchasePrice: player.Owner.Price,
                Clause: player.Owner.Clause,
                LockedUntil: FromEpoch(player.Owner.LockedUntil)));
        }

        if (rows.Count == 0)
            warnings.Add("La plantilla llegó vacía. ¿Cambió la sintaxis de 'fields'?");

        var account = new AccountRow(
            AsOf: asOf,
            UserId: data.Id,
            LeagueId: leagueId,
            TeamName: data.Name,
            Balance: data.Balance,
            Points: data.Points,
            SquadValue: null,          // lo calcula el repositorio contra player_daily
            SquadSize: rows.Count);

        return new SquadMapResult(account, rows, warnings);
    }

    private static DateTimeOffset? FromEpoch(long? seconds)
        => seconds is null or <= 0 ? null : DateTimeOffset.FromUnixTimeSeconds(seconds.Value);
}