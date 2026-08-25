using Dapper;
using Npgsql;

namespace Biwenger.Ingest;

/// <summary>
/// Persistencia del mercado diario.
///
/// Dos estrategias distintas a propósito:
///  - market_sale  no tiene id en la API → snapshot diario, borrar y reinsertar.
///  - market_offer sí tiene id estable   → upsert en sitio, conservando
///                                          first_seen_at para ver su historia.
/// </summary>
public sealed class MarketRepository
{
    private readonly string _connectionString;

    public MarketRepository(string connectionString) => _connectionString = connectionString;

    public async Task<(int Sales, int Offers)> SaveAsync(
        MarketMapResult map, DateOnly asOf, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Ventas: el mercado del día es el que manda. Si un jugador ya no está,
        // su fila debe desaparecer, no quedarse por no venir en el upsert.
        await conn.ExecuteAsync(
            "DELETE FROM public.market_sale WHERE as_of = @asOf", new { asOf }, tx);

        const string insertSale = """
            INSERT INTO public.market_sale
                (as_of, player_id, is_system, seller_user_id, seller_name,
                 price, listed_at, until, expired,
                 owner_purchase_price, owner_clause, owner_since, clause_locked_until)
            VALUES
                (@AsOf, @PlayerId, @IsSystem, @SellerUserId, @SellerName,
                 @Price, @ListedAt, @Until, @Expired,
                 @OwnerPurchasePrice, @OwnerClause, @OwnerSince, @ClauseLockedUntil);
            """;

        var salesWritten = await conn.ExecuteAsync(insertSale, map.Sales, tx);

        // Ofertas: entidad viva. first_seen_at se preserva en el UPDATE para
        // poder medir cuánto tiempo lleva abierta una oferta.
        const string upsertOffer = """
            INSERT INTO public.market_offer
                (offer_id, amount, status, offer_type,
                 from_user_id, from_name, to_user_id, to_name,
                 requested_players, created_at, until, last_seen_as_of)
            VALUES
                (@OfferId, @Amount, @Status, @OfferType,
                 @FromUserId, @FromName, @ToUserId, @ToName,
                 @RequestedPlayers, @CreatedAt, @Until, @LastSeenAsOf)
            ON CONFLICT (offer_id) DO UPDATE SET
                amount           = EXCLUDED.amount,
                status           = EXCLUDED.status,
                offer_type       = EXCLUDED.offer_type,
                from_user_id     = EXCLUDED.from_user_id,
                from_name        = EXCLUDED.from_name,
                to_user_id       = EXCLUDED.to_user_id,
                to_name          = EXCLUDED.to_name,
                requested_players= EXCLUDED.requested_players,
                created_at       = EXCLUDED.created_at,
                until            = EXCLUDED.until,
                last_seen_at     = now(),
                last_seen_as_of  = EXCLUDED.last_seen_as_of;
            """;

        var offersWritten = await conn.ExecuteAsync(upsertOffer, map.Offers, tx);

        // maximumBid pertenece al estado de cuenta del día, no al mercado.
        if (map.MaximumBid is { } maxBid)
        {
            await conn.ExecuteAsync("""
                UPDATE public.user_account_daily
                   SET maximum_bid = @maxBid
                 WHERE as_of = @asOf;
                """, new { maxBid, asOf }, tx);
        }

        await tx.CommitAsync(ct);
        return (salesWritten, offersWritten);
    }
}