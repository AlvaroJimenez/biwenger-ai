using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Npgsql;

namespace Biwenger.Advisor;

/// <summary>
/// Una herramienta que el modelo puede invocar. El esquema es un contrato:
/// el modelo solo sabe de esta función lo que digan Name, Description e
/// InputSchema, así que esos tres campos determinan si la usará bien.
/// </summary>
public sealed record ToolDefinition(
    string Name,
    string Description,
    JsonObject InputSchema,
    Func<JsonObject, CancellationToken, Task<string>> Execute);

public sealed class SquadTools
{
    private readonly string _connectionString;

    public SquadTools(string connectionString) => _connectionString = connectionString;

    public IReadOnlyList<ToolDefinition> All => [GetSquad()];

    // -------------------------------------------------------------------------
    // get_squad
    // -------------------------------------------------------------------------

    private ToolDefinition GetSquad() => new(
        Name: "get_squad",

       // La descripción es lo único que el modelo lee para decidir si invocar
       // esta herramienta. Conviene decir qué devuelve, en qué unidades, y —
       // sobre todo — qué NO se puede concluir de los datos.
       Description: """
    Devuelve la plantilla actual del usuario en su liga de Biwenger con tres
    familias de señales: económicas, de tendencia de valor y de rendimiento
    deportivo.

    Importes en euros. Posiciones: 1 portero, 2 defensa, 3 centrocampista,
    4 delantero.

    Trampas de estos datos, importantes para no sacar conclusiones falsas:
    - 'purchase_price' y 'gain_pct' son null en los jugadores del sorteo
      inicial: no se compraron, así que su plusvalía NO existe. No los trates
      como si hubieran costado cero, y no los excluyas del análisis por ello:
      son vendibles igual que el resto.
    - 'points_last_season' null significa que el jugador no estuvo en LaLiga la
      temporada pasada. Es ausencia de dato, no un cero.
    - 'points_season' y 'matches_played' corresponden a una temporada recién
      empezada, con muy pocas jornadas disputadas. Un 0 puede significar
      suplencia, rotación o mal partido: no es evidencia de mal rendimiento.
    - 'delta_1d' es la variación de valor de un solo día. 'delta_7d' y
      'delta_pct_7d' cubren la última semana y son mejor indicador de tendencia.
    - 'is_locked' true significa que el jugador NO se puede vender ahora.

    Al analizar, considera TODOS los jugadores devueltos, incluidos los que no
    tienen plusvalía calculable.
    """,

        InputSchema: new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["only_sellable"] = new JsonObject
                {
                    ["type"] = "boolean",
                    ["description"] =
                        "Si es true, excluye los jugadores bloqueados para la venta. " +
                        "Por defecto false, que devuelve la plantilla completa."
                }
            },
            ["required"] = new JsonArray()
        },

        Execute: ExecuteGetSquadAsync);

    private async Task<string> ExecuteGetSquadAsync(JsonObject input, CancellationToken ct)
    {
        var onlySellable = input["only_sellable"]?.GetValue<bool>() ?? false;

        const string sql = """
    SELECT player_id, player_name, team_name, position, status,
           market_value, purchase_price, clause, gain_abs, gain_pct, purchase_note,
           delta_1d, delta_7d, delta_pct_7d, delta_total,
           points_season, points_last_season, last_season_note,
           matchdays, matches_played, avg_points, starter_ratio,
           is_locked
    FROM public.v_squad_analysis
    WHERE (@onlySellable = false OR is_locked = false)
    ORDER BY market_value DESC;
    """;

        await using var conn = new NpgsqlConnection(_connectionString);
        var rows = (await conn.QueryAsync(sql, new { onlySellable })).ToList();

        // El modelo recibe texto. JSON compacto es más barato en tokens que una
        // tabla formateada y no pierde estructura.
        return JsonSerializer.Serialize(new
        {
            count = rows.Count,
            players = rows
        });
    }
}