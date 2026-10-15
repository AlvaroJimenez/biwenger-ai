using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Npgsql;

namespace Biwenger.Advisor;

/// <summary>
/// Movimientos en marcha del usuario: pujas hechas, jugadores puestos en venta
/// y ofertas recibidas.
///
/// Existe porque ninguna otra herramienta los expone: get_market devuelve el
/// mercado excluyendo las ventas propias, y get_squad solo la plantilla actual.
/// El asesor no podía ver lo que el usuario ya tiene en marcha, y podía
/// recomendarle pujar por alguien por quien ya pujó.
///
/// La clave del análisis es que TODO movimiento pendiente es incierto: una puja
/// puede perderse y una venta puede no cerrarse. Por eso no se devuelve un
/// escenario sino cuatro, y el peor caso importa más que el optimista.
/// </summary>
public sealed class PendingTools
{
    private readonly string _connectionString;

    public PendingTools(string connectionString) => _connectionString = connectionString;

    public IReadOnlyList<ToolDefinition> All => [GetPendingMoves()];

    private const int MinGoalkeepers = 1;
    private const int MinDefenders = 3;
    private const int MinMidfielders = 2;
    private const int MinForwards = 1;
    private const int MinSquadSize = 11;
    private const int RecommendedSquadSize = 13;

    private ToolDefinition GetPendingMoves() => new(
        Name: "get_pending_moves",

        Description: """
            Devuelve los movimientos que el usuario ya tiene en marcha —pujas
            hechas, jugadores que ha puesto en venta y ofertas recibidas— junto
            con el análisis de los escenarios posibles.

            Consúltala cuando el usuario pregunte por "mi escenario", "mis
            pujas", "lo que tengo montado", o antes de recomendar fichajes: no
            tiene sentido sugerir pujar por alguien por quien ya pujó.

            ── Por qué hay varios escenarios ─────────────────────────────────
            Nada de esto está cerrado. Una puja puede perderse (las subastas del
            sistema son a ciegas, y en las ventas de manager decide el dueño) y
            una venta puede no cerrarse si nadie la acepta. Por eso se devuelven
            cuatro combinaciones:

            - 'all_succeed': gana todas las pujas y vende todo. El optimista.
            - 'bids_win_no_sales': gana las pujas pero no vende. El peligroso:
              es donde puede faltar dinero.
            - 'sales_only': vende pero pierde las pujas. Aquí sobra dinero pero
              puede faltar gente en alguna línea.
            - 'nothing_happens': todo sigue igual.

            Cada escenario trae su balance final, la composición resultante por
            posición y los problemas detectados.

            'risk_summary' resume el riesgo principal. Presta especial atención
            cuando 'bids_win_no_sales' salga con déficit: significa que el
            usuario ha comprometido más dinero del que tiene disponible sin
            vender, y no controla si las ventas se cierran a tiempo.

            ── Frescura ──────────────────────────────────────────────────────
            Solo se devuelven movimientos que no han caducado. 'market_data_as_of'
            es la fecha del último snapshot de mercado; si 'market_data_is_current'
            es false, 'data_note' lo explica: puede haber pujas o ventas más
            recientes que no se ven. Menciónalo y no afirmes que el usuario no
            tiene nada pendiente.

            ── Señales de los jugadores implicados ───────────────────────────
            Cada jugador viene con 'starter_ratio', 'avg_points' y
            'points_last_season' para poder juzgar si el movimiento tiene
            sentido deportivo, no solo económico. Con pocas jornadas disputadas
            estas señales son orientativas: dilo al valorarlas.
            """,

        InputSchema: new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject(),
            ["required"] = new JsonArray()
        },

        Execute: ExecutePendingAsync);

    private async Task<string> ExecutePendingAsync(JsonObject input, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connectionString);

        var account = await conn.QueryFirstOrDefaultAsync("""
            SELECT user_id, balance, maximum_bid
            FROM public.user_account_daily
            ORDER BY as_of DESC LIMIT 1;
            """);

        if (account is null)
            return Json(new { error = "No hay datos de cuenta." });

        long userId = account.user_id;
        long balance = account.balance;

        // ── Pujas salientes ──────────────────────────────────────────────
        // from_user_id = el usuario significa que la oferta la hizo él.
        var bids = (await conn.QueryAsync("""
            SELECT o.offer_id, o.amount, o.until,
                   m.player_id, m.player_name, m.position,
                   m.market_value, m.acquisition_route,
                   m.starter_ratio, m.avg_points, m.points_last_season,
                   ROUND(100.0 * o.amount / NULLIF(m.market_value, 0), 1) AS bid_pct_of_value
            FROM public.market_offer o
            CROSS JOIN LATERAL unnest(o.requested_players) AS rp(player_id)
            LEFT JOIN public.v_market_analysis m
                   ON m.player_id = rp.player_id
                  AND m.as_of = (SELECT MAX(as_of) FROM public.market_sale)
            WHERE o.from_user_id = @userId
              AND o.status = 'waiting'
              AND (o.until IS NULL OR o.until > now())
              AND o.last_seen_as_of = (SELECT MAX(last_seen_as_of) FROM public.market_offer);
            """, new { userId })).ToList();

        // ── Ventas propias listadas ──────────────────────────────────────
        var listings = (await conn.QueryAsync("""
    SELECT s.player_id, s.player_name, s.position, s.price,
           s.market_value, ms.until,
           q.starter_ratio, q.avg_points, q.points_last_season
    FROM public.v_market_analysis s
    JOIN public.market_sale ms
           ON ms.player_id = s.player_id AND ms.as_of = s.as_of
    LEFT JOIN public.v_squad_analysis q ON q.player_id = s.player_id
    WHERE s.as_of = (SELECT MAX(as_of) FROM public.market_sale)
      AND s.is_own_sale
      AND (ms.until IS NULL OR ms.until > now());
    """)).ToList();

        // ── Ofertas entrantes ────────────────────────────────────────────
        var incoming = (await conn.QueryAsync("""
            SELECT o.offer_id, o.amount, o.until,
                   COALESCE(o.from_name, 'Sistema') AS ofertante,
                   p.name AS player_name, rp.player_id,
                   q.market_value, q.purchase_price,
                   o.amount - q.purchase_price AS profit_vs_purchase
            FROM public.market_offer o
            CROSS JOIN LATERAL unnest(o.requested_players) AS rp(player_id)
            JOIN public.player p ON p.player_id = rp.player_id
            LEFT JOIN public.v_squad_analysis q ON q.player_id = rp.player_id
            WHERE o.to_user_id = @userId
              AND o.status = 'waiting'
              AND (o.until IS NULL OR o.until > now())
              AND o.last_seen_as_of = (SELECT MAX(last_seen_as_of) FROM public.market_offer);
            """, new { userId })).ToList();

        // ── Frescura ─────────────────────────────────────────────────────
        // El mercado solo se persiste al lanzar la ingesta o al llamar a
        // get_market. Sin esta señal, un movimiento ya cerrado o caducado
        // se presenta como pendiente y el análisis sale impecable pero falso.
        var freshness = await conn.QueryFirstAsync("""
            SELECT (SELECT MAX(last_seen_as_of) FROM public.market_offer) AS offers_as_of,
                   (SELECT MAX(as_of) FROM public.market_sale)            AS market_as_of;
            """);

        var oldest = new[] { (DateOnly?)freshness.offers_as_of, (DateOnly?)freshness.market_as_of }
            .Where(d => d.HasValue).Select(d => d!.Value).DefaultIfEmpty(DateOnly.MinValue).Min();
        var dataIsCurrent = oldest >= DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
        var dataNote = dataIsCurrent
            ? null
            : $"Los datos de mercado son del {oldest:yyyy-MM-dd}. Se han excluido los movimientos " +
              "ya caducados, pero pueden faltar pujas o ventas posteriores: dilo al usuario.";

        // ── Plantilla actual, por posición ───────────────────────────────
        var squad = (await conn.QueryAsync("""
            SELECT player_id, position FROM public.v_squad_analysis;
            """)).ToList();

        var current = squad
            .GroupBy(p => (int)p.position)
            .ToDictionary(g => g.Key, g => g.Count());

        long bidTotal = bids.Sum(b => (long)b.amount);
        long saleTotal = listings.Sum(l => (long)l.price);

        var bidPositions = bids.Select(b => b.position is null ? 0 : (int)b.position).ToList();
        var salePositions = listings.Select(l => (int)l.position).ToList();

        // ── Los cuatro escenarios ────────────────────────────────────────
        var scenarios = new
        {
            all_succeed = BuildScenario(
                "Gana todas las pujas y vende todo",
                balance, saleTotal, bidTotal, current, bidPositions, salePositions),

            bids_win_no_sales = BuildScenario(
                "Gana las pujas pero no se vende nada",
                balance, 0, bidTotal, current, bidPositions, []),

            sales_only = BuildScenario(
                "Vende todo pero pierde las pujas",
                balance, saleTotal, 0, current, [], salePositions),

            nothing_happens = BuildScenario(
                "No se cierra ningún movimiento",
                balance, 0, 0, current, [], [])
        };

        // El riesgo relevante es el peor caso, no el optimista.
        var risky = scenarios.bids_win_no_sales;
        var riskSummary = risky.remaining < 0
            ? $"RIESGO: si ganas las pujas antes de vender, te faltarían {-risky.remaining:N0} €. " +
              $"Has comprometido {bidTotal:N0} € con {balance:N0} € disponibles."
            : "Las pujas comprometidas caben en el saldo actual sin necesidad de vender.";

        return Json(new
        {
            balance,
            maximum_bid = (long?)account.maximum_bid,
            outgoing_bids = new { count = bids.Count, total = bidTotal, items = bids },
            own_listings = new { count = listings.Count, total = saleTotal, items = listings },
            incoming_offers = new { count = incoming.Count, items = incoming },
            scenarios,
            risk_summary = riskSummary,
            market_data_as_of = oldest == DateOnly.MinValue ? null : oldest.ToString("yyyy-MM-dd"),
            market_data_is_current = dataIsCurrent,
            data_note = dataNote
        });
    }

    private static ScenarioResult BuildScenario(
        string label,
        long balance,
        long income,
        long cost,
        Dictionary<int, int> current,
        IReadOnlyList<int> incomingPositions,
        IReadOnlyList<int> outgoingPositions)
    {
        var final = new Dictionary<int, int>(current);

        foreach (var pos in outgoingPositions)
            if (final.TryGetValue(pos, out var n)) final[pos] = n - 1;

        foreach (var pos in incomingPositions)
        {
            if (pos == 0) continue;   // jugador sin datos de posición
            final[pos] = final.TryGetValue(pos, out var n) ? n + 1 : 1;
        }

        int Count(int p) => final.TryGetValue(p, out var n) ? Math.Max(n, 0) : 0;

        var size = final.Values.Where(v => v > 0).Sum();
        var remaining = balance + income - cost;
        var problems = new List<string>();

        if (remaining < 0)
            problems.Add($"Faltarían {-remaining:N0} €.");
        if (Count(1) < MinGoalkeepers) problems.Add($"Quedarían {Count(1)} porteros.");
        if (Count(2) < MinDefenders) problems.Add($"Quedarían {Count(2)} defensas.");
        if (Count(3) < MinMidfielders) problems.Add($"Quedarían {Count(3)} centrocampistas.");
        if (Count(4) < MinForwards) problems.Add($"Quedarían {Count(4)} delanteros.");
        if (size < MinSquadSize) problems.Add($"La plantilla quedaría en {size} jugadores.");
        else if (size < RecommendedSquadSize)
            problems.Add($"La plantilla quedaría en {size}: por debajo de los {RecommendedSquadSize} recomendados.");

        return new ScenarioResult(
            label, balance, income, cost, remaining,
            size, Count(1), Count(2), Count(3), Count(4),
            problems.Count == 0, problems);
    }

    private sealed record ScenarioResult(
        string label,
        long balance_before,
        long sale_income,
        long bid_cost,
        long remaining,
        int squad_size,
        int goalkeepers,
        int defenders,
        int midfielders,
        int forwards,
        bool ok,
        List<string> problems);

    private static string Json(object value) => JsonSerializer.Serialize(value);
}