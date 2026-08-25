using System.Text.Json;
using System.Text.Json.Nodes;
using Biwenger.Ingest;
using Dapper;
using Npgsql;

namespace Biwenger.Advisor;

/// <summary>
/// Herramienta de mercado.
///
/// Consulta Biwenger EN VIVO en lugar de leer de Postgres: el mercado rota cada
/// día a las 05:00 UTC y solo se persiste cuando se lanza la ingesta a mano.
///
/// Efecto secundario deliberado: cada consulta guarda el snapshot, así que la
/// serie histórica se construye sola sin un cron que maneje credenciales.
///
/// Los cálculos viven en la vista v_market_analysis, no aquí: la lógica de
/// negocio pertenece a SQL determinista y testeable.
/// </summary>
public sealed class MarketTools
{
    private readonly string _connectionString;
    private readonly BiwengerAuthOptions _authOptions;
    private readonly Action<string>? _trace;

    public MarketTools(string connectionString, BiwengerAuthOptions authOptions, Action<string>? trace = null)
    {
        _connectionString = connectionString;
        _authOptions = authOptions;
        _trace = trace;
    }

    public IReadOnlyList<ToolDefinition> All => [GetMarket()];

    private ToolDefinition GetMarket() => new(
        Name: "get_market",

    Description: """
            Devuelve los jugadores disponibles en el mercado de Biwenger AHORA
            MISMO, consultando la API en vivo. Incluye tanto los jugadores libres
            que ofrece el sistema como los que otros managers ponen en venta.

            Importes en euros. Posiciones: 1 portero, 2 defensa, 3 centrocampista,
            4 delantero.

            ── Cómo se consigue cada jugador (importante) ────────────────────
            'acquisition_route' indica la vía, y no son equivalentes:

            - 'subasta_sistema': jugador libre. Pujas y se resuelve al cierre
              contra las pujas de otros managers, que NO son visibles. Puedes
              perderla sin enterarte de por cuánto.
            - 'puja_a_manager': lo vende otro usuario. Puedes ofrecer 'price',
              pero el dueño DECIDE si acepta: puede rechazarte, aceptar otra
              oferta o retirar al jugador. Es una solicitud, no una compra.

            Cuando la vía es 'puja_a_manager' existe una alternativa segura:
            pagar la cláusula de rescisión ('owner_clause'). El dueño no puede
            impedirlo. Cuesta más —'clause_premium_pct' dice cuánto por encima
            del precio pedido— pero garantiza el fichaje. Solo es posible si
            'clause_available' es true.

            Al recomendar un fichaje a otro manager, menciona SIEMPRE las dos
            opciones con sus dos importes, y advierte de que la puja puede no
            prosperar.

            ── Precio ────────────────────────────────────────────────────────
            - 'price' es lo que piden; 'market_value' es lo que vale según
              Biwenger. 'price_pct_of_value' los relaciona: por debajo de 100 el
              vendedor pide menos de lo que vale; por encima, pide de más. Los
              managers suelen pedir por encima; el sistema vende al valor.
            - 'at_price_floor' true significa que el jugador está en el mínimo de
              Biwenger (150.000 €). NO es una ganga: está ahí porque el mercado
              no le ve utilidad deportiva. Casi siempre son jugadores que no
              juegan.

            ── Titularidad (lo más importante para decidir un fichaje) ───────
            - 'matchdays': jornadas disputadas por SU EQUIPO de las que hay dato.
              Distintos equipos pueden llevar distinto número.
            - 'matches_played': en cuántas de ellas jugó realmente.
            - 'starter_ratio': matches_played / matchdays. 1.00 es titular,
              0.00 es que no ha jugado nada.
            - 'avg_points': media por partido JUGADO. Es null cuando no ha jugado
              ninguno; eso es ausencia de dato, no un cero.

            AVISO sobre la muestra: la temporada acaba de empezar y 'matchdays'
            es todavía 1 o 2. Con tan pocas jornadas un starter_ratio de 0.00
            puede ser suplencia fija o un simple descanso, y un 1.00 no garantiza
            titularidad. Trátalo como indicio, nunca como conclusión, y dilo
            explícitamente cuando recomiendes a alguien basándote en ello. No
            escribas "titular fijo" con dos jornadas de muestra.

            ── Rendimiento histórico ─────────────────────────────────────────
            - 'points_last_season': TOTAL de la temporada pasada en LaLiga, no
              una media. 60 puntos pueden ser muchos partidos flojos o pocos
              buenos.
            - null significa que no jugó en LaLiga, NO que rindiera mal.

            ── Parámetros ────────────────────────────────────────────────────
            Usa 'max_price' para limitar a lo que el usuario puede permitirse y
            'position' para buscar en una línea concreta. Si necesitas varias
            posiciones, es más barato una sola llamada sin filtro con un 'limit'
            alto que varias llamadas por posición.
            """,

        InputSchema: new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["max_price"] = new JsonObject
                {
                    ["type"] = "integer",
                    ["description"] = "Precio máximo en euros. Omitir para no filtrar."
                },
                ["position"] = new JsonObject
                {
                    ["type"] = "integer",
                    ["description"] =
                        "1 portero, 2 defensa, 3 centrocampista, 4 delantero. Omitir para todas. " +
                        "NO hagas una llamada por cada posición: una sola llamada sin este filtro " +
                        "y con limit alto devuelve el mercado completo y cuesta mucho menos."
                },
                ["only_starters"] = new JsonObject
                {
                    ["type"] = "boolean",
                    ["description"] =
                        "Si es true, excluye a los jugadores que no han disputado ningún " +
                        "partido. Útil para descartar suplentes, pero con pocas jornadas " +
                        "puede dejar fuera a titulares que descansaron."
                },
                ["limit"] = new JsonObject
                {
                    ["type"] = "integer",
                    ["description"] =
                        "Máximo de jugadores a devolver. Por defecto 15, ordenados por " +
                        "precio respecto a valor de mercado."
                }
            },
            ["required"] = new JsonArray()
        },

        Execute: ExecuteGetMarketAsync);

    private async Task<string> ExecuteGetMarketAsync(JsonObject input, CancellationToken ct)
    {
        var maxPrice = input["max_price"]?.GetValue<long>();
        var position = input["position"]?.GetValue<int>();
        var onlyStarters = input["only_starters"]?.GetValue<bool>() ?? false;
        var limit = input["limit"]?.GetValue<int>() ?? 15;

        var asOf = DateOnly.FromDateTime(DateTime.UtcNow);

        // 1. Consulta en vivo a Biwenger.
        _trace?.Invoke("consultando mercado en Biwenger…");

        using var http = new HttpClient();
        using var auth = new BiwengerAuthClient(http, _authOptions);

        var json = await auth.GetMarketRawAsync(ct);
        var response = JsonSerializer.Deserialize<MarketResponse>(json)
            ?? throw new InvalidOperationException("Respuesta de mercado vacía.");

        if (response.Data is null)
            throw new InvalidOperationException("La respuesta de mercado no traía bloque 'data'.");

        var map = MarketMapper.Map(response.Data, asOf);

        // 2. Persistir: gratis, y construye la serie histórica.
        await new MarketRepository(_connectionString).SaveAsync(map, asOf, ct);
        _trace?.Invoke($"snapshot guardado: {map.Sales.Count} ventas");

        // 3. Leer de la vista, que ya trae todas las señales calculadas.
        const string sql = """
    SELECT player_id, player_name, team_name, position, status,
           acquisition_route, is_system, seller_name,
           price, market_value, price_pct_of_value, at_price_floor,
           owner_clause, clause_available, clause_premium_pct,
           delta_7d, matchdays, matches_played, avg_points, starter_ratio,
           points_season, points_last_season, last_season_note
    FROM public.v_market_analysis
    WHERE as_of = @asOf
      AND NOT is_own_sale
      AND (@maxPrice::bigint IS NULL OR price <= @maxPrice)
      AND (@position::int    IS NULL OR position = @position)
      AND (@onlyStarters = false OR matches_played > 0)
    ORDER BY price_pct_of_value ASC NULLS LAST
    LIMIT @limit;
    """;

        await using var conn = new NpgsqlConnection(_connectionString);
        var rows = (await conn.QueryAsync(sql,
            new { asOf, maxPrice, position, onlyStarters, limit })).ToList();

        return JsonSerializer.Serialize(new
        {
            count = rows.Count,
            note = "Ordenados por price_pct_of_value ascendente: los primeros piden menos " +
                   "respecto a lo que vale el jugador. Recuerda que un precio bajo con " +
                   "at_price_floor true no es una oportunidad.",
            players = rows
        });
    }
}