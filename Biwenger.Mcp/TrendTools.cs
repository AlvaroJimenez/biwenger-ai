using System.Text.Json;
using System.Text.Json.Nodes;
using Biwenger.Advisor;
using Dapper;
using Npgsql;

namespace Biwenger.Mcp;

/// <summary>
/// Serie histórica del valor de mercado de un jugador.
///
/// Es la única herramienta que no existía en el asesor de consola, y la que
/// justifica guardar el histórico en Postgres: la app de Biwenger solo enseña
/// una gráfica corta, y aquí se puede consultar cualquier jugador de la liga
/// (también los que no son del usuario), con huecos de captura explícitos.
/// </summary>
public sealed class TrendTools
{
    private const int DefaultDays = 14;
    private const int MaxDays = 90;
    private const int MaxCandidates = 10;

    private readonly string _connectionString;

    public TrendTools(string connectionString) => _connectionString = connectionString;

    public IReadOnlyList<ToolDefinition> All => [GetPriceTrend()];

    private ToolDefinition GetPriceTrend() => new(
        Name: "get_price_trend",

        Description: """
            Devuelve la evolución del valor de mercado de UN jugador de LaLiga
            (propio o no) en los últimos días: serie diaria, variación total,
            máximo y mínimo.

            Identifica al jugador con 'player_id' (preferible, si ya lo tienes
            de otra herramienta) o con 'name'. Si 'name' coincide con varios
            jugadores no se devuelve ninguna serie, sino la lista de candidatos
            con su player_id: vuelve a llamar con el que corresponda.

            Importes en euros. 'days' son los días hacia atrás desde la última
            captura (por defecto 14, máximo 90).

            Cómo interpretar la respuesta:
            - 'delta_1d' de cada día es la variación respecto al día anterior;
              es null si ese día anterior no se capturó. Un null NO es un cero.
            - 'missing_dates' son días sin captura dentro del periodo. Si hay,
              dilo: la tendencia se calcula sobre menos puntos de los esperados.
            - 'latest_as_of' es la fecha de la última captura y 'is_current'
              indica si es de hoy o de ayer. Si es false, el valor actual real
              puede ser distinto: menciónalo.
            - La serie solo existe desde que empezó la ingesta; si 'days_captured'
              es menor que 'days', el histórico no llega tan atrás.
            - El precio sube o baja por rendimiento y demanda; esta serie por sí
              sola no dice POR QUÉ se mueve, no lo inventes.
            """,

        InputSchema: new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["player_id"] = new JsonObject
                {
                    ["type"] = "integer",
                    ["description"] = "player_id del jugador. Tiene prioridad sobre 'name'."
                },
                ["name"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Nombre o parte del nombre del jugador, sin distinguir mayúsculas."
                },
                ["days"] = new JsonObject
                {
                    ["type"] = "integer",
                    ["description"] = $"Días hacia atrás desde la última captura. Por defecto {DefaultDays}, máximo {MaxDays}.",
                    ["minimum"] = 1,
                    ["maximum"] = MaxDays
                }
            },
            ["required"] = new JsonArray()
        },

        Execute: ExecuteAsync);

    private async Task<string> ExecuteAsync(JsonObject input, CancellationToken ct)
    {
        var playerId = input["player_id"]?.GetValue<long>();
        var name = input["name"]?.GetValue<string>()?.Trim();
        var days = Math.Clamp(input["days"]?.GetValue<int>() ?? DefaultDays, 1, MaxDays);

        if (playerId is null && string.IsNullOrEmpty(name))
            return Json(new { error = "Indica 'player_id' o 'name'." });

        await using var conn = new NpgsqlConnection(_connectionString);

        // ── Resolución del jugador ───────────────────────────────────────
        if (playerId is null)
        {
            var candidates = (await conn.QueryAsync("""
                SELECT p.player_id, p.name AS player_name, t.name AS team_name, p.position
                FROM public.player p
                LEFT JOIN public.team t ON t.team_id = p.team_id
                WHERE p.name ILIKE '%' || @name || '%'
                ORDER BY (lower(p.name) = lower(@name)) DESC, p.name
                LIMIT @limit;
                """, new { name, limit = MaxCandidates + 1 })).ToList();

            if (candidates.Count == 0)
                return Json(new { error = $"No hay ningún jugador que coincida con '{name}'." });

            // Una coincidencia exacta de nombre gana aunque haya más parciales.
            var exact = candidates.Where(c => string.Equals((string)c.player_name, name,
                StringComparison.OrdinalIgnoreCase)).ToList();

            if (exact.Count == 1)
                playerId = (long)exact[0].player_id;
            else if (candidates.Count == 1)
                playerId = (long)candidates[0].player_id;
            else
                return Json(new
                {
                    ambiguous = true,
                    message = $"'{name}' coincide con varios jugadores; vuelve a llamar con 'player_id'.",
                    truncated = candidates.Count > MaxCandidates,
                    candidates = candidates.Take(MaxCandidates)
                });
        }

        // ── Serie ────────────────────────────────────────────────────────
        // Se ancla a la última captura de la tabla, no a hoy: así el periodo
        // no se vacía si la ingesta lleva días parada.
        var latest = await conn.ExecuteScalarAsync<DateTime?>(
            "SELECT MAX(as_of)::timestamp FROM public.player_daily;");

        if (latest is null)
            return Json(new { error = "No hay datos históricos." });

        var latestDate = DateOnly.FromDateTime(latest.Value);
        var from = latestDate.AddDays(-days);

        var rows = (await conn.QueryAsync("""
            SELECT as_of::text AS as_of, player_name, team_name, market_value,
                   delta_1d, delta_7d, pct_7d, status
            FROM public.v_player_value_trend
            WHERE player_id = @playerId
              AND as_of >= @from::date
            ORDER BY as_of;
            """, new { playerId, from = from.ToString("yyyy-MM-dd") })).ToList();

        if (rows.Count == 0)
            return Json(new
            {
                error = $"El jugador {playerId} no tiene capturas en los últimos {days} días.",
                latest_as_of = latestDate.ToString("yyyy-MM-dd")
            });

        var values = rows.Where(r => r.market_value is not null)
                         .Select(r => (Date: DateOnly.Parse((string)r.as_of), Value: (long)r.market_value))
                         .ToList();

        var captured = values.Select(v => v.Date).ToHashSet();
        var missing = Enumerable.Range(0, latestDate.DayNumber - from.DayNumber + 1)
            .Select(i => from.AddDays(i))
            .Where(d => !captured.Contains(d))
            .Select(d => d.ToString("yyyy-MM-dd"))
            .ToList();

        var first = values.First();
        var last = values.Last();
        var change = last.Value - first.Value;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        return Json(new
        {
            player_id = playerId,
            player_name = (string)rows[0].player_name,
            team_name = (string?)rows[0].team_name,
            days_requested = days,
            days_captured = values.Count,
            latest_as_of = latestDate.ToString("yyyy-MM-dd"),
            is_current = latestDate >= today.AddDays(-1),
            summary = new
            {
                first_date = first.Date.ToString("yyyy-MM-dd"),
                first_value = first.Value,
                last_date = last.Date.ToString("yyyy-MM-dd"),
                last_value = last.Value,
                change_abs = change,
                change_pct = first.Value == 0 ? (double?)null : Math.Round(100.0 * change / first.Value, 2),
                max_value = values.Max(v => v.Value),
                min_value = values.Min(v => v.Value)
            },
            missing_dates = missing,
            series = rows.Select(r => new
            {
                as_of = (string)r.as_of,
                market_value = (long?)r.market_value,
                delta_1d = (long?)r.delta_1d,
                status = (string?)r.status
            })
        });
    }

    private static string Json(object value) => JsonSerializer.Serialize(value);
}
