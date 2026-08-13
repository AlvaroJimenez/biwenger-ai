using Dapper;
using Npgsql;

namespace Biwenger.Ingest;

// =============================================================================
// REPOSITORIO
// Escribe en las tres capas dentro de UNA transacción: o entra el día entero
// o no entra nada. Un día a medias en player_daily es peor que un día perdido,
// porque luego parece legítimo y no hay forma de distinguirlo.
//
// Idempotente: reejecutar la misma fecha sobrescribe, no duplica.
// =============================================================================

public sealed class SnapshotRepository
{
    private readonly string _connectionString;

    public SnapshotRepository(string connectionString) =>
        _connectionString = connectionString;
    static SnapshotRepository()
    {
        SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());
    }
    private NpgsqlConnection Connect() => new(_connectionString);

    // -------------------------------------------------------------------------
    // Registro de ejecución. Fuera de la transacción principal a propósito:
    // si la ingesta falla, queremos que quede constancia del intento.
    // -------------------------------------------------------------------------

    public async Task<long> StartRunAsync(DateOnly asOf, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO ingest_run (as_of, started_at)
            VALUES (@AsOf, now())
            RETURNING id;
            """;

        await using var conn = Connect();
        return await conn.ExecuteScalarAsync<long>(
            new CommandDefinition(sql, new { AsOf = asOf }, cancellationToken: ct));
    }

    public async Task FinishRunAsync(
        long runId, int seen, int written, int skipped, bool ok, string? notes,
        CancellationToken ct = default)
    {
        const string sql = """
            UPDATE ingest_run
               SET finished_at     = now(),
                   players_seen    = @Seen,
                   players_written = @Written,
                   players_skipped = @Skipped,
                   ok              = @Ok,
                   notes           = @Notes
             WHERE id = @RunId;
            """;

        await using var conn = Connect();
        await conn.ExecuteAsync(new CommandDefinition(
            sql,
            new { RunId = runId, Seen = seen, Written = written, Skipped = skipped, Ok = ok, Notes = notes },
            cancellationToken: ct));
    }

    // -------------------------------------------------------------------------
    // Ingesta de un día completo
    // -------------------------------------------------------------------------

    public async Task<long> SaveAsync(
        RawFetch fetch, MapResult map, DateOnly asOf, CancellationToken ct = default)
    {
        await using var conn = Connect();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        try
        {
            var rawId = await InsertRawPayloadAsync(conn, tx, fetch, asOf, ct);
            await UpsertTeamsAsync(conn, tx, map.Teams, ct);
            await UpsertPlayersAsync(conn, tx, map.Players, ct);
            await UpsertSnapshotsAsync(conn, tx, map.Snapshots, rawId, ct);

            await tx.CommitAsync(ct);
            return rawId;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    // -------------------------------------------------------------------------
    // Capa 1 — RAW
    // -------------------------------------------------------------------------

    private static async Task<long> InsertRawPayloadAsync(
        NpgsqlConnection conn, System.Data.Common.DbTransaction tx,
        RawFetch fetch, DateOnly asOf, CancellationToken ct)
    {
        // Mismo día + mismo contenido = misma fila. Contenido distinto = fila nueva:
        // el día que Biwenger cambie el contrato, quedan guardadas las dos formas.
        const string sql = """
            INSERT INTO raw_payload
                (as_of, fetched_at, source, score_mode, http_status,
                 payload, payload_sha256, source_updated_at)
            VALUES
                (@AsOf, @FetchedAt, @Source, @ScoreMode, @HttpStatus,
                 @Payload::jsonb, @Sha256, @SourceUpdatedAt)
            ON CONFLICT (as_of, source, score_mode, payload_sha256)
            DO UPDATE SET fetched_at = EXCLUDED.fetched_at
            RETURNING id;
            """;

        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            sql,
            new
            {
                AsOf = asOf,
                FetchedAt = fetch.FetchedAt.UtcDateTime,
                Source = fetch.SourceUrl,
                ScoreMode = (short)fetch.ScoreMode,
                HttpStatus = fetch.HttpStatus,
                Payload = fetch.Json,
                Sha256 = fetch.Sha256,
                SourceUpdatedAt = fetch.SourceUpdatedAt?.UtcDateTime
            },
            transaction: tx, cancellationToken: ct));
    }

    // -------------------------------------------------------------------------
    // Capa 2 — DIMENSIONES (SCD tipo 1: se sobrescriben, sin historial)
    // -------------------------------------------------------------------------

    private static async Task UpsertTeamsAsync(
        NpgsqlConnection conn, System.Data.Common.DbTransaction tx,
        IReadOnlyList<Team> teams, CancellationToken ct)
    {
        if (teams.Count == 0) return;

        const string sql = """
            INSERT INTO team (team_id, name, slug)
            VALUES (@TeamId, @Name, @Slug)
            ON CONFLICT (team_id) DO UPDATE
               SET name = EXCLUDED.name,
                   slug = EXCLUDED.slug;
            """;

        await conn.ExecuteAsync(new CommandDefinition(
            sql, teams, transaction: tx, cancellationToken: ct));
    }

    private static async Task UpsertPlayersAsync(
        NpgsqlConnection conn, System.Data.Common.DbTransaction tx,
        IReadOnlyList<Player> players, CancellationToken ct)
    {
        if (players.Count == 0) return;

        const string sql = """
            INSERT INTO player (player_id, team_id, name, slug, position)
            VALUES (@PlayerId, @TeamId, @Name, @Slug, @Position)
            ON CONFLICT (player_id) DO UPDATE
               SET team_id  = EXCLUDED.team_id,
                   name     = EXCLUDED.name,
                   slug     = EXCLUDED.slug,
                   position = EXCLUDED.position;
            """;

        var rows = players.Select(p => new
        {
            p.PlayerId,
            p.TeamId,
            p.Name,
            p.Slug,
            Position = (short?)p.Position
        });

        await conn.ExecuteAsync(new CommandDefinition(
            sql, rows, transaction: tx, cancellationToken: ct));
    }

    // -------------------------------------------------------------------------
    // Capa 3 — HECHO DIARIO
    // -------------------------------------------------------------------------

    private static async Task UpsertSnapshotsAsync(
        NpgsqlConnection conn, System.Data.Common.DbTransaction tx,
        IReadOnlyList<PlayerSnapshot> snapshots, long rawPayloadId, CancellationToken ct)
    {
        if (snapshots.Count == 0) return;

        const string sql = """
            INSERT INTO player_daily
                (player_id, as_of, team_id, position, alt_positions,
                 market_value, fantasy_price, price_increment, shirt_number,
                 status, status_info, status_changed_at, fitness,
                 points, points_home, points_away, played_home, played_away,
                 points_last_season, raw_payload_id)
            VALUES
                (@PlayerId, @AsOf, @TeamId, @Position, @AltPositions,
                 @MarketValue, @FantasyPrice, @PriceIncrement, @ShirtNumber,
                 @Status, @StatusInfo, @StatusChangedAt, @Fitness::jsonb,
                 @Points, @PointsHome, @PointsAway, @PlayedHome, @PlayedAway,
                 @PointsLastSeason, @RawPayloadId)
            ON CONFLICT (player_id, as_of) DO UPDATE
               SET team_id            = EXCLUDED.team_id,
                   position           = EXCLUDED.position,
                   alt_positions      = EXCLUDED.alt_positions,
                   market_value       = EXCLUDED.market_value,
                   fantasy_price      = EXCLUDED.fantasy_price,
                   price_increment    = EXCLUDED.price_increment,
                   shirt_number       = EXCLUDED.shirt_number,
                   status             = EXCLUDED.status,
                   status_info        = EXCLUDED.status_info,
                   status_changed_at  = EXCLUDED.status_changed_at,
                   fitness            = EXCLUDED.fitness,
                   points             = EXCLUDED.points,
                   points_home        = EXCLUDED.points_home,
                   points_away        = EXCLUDED.points_away,
                   played_home        = EXCLUDED.played_home,
                   played_away        = EXCLUDED.played_away,
                   points_last_season = EXCLUDED.points_last_season,
                   raw_payload_id     = EXCLUDED.raw_payload_id,
                   ingested_at        = now();
            """;

        var rows = snapshots.Select(s => new
        {
            s.PlayerId,
            s.AsOf,
            s.TeamId,
            Position = (short?)s.Position,
            AltPositions = s.AltPositions?.Select(p => (short)p).ToArray(),
            s.MarketValue,
            s.FantasyPrice,
            s.PriceIncrement,
            ShirtNumber = (short?)s.ShirtNumber,
            s.Status,
            s.StatusInfo,
            StatusChangedAt = s.StatusChangedAt?.UtcDateTime,
            Fitness = s.FitnessJson,
            s.Points,
            s.PointsHome,
            s.PointsAway,
            s.PlayedHome,
            s.PlayedAway,
            s.PointsLastSeason,
            RawPayloadId = rawPayloadId
        });

        await conn.ExecuteAsync(new CommandDefinition(
            sql, rows, transaction: tx, cancellationToken: ct));
    }

    // -------------------------------------------------------------------------
    // Consultas de verificación
    // -------------------------------------------------------------------------

    public async Task<int> CountSnapshotsAsync(DateOnly asOf, CancellationToken ct = default)
    {
        const string sql = "SELECT COUNT(*) FROM player_daily WHERE as_of = @AsOf;";

        await using var conn = Connect();
        return await conn.ExecuteScalarAsync<int>(
            new CommandDefinition(sql, new { AsOf = asOf }, cancellationToken: ct));
    }

    // =============================================================================
    // Dapper no mapea DateOnly de serie. Esto le enseña a tratarlo como un DATE.
    // =============================================================================
    public sealed class DateOnlyTypeHandler : SqlMapper.TypeHandler<DateOnly>
    {
        public override void SetValue(System.Data.IDbDataParameter parameter, DateOnly value)
        {
            parameter.DbType = System.Data.DbType.Date;
            parameter.Value = value.ToDateTime(TimeOnly.MinValue);
        }

        public override DateOnly Parse(object value) =>
            DateOnly.FromDateTime((DateTime)value);
    }
}