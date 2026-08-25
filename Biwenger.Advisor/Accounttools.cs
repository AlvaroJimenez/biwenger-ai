using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Npgsql;

namespace Biwenger.Advisor;

/// <summary>
/// Contexto económico y estructural del equipo.
///
/// Existe como herramienta aparte —en vez de añadir el saldo a get_squad—
/// porque "¿cuánto dinero tengo?" es una pregunta legítima por sí sola y no
/// debería obligar a traer las fichas completas de doce jugadores.
///
/// El reparto por posición es una restricción, no un dato informativo: sin él
/// el modelo no puede saber si una venta deja una línea sin cubrir.
/// </summary>
public sealed class AccountTools
{
    private readonly string _connectionString;

    public AccountTools(string connectionString) => _connectionString = connectionString;

    public IReadOnlyList<ToolDefinition> All => [GetAccount()];

    private ToolDefinition GetAccount() => new(
        Name: "get_account",

        Description: """
            Devuelve el estado económico y la composición de la plantilla del
            usuario: saldo disponible, capacidad máxima de puja, número total de
            jugadores y cuántos hay en cada posición.

            Consúltala ANTES de recomendar fichajes. El saldo determina qué se
            puede comprar, y el reparto por posición determina qué se puede
            vender sin dejar una línea descubierta.

            Importes en euros.

            Campos:
            - 'balance': dinero disponible ahora mismo.
            - 'maximum_bid': tope de puja que permite Biwenger. Es mayor que el
              saldo porque incluye crédito respaldado por el valor de la
              plantilla. Pujar por encima del saldo obliga a vender para cubrir
              la diferencia, así que trátalo como un límite superior, no como
              dinero libre.
            - 'squad_value': valor de mercado sumado de todos los jugadores.
            - 'squad_size': número de jugadores en plantilla.
            - 'by_position': cuántos jugadores hay por posición
              (1 portero, 2 defensa, 3 centrocampista, 4 delantero).

            Un equipo de Biwenger alinea 11 jugadores, así que una plantilla
            cercana a 11 apenas tiene banquillo: vender sin fichar un sustituto
            puede dejar al usuario sin alineación válida.
            """,

        InputSchema: new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject(),
            ["required"] = new JsonArray()
        },

        Execute: ExecuteGetAccountAsync);

    private async Task<string> ExecuteGetAccountAsync(JsonObject input, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connectionString);

        // Última foto de cuenta disponible.
        const string accountSql = """
            SELECT as_of, team_name, balance, maximum_bid, squad_value, squad_size, points
            FROM public.user_account_daily
            ORDER BY as_of DESC
            LIMIT 1;
            """;

        var account = await conn.QueryFirstOrDefaultAsync(accountSql);

        if (account is null)
            return JsonSerializer.Serialize(new
            {
                error = "No hay datos de cuenta. Hace falta ejecutar la ingesta de plantilla."
            });

        // Reparto por posición, de la plantilla del mismo día.
        const string positionSql = """
            SELECT pd.position, COUNT(*) AS count
            FROM public.user_squad us
            JOIN public.player_daily pd
              ON pd.player_id = us.player_id AND pd.as_of = us.as_of
            WHERE us.as_of = (SELECT MAX(as_of) FROM public.user_squad)
            GROUP BY pd.position
            ORDER BY pd.position;
            """;

        var positions = (await conn.QueryAsync(positionSql)).ToList();

        var byPosition = new Dictionary<string, object>();
        foreach (var row in positions)
        {
            var name = (int)row.position switch
            {
                1 => "portero",
                2 => "defensa",
                3 => "centrocampista",
                4 => "delantero",
                5 => "entrenador",
                _ => $"posicion_{row.position}"
            };
            byPosition[name] = row.count;
        }

        return JsonSerializer.Serialize(new
        {
            as_of = account.as_of,
            team_name = account.team_name,
            balance = account.balance,
            maximum_bid = account.maximum_bid,
            squad_value = account.squad_value,
            squad_size = account.squad_size,
            by_position = byPosition
        });
    }
}