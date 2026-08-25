using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Npgsql;

namespace Biwenger.Advisor;

/// <summary>
/// Verificación determinista de un plan de fichajes.
///
/// Existe porque un LLM no debe hacer aritmética: produce cifras plausibles y
/// equivocadas, y las presenta con la misma seguridad que las correctas. Aquí
/// el modelo PROPONE (qué vender, qué comprar) y este código VERIFICA (si el
/// dinero llega y si la plantilla queda válida).
///
/// Todos los importes se leen de la base, no del modelo.
///
/// Distingue dos categorías, y la diferencia importa:
///   - 'problems'  → hacen el plan IMPOSIBLE. No hay dinero, o no se podría
///                   alinear un once. El plan se marca como no viable.
///   - 'warnings'  → riesgos y recomendaciones. El plan sigue siendo viable;
///                   el usuario decide si asume el riesgo.
///
/// Meter una puja incierta en 'problems' invalidaría cualquier plan que
/// implique negociar con otro manager, que es algo perfectamente normal.
/// </summary>
public sealed class PlanTools
{
    private readonly string _connectionString;

    public PlanTools(string connectionString) => _connectionString = connectionString;

    public IReadOnlyList<ToolDefinition> All => [CheckPlan()];

    // Mínimos por línea para poder alinear un once.
    private const int MinGoalkeepers = 1;
    private const int MinDefenders = 3;
    private const int MinMidfielders = 2;
    private const int MinForwards = 1;

    // Once titular: por debajo de esto el plan es inviable.
    private const int MinSquadSize = 11;

    // Tamaño deseable: 11 titulares más dos suplentes para cubrir lesiones,
    // sanciones y rotaciones. Por debajo se avisa, pero no se bloquea.
    private const int RecommendedSquadSize = 13;

    private ToolDefinition CheckPlan() => new(
        Name: "check_plan",

        Description: """
            Verifica si un plan de ventas y fichajes es viable. Devuelve las
            cuentas exactas y los problemas detectados.

            LLAMA A ESTA HERRAMIENTA SIEMPRE antes de presentar un plan al
            usuario, y presenta las cifras que devuelva en lugar de las tuyas.
            No sumes ni restes importes por tu cuenta: esta herramienta lo hace
            con los precios reales de la base de datos.

            Parámetros:
            - 'sell': lista de player_id de la plantilla que se venderían.
            - 'buy': lista de objetos {player_id, via} donde 'via' es 'puja' o
              'clausula'. La cláusula es más cara pero garantizada.

            La respuesta distingue dos cosas:
            - 'problems': hacen el plan IMPOSIBLE (no hay dinero, no se podría
              alinear un once, el jugador está bloqueado). Si hay alguno,
              'viable' es false y NO debes presentar el plan: corrígelo.
            - 'warnings': riesgos que el usuario debe conocer pero que no
              impiden el plan (una puja puede ser rechazada, la plantilla queda
              sin suplentes). Menciónalos en tu respuesta, pero el plan sigue
              siendo válido.

            'squad_advice' indica si la plantilla resultante tiene banquillo
            suficiente. Lo recomendable son 13 jugadores: once titulares más dos
            suplentes para cubrir lesiones y sanciones.
            """,

        InputSchema: new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["sell"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "player_id de los jugadores propios a vender.",
                    ["items"] = new JsonObject { ["type"] = "integer" }
                },
                ["buy"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "Jugadores a fichar y por qué vía.",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["player_id"] = new JsonObject { ["type"] = "integer" },
                            ["via"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["enum"] = new JsonArray { "puja", "clausula" },
                                ["description"] = "'puja' usa el precio pedido; 'clausula' el de rescisión."
                            }
                        },
                        ["required"] = new JsonArray { "player_id", "via" }
                    }
                }
            },
            ["required"] = new JsonArray()
        },

        Execute: ExecuteCheckPlanAsync);

    private async Task<string> ExecuteCheckPlanAsync(JsonObject input, CancellationToken ct)
    {
        var sellIds = (input["sell"]?.AsArray() ?? [])
            .Select(n => n!.GetValue<long>())
            .Distinct()
            .ToArray();

        var buyItems = (input["buy"]?.AsArray() ?? [])
            .Select(n => (
                PlayerId: n!["player_id"]!.GetValue<long>(),
                Via: n["via"]?.GetValue<string>() ?? "puja"))
            .ToList();

        await using var conn = new NpgsqlConnection(_connectionString);

        var account = await conn.QueryFirstOrDefaultAsync("""
            SELECT balance, squad_size
            FROM public.user_account_daily
            ORDER BY as_of DESC LIMIT 1;
            """);

        if (account is null)
            return Json(new { error = "No hay datos de cuenta." });

        long balance = account.balance;

        var squad = (await conn.QueryAsync("""
            SELECT player_id, player_name, position, market_value, is_locked
            FROM public.v_squad_analysis;
            """)).ToList();

        var problems = new List<string>();   // bloqueantes
        var warnings = new List<string>();   // informativos

        // ── Ventas ───────────────────────────────────────────────────────
        long saleIncome = 0;
        var saleDetail = new List<object>();

        foreach (var id in sellIds)
        {
            var player = squad.FirstOrDefault(p => (long)p.player_id == id);

            if (player is null)
            {
                problems.Add($"El jugador {id} no está en la plantilla y no se puede vender.");
                continue;
            }

            if ((bool)player.is_locked)
            {
                problems.Add($"{player.player_name} está bloqueado: no se puede vender ahora.");
                continue;
            }

            long value = player.market_value;
            saleIncome += value;
            saleDetail.Add(new { player_id = id, name = (string)player.player_name, amount = value });
        }

        // ── Compras ──────────────────────────────────────────────────────
        long buyCost = 0;
        var buyDetail = new List<object>();
        var incomingPositions = new List<int>();

        foreach (var (playerId, via) in buyItems)
        {
            var offer = await conn.QueryFirstOrDefaultAsync("""
                SELECT player_name, position, price, owner_clause, clause_available,
                       acquisition_route, is_own_sale
                FROM public.v_market_analysis
                WHERE player_id = @playerId
                  AND as_of = (SELECT MAX(as_of) FROM public.market_sale);
                """, new { playerId });

            if (offer is null)
            {
                problems.Add($"El jugador {playerId} no está en el mercado ahora mismo.");
                continue;
            }

            if ((bool)offer.is_own_sale)
            {
                problems.Add($"{offer.player_name} ya es del usuario: no tiene sentido ficharlo.");
                continue;
            }

            long cost;

            if (via == "clausula")
            {
                if (!(bool)offer.clause_available)
                {
                    // No poder pagar la cláusula no invalida el plan: se puja.
                    warnings.Add($"{offer.player_name} no tiene cláusula disponible; se ha calculado con el precio de puja.");
                    cost = offer.price;
                }
                else
                {
                    cost = offer.owner_clause;
                }
            }
            else
            {
                cost = offer.price;
            }

            // Riesgo, no impedimento: el plan es válido, pero puede no salir.
            var route = (string)offer.acquisition_route;

            if (route == "puja_a_manager" && via != "clausula")
                warnings.Add($"{offer.player_name}: la puja depende de que el dueño acepte. Si quieres asegurarlo, usa la cláusula.");
            else if (route == "subasta_sistema")
                warnings.Add($"{offer.player_name}: es una subasta del sistema, se resuelve contra las pujas de otros managers, que no son visibles.");

            buyCost += cost;
            incomingPositions.Add((int)offer.position);
            buyDetail.Add(new
            {
                player_id = playerId,
                name = (string)offer.player_name,
                via,
                amount = cost,
                route
            });
        }

        // ── Presupuesto ──────────────────────────────────────────────────
        var available = balance + saleIncome;
        var remaining = available - buyCost;

        if (remaining < 0)
            problems.Add($"El plan no es asequible: faltan {-remaining:N0} €.");

        // ── Composición resultante ───────────────────────────────────────
        var finalPositions = squad
            .Where(p => !sellIds.Contains((long)p.player_id))
            .Select(p => (int)p.position)
            .Concat(incomingPositions)
            .GroupBy(x => x)
            .ToDictionary(g => g.Key, g => g.Count());

        int Count(int pos) => finalPositions.TryGetValue(pos, out var n) ? n : 0;

        var finalSize = finalPositions.Values.Sum();

        // Bloqueantes: sin esto no hay once posible.
        if (Count(1) < MinGoalkeepers)
            problems.Add($"Quedarían {Count(1)} porteros: hace falta al menos {MinGoalkeepers}.");
        if (Count(2) < MinDefenders)
            problems.Add($"Quedarían {Count(2)} defensas: hacen falta al menos {MinDefenders}.");
        if (Count(3) < MinMidfielders)
            problems.Add($"Quedarían {Count(3)} centrocampistas: hacen falta al menos {MinMidfielders}.");
        if (Count(4) < MinForwards)
            problems.Add($"Quedarían {Count(4)} delanteros: hace falta al menos {MinForwards}.");
        if (finalSize < MinSquadSize)
            problems.Add($"La plantilla quedaría en {finalSize} jugadores: no llegan para alinear {MinSquadSize}.");

        // Aviso: se puede alinear, pero sin margen.
        var bench = finalSize - MinSquadSize;
        string squadAdvice;

        if (finalSize < MinSquadSize)
        {
            squadAdvice = $"Insuficiente: {finalSize} jugadores, no se puede alinear un once.";
        }
        else if (finalSize < RecommendedSquadSize)
        {
            squadAdvice = $"Justa: {finalSize} jugadores ({bench} suplente(s)). Lo recomendable son " +
                          $"{RecommendedSquadSize} para cubrir lesiones y sanciones.";
            warnings.Add($"La plantilla quedaría en {finalSize} jugadores, con solo {bench} suplente(s). " +
                         $"Con menos de {RecommendedSquadSize} cualquier lesión o sanción deja el once corto.");
        }
        else
        {
            squadAdvice = $"Adecuada: {finalSize} jugadores ({bench} suplentes).";
        }

        return Json(new
        {
            viable = problems.Count == 0,
            budget = new
            {
                balance_before = balance,
                sale_income = saleIncome,
                available = available,
                buy_cost = buyCost,
                remaining = remaining
            },
            sales = saleDetail,
            purchases = buyDetail,
            final_squad = new
            {
                size = finalSize,
                goalkeepers = Count(1),
                defenders = Count(2),
                midfielders = Count(3),
                forwards = Count(4)
            },
            squad_advice = squadAdvice,
            problems,
            warnings
        });
    }

    private static string Json(object value) => JsonSerializer.Serialize(value);
}