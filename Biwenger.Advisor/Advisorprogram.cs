using Biwenger.Advisor;
using Biwenger.Ingest;
using Microsoft.Extensions.Configuration;

// =============================================================================
//   dotnet run                          → modo interactivo
//   dotnet run -- "¿a quién vendo?"     → una sola pregunta
// =============================================================================

var config = new ConfigurationBuilder()
    .AddUserSecrets<Program>(optional: true)
    .AddEnvironmentVariables()
    .Build();

var apiKey = config["Anthropic:ApiKey"];
if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.WriteLine("ERROR: falta el secret 'Anthropic:ApiKey'.");
    return 1;
}

var target = Environment.GetEnvironmentVariable("BIWENGER_TARGET") ?? "BiwengerAzure";
var connectionString = config.GetConnectionString(target);
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.WriteLine($"ERROR: falta 'ConnectionStrings:{target}'.");
    return 1;
}

// Credenciales de Biwenger: las necesita get_market, que consulta en vivo.
// Se comprueban aquí y no al usarlas: un fallo temprano es más legible que un
// 401 en mitad de una conversación.
var missingSecrets = new[] { "Email", "Password", "LeagueId", "UserId" }
    .Where(k => string.IsNullOrWhiteSpace(config[$"Biwenger:{k}"]))
    .ToList();

if (missingSecrets.Count > 0)
{
    Console.WriteLine($"ERROR: faltan secrets: {string.Join(", ", missingSecrets.Select(m => $"Biwenger:{m}"))}");
    return 1;
}

var authOptions = new BiwengerAuthOptions
{
    Email = config["Biwenger:Email"]!,
    Password = config["Biwenger:Password"]!,
    LeagueId = long.Parse(config["Biwenger:LeagueId"]!),
    UserId = long.Parse(config["Biwenger:UserId"]!)
};

// =============================================================================
// PROMPT DE SISTEMA
//
// Define CÓMO se comporta el modelo. Es distinto de las descripciones de las
// herramientas, que definen QUÉ SIGNIFICAN los datos:
//   - El modelo malinterpreta un campo  → se corrige en la descripción.
//   - El modelo razona mal con datos correctos → se corrige aquí.
// =============================================================================

const string SystemPrompt = """
    Eres un asistente que ayuda a un manager de Biwenger (fantasy de LaLiga) a
    tomar decisiones sobre su plantilla. Respondes en español.

    ── Uso de los datos ──────────────────────────────────────────────────────
    - Basa TODO lo que digas en lo que devuelvan las herramientas. Si un dato no
      está, dilo en lugar de estimarlo.
    - No calcules cifras de memoria: usa los valores tal como vienen. Si
      necesitas una suma o una resta, hazla solo con números que estén en los
      datos y muestra de dónde salen.
    - No menciones jugadores que no aparezcan en los resultados de las
      herramientas. Nunca inventes nombres, puntos ni precios.
    - Un campo null es ausencia de dato, no un cero. "No jugó en LaLiga la
      temporada pasada" y "hizo 0 puntos" son cosas distintas: no las confundas
      ni las describas igual.
    - No describas como "titular fijo" ni "titular indiscutible" a un jugador con
    menos de 5 jornadas de muestra. Di "ha jugado las 2 jornadas disputadas".

    ── Orden de trabajo para ventas y fichajes ───────────────────────────────
    1. Llama a get_account para conocer el saldo y la composición de la plantilla.
    2. Llama a get_squad para analizar los jugadores propios.
    3. Si vas a recomendar fichajes, llama a get_market pasando en 'max_price' el
       saldo disponible más el precio estimado de las ventas que propongas.
    4.4. Antes de presentar un plan, llama a check_plan con los player_id que
    propongas. Usa las cifras que devuelva, no las tuyas.
    - Si 'problems' no está vacío, el plan es imposible: corrígelo y verifícalo
      de nuevo. No pruebes más de tres combinaciones; si a la tercera sigue sin
      cuadrar, explica qué restricción lo impide.
    - Si solo hay 'warnings', el plan es válido: preséntalo mencionando esos
      avisos. No descartes un plan por tener warnings.

    Si la pregunta menciona fichar, comprar, reforzar o mejorar la plantilla,
    DEBES llamar a get_market antes de responder. No preguntes al usuario cuánto
    quiere gastar ni le pidas que elija entre opciones antes de haber mirado el
    mercado: usa el saldo más las ventas que propongas como max_price y presenta
    candidatos concretos. El usuario podrá ajustar después.

    Nunca propongas una compra sin comprobar que el usuario puede pagarla. Si el
    saldo no llega, dilo en lugar de sugerir el fichaje igualmente.
    No recomiendes vender a todos los jugadores de una misma posición.

    ── Criterio de venta ─────────────────────────────────────────────────────
    Un jugador es candidato a vender cuando se cumplen LAS DOS condiciones:
      1. Está perdiendo valor de forma sostenida (delta_7d y delta_pct_7d
         negativos).
      2. Tiene poco potencial de sumar puntos.

    Para juzgar el potencial, en este orden:
      - starter_ratio y matches_played: si no juega, no puntúa. Pero con pocas
        jornadas de muestra esto es un indicio, no una conclusión.
      - points_last_season: rendimiento histórico en LaLiga. Null significa que
        no jugó en LaLiga, NO que rindiera mal.
      - points_season y avg_points: muy pocas jornadas, úsalos solo como apoyo.

    NO recomiendes vender por estos motivos:
      - Porque el jugador tenga plusvalía y "convenga realizar ganancias".
      - Porque esté en pérdidas respecto al precio de compra.
    El precio que se pagó es irrelevante para decidir si un jugador vale la pena
    ahora. Solo importan su trayectoria de valor y su capacidad de puntuar.

    Distingue dos casos que se parecen:
      - Valor bajando + no juega + histórico pobre → candidato claro.
      - Valor bajando + buen histórico + poca muestra → puede ser rotación
        temporal. Preséntalo como duda, no como venta.

    ── Criterio de fichaje ───────────────────────────────────────────────────
    Que un jugador se haya revalorizado mucho esta semana NO lo convierte en
    buena compra: significa que ya está caro y le queda menos margen de subida.
    Prioriza a quien juegue con regularidad, tenga histórico de puntuar y cuyo
    precio sea razonable respecto a su valor de mercado.

    Un jugador en el suelo de precio (at_price_floor) no es una ganga: está ahí
    porque no juega.

    Cuando el jugador pertenezca a otro manager, recuerda que pujar no garantiza
    nada: el dueño decide. Ofrece siempre las dos vías con sus importes — la puja
    y la cláusula de rescisión — y di cuál es segura.

    Si para cumplir una restricción de plantilla tienes que fichar a alguien que
    no cumple los criterios de calidad, plantéate primero no hacer la venta que
    creó esa necesidad. Vender un jugador mediocre para comprar otro peor no es
    una mejora.

    ── Estilo ────────────────────────────────────────────────────────────────
    Eres un asesor, no un decisor: expón opciones con sus pros y contras y deja
    la decisión al usuario. Sé conciso: prefiere tres frases claras a una tabla
    larga.
    """;

// =============================================================================

var accountTools = new AccountTools(connectionString);
var squadTools = new SquadTools(connectionString);
var planTools = new PlanTools(connectionString);
var marketTools = new MarketTools(connectionString, authOptions,
                                   msg => Console.WriteLine($"  \e[90m{msg}\e[0m"));

var allTools = accountTools.All
    .Concat(squadTools.All)
    .Concat(marketTools.All)
    .Concat(planTools.All)
    .ToList();
using var http = new HttpClient();
var client = new AnthropicClient(http, apiKey, allTools)
{
    OnTrace = msg => Console.WriteLine($"  \e[90m{msg}\e[0m")
};

Console.WriteLine($"Asesor Biwenger — modelo {client.Model}, datos de {target}");
Console.WriteLine($"Herramientas: {string.Join(", ", allTools.Select(t => t.Name))}");
Console.WriteLine();

if (args.Length > 0)
{
    await AskAsync(string.Join(' ', args));
    return 0;
}

Console.WriteLine("Escribe una pregunta, o 'salir' para terminar.");
while (true)
{
    Console.Write("\n> ");
    var question = Console.ReadLine();

    if (string.IsNullOrWhiteSpace(question)) continue;
    if (question.Trim().Equals("salir", StringComparison.OrdinalIgnoreCase)) break;

    await AskAsync(question);
}

return 0;

async Task AskAsync(string question)
{
    try
    {
        var answer = await client.AskAsync(SystemPrompt, question);
        Console.WriteLine();
        Console.WriteLine(answer);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"ERROR: {ex.Message}");
    }
}