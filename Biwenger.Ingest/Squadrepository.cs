using Dapper;
using Npgsql;

namespace Biwenger.Ingest;

/// <summary>
/// Persistencia de la plantilla y la cuenta del usuario.
/// Mismo contrato que SnapshotRepository: upsert idempotente por (as_of, ...).
/// </summary>
public sealed class SquadRepository
{
    private readonly string _connectionString;

    public SquadRepository(string connectionString) => _connectionString = connectionString;

    /// <summary>
    /// Guarda plantilla y cuenta en una única transacción. Reejecutar el mismo
    /// día sobrescribe: no duplica ni acumula.
    /// Devuelve el valor total de la plantilla calculado contra player_daily.
    /// </summary>
    public async Task<long?> SaveAsync(SquadMapResult map, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // La plantilla cambia de tamaño: si vendiste a alguien, su fila del día
        // debe desaparecer, no quedarse por no estar en el upsert.
        await conn.ExecuteAsync(
            "DELETE FROM public.user_squad WHERE as_of = @AsOf",
            new { map.Account.AsOf }, tx);

        const string insertPlayer = """
            INSERT INTO public.user_squad
                (as_of, player_id, acquired_at, purchase_price, clause, locked_until)
            VALUES
                (@AsOf, @PlayerId, @AcquiredAt, @PurchasePrice, @Clause, @LockedUntil)
            ON CONFLICT (as_of, player_id) DO UPDATE SET
                acquired_at    = EXCLUDED.acquired_at,
                purchase_price = EXCLUDED.purchase_price,
                clause         = EXCLUDED.clause,
                locked_until   = EXCLUDED.locked_until,
                ingested_at    = now();
            """;

        await conn.ExecuteAsync(insertPlayer, map.Players, tx);

        // Valor de plantilla al cierre de ese día. Se calcula aquí, en SQL,
        // porque los datos de mercado ya están en la base.
        var squadValue = await conn.ExecuteScalarAsync<long?>("""
            SELECT SUM(pd.market_value)
            FROM public.user_squad us
            JOIN public.player_daily pd
              ON pd.player_id = us.player_id
             AND pd.as_of     = us.as_of
            WHERE us.as_of = @AsOf;
            """, new { map.Account.AsOf }, tx);

        const string insertAccount = """
            INSERT INTO public.user_account_daily
                (as_of, user_id, league_id, team_name, balance, points, squad_value, squad_size)
            VALUES
                (@AsOf, @UserId, @LeagueId, @TeamName, @Balance, @Points, @SquadValue, @SquadSize)
            ON CONFLICT (as_of, user_id, league_id) DO UPDATE SET
                team_name   = EXCLUDED.team_name,
                balance     = EXCLUDED.balance,
                points      = EXCLUDED.points,
                squad_value = EXCLUDED.squad_value,
                squad_size  = EXCLUDED.squad_size,
                ingested_at = now();
            """;

        await conn.ExecuteAsync(insertAccount, map.Account with { SquadValue = squadValue }, tx);

        await tx.CommitAsync(ct);
        return squadValue;
    }

    /// <summary>Comprobación rápida tras la ingesta.</summary>
    public async Task<int> CountSquadAsync(DateOnly asOf, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM public.user_squad WHERE as_of = @asOf", new { asOf });
    }
}