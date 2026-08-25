using System.Text.Json.Serialization;

namespace Biwenger.Ingest;

// =============================================================================
// Modelos de /api/v2/market
// =============================================================================

public sealed class MarketResponse
{
    [JsonPropertyName("status")] public int Status { get; set; }
    [JsonPropertyName("data")] public MarketData? Data { get; set; }
}

public sealed class MarketData
{
    [JsonPropertyName("status")] public MarketStatus? Status { get; set; }
    [JsonPropertyName("sales")] public List<MarketSale>? Sales { get; set; }
    [JsonPropertyName("offers")] public List<MarketOffer>? Offers { get; set; }
}

public sealed class MarketStatus
{
    [JsonPropertyName("balance")] public long Balance { get; set; }

    /// <summary>Capacidad máxima de puja. Mayor que el saldo; fórmula sin confirmar.</summary>
    [JsonPropertyName("maximumBid")] public long? MaximumBid { get; set; }
}

public sealed class MarketSale
{
    [JsonPropertyName("date")] public long? Date { get; set; }
    [JsonPropertyName("until")] public long? Until { get; set; }
    [JsonPropertyName("price")] public long Price { get; set; }

    /// <summary>Solo presente cuando es true. Ausente equivale a false.</summary>
    [JsonPropertyName("expired")] public bool? Expired { get; set; }

    [JsonPropertyName("player")] public MarketPlayer? Player { get; set; }

    /// <summary>null → lo vende el sistema, no un manager.</summary>
    [JsonPropertyName("user")] public MarketUser? User { get; set; }
}

public sealed class MarketPlayer
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("owner")] public MarketOwner? Owner { get; set; }
}

public sealed class MarketOwner
{
    [JsonPropertyName("date")] public long? Date { get; set; }

    /// <summary>
    /// CUIDADO: aquí llega 0 en jugadores de draft, mientras que en /user el
    /// campo simplemente no existe. Mismo significado, representación distinta.
    /// </summary>
    [JsonPropertyName("price")] public long? Price { get; set; }

    [JsonPropertyName("clause")] public long? Clause { get; set; }
    [JsonPropertyName("clauseLockedUntil")] public long? ClauseLockedUntil { get; set; }
}

public sealed class MarketUser
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
}

public sealed class MarketOffer
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("amount")] public long Amount { get; set; }
    [JsonPropertyName("created")] public long? Created { get; set; }
    [JsonPropertyName("until")] public long? Until { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }

    /// <summary>null → viene del sistema.</summary>
    [JsonPropertyName("from")] public MarketUser? From { get; set; }

    /// <summary>null → la hemos hecho nosotros al sistema.</summary>
    [JsonPropertyName("to")] public MarketUser? To { get; set; }

    [JsonPropertyName("requestedPlayers")] public List<long>? RequestedPlayers { get; set; }
}

// =============================================================================
// Filas de base de datos
// =============================================================================

public sealed record MarketSaleRow(
    DateOnly AsOf,
    long PlayerId,
    bool IsSystem,
    long? SellerUserId,
    string? SellerName,
    long Price,
    DateTimeOffset? ListedAt,
    DateTimeOffset? Until,
    bool Expired,
    long? OwnerPurchasePrice,
    long? OwnerClause,
    DateTimeOffset? OwnerSince,
    DateTimeOffset? ClauseLockedUntil);

public sealed record MarketOfferRow(
    long OfferId,
    long Amount,
    string Status,
    string? OfferType,
    long? FromUserId,
    string? FromName,
    long? ToUserId,
    string? ToName,
    long[] RequestedPlayers,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? Until,
    DateOnly LastSeenAsOf);

public sealed record MarketMapResult(
    IReadOnlyList<MarketSaleRow> Sales,
    IReadOnlyList<MarketOfferRow> Offers,
    long? MaximumBid,
    IReadOnlyList<string> Warnings);

// =============================================================================
// Mapper
// =============================================================================

public static class MarketMapper
{
    public static MarketMapResult Map(MarketData data, DateOnly asOf)
    {
        var warnings = new List<string>();
        var sales = new List<MarketSaleRow>();
        var seenPlayers = new HashSet<long>();

        foreach (var sale in data.Sales ?? [])
        {
            if (sale.Player is null)
            {
                warnings.Add("Venta sin bloque 'player'; se omite.");
                continue;
            }

            // La PK es (as_of, player_id). Si la API listara el mismo jugador
            // dos veces el mismo día, el INSERT fallaría: mejor detectarlo aquí.
            if (!seenPlayers.Add(sale.Player.Id))
            {
                warnings.Add($"Jugador {sale.Player.Id} aparece más de una vez en ventas; se queda la primera.");
                continue;
            }

            sales.Add(new MarketSaleRow(
                AsOf: asOf,
                PlayerId: sale.Player.Id,
                IsSystem: sale.User is null,
                SellerUserId: sale.User?.Id,
                SellerName: sale.User?.Name,
                Price: sale.Price,
                ListedAt: FromEpoch(sale.Date),
                Until: FromEpoch(sale.Until),
                Expired: sale.Expired ?? false,
                OwnerPurchasePrice: NormalizePurchasePrice(sale.Player.Owner?.Price),
                OwnerClause: sale.Player.Owner?.Clause,
                OwnerSince: FromEpoch(sale.Player.Owner?.Date),
                ClauseLockedUntil: FromEpoch(sale.Player.Owner?.ClauseLockedUntil)));
        }

        var offers = new List<MarketOfferRow>();

        foreach (var offer in data.Offers ?? [])
        {
            var players = offer.RequestedPlayers?.ToArray() ?? [];

            if (players.Length == 0)
            {
                warnings.Add($"Oferta {offer.Id} sin jugadores solicitados; se omite.");
                continue;
            }

            // Si esto salta algún día, el array deja de ser una precaución
            // teórica y hay que revisar cómo se consulta.
            if (players.Length > 1)
                warnings.Add($"Oferta {offer.Id} con {players.Length} jugadores: primer intercambio múltiple observado.");

            offers.Add(new MarketOfferRow(
                OfferId: offer.Id,
                Amount: offer.Amount,
                Status: offer.Status ?? "unknown",
                OfferType: offer.Type,
                FromUserId: offer.From?.Id,
                FromName: offer.From?.Name,
                ToUserId: offer.To?.Id,
                ToName: offer.To?.Name,
                RequestedPlayers: players,
                CreatedAt: FromEpoch(offer.Created),
                Until: FromEpoch(offer.Until),
                LastSeenAsOf: asOf));
        }

        if (sales.Count == 0 && offers.Count == 0)
            warnings.Add("El mercado llegó vacío. ¿Cambió la forma de la respuesta?");

        return new MarketMapResult(sales, offers, data.Status?.MaximumBid, warnings);
    }

    /// <summary>
    /// La API manda 0 para jugadores del draft, que no se compraron. Un 0 real
    /// haría que la plusvalía fuese igual al precio íntegro, así que se
    /// normaliza a NULL: "no hay precio de compra" ≠ "costó cero".
    /// </summary>
    private static long? NormalizePurchasePrice(long? price)
        => price is null or 0 ? null : price;

    private static DateTimeOffset? FromEpoch(long? seconds)
        => seconds is null or <= 0 ? null : DateTimeOffset.FromUnixTimeSeconds(seconds.Value);
}