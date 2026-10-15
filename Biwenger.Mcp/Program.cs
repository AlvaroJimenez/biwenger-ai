using System.Text.Json;
using System.Text.Json.Nodes;
using Biwenger.Advisor;
using Biwenger.Mcp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

// =============================================================================
// Servidor MCP de Biwenger (transporte stdio).
//
// En stdio la salida estándar ES el canal del protocolo: cualquier Console.Write
// la corrompe. Por eso los logs van a stderr, y nada de este proyecto debe
// escribir en stdout.
//
// Las herramientas son las mismas ToolDefinition que usa el asesor de consola:
// una única fuente de verdad para nombre, descripción, esquema y ejecución.
// =============================================================================

var config = new ConfigurationBuilder()
    .AddUserSecrets<Program>(optional: true)
    .AddEnvironmentVariables()
    .Build();

var target = Environment.GetEnvironmentVariable("BIWENGER_TARGET") ?? "BiwengerAzure";
var connectionString = config.GetConnectionString(target);
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine($"ERROR: falta 'ConnectionStrings:{target}'.");
    return 1;
}

// Las herramientas se van añadiendo a esta lista a medida que se migran.
ToolDefinition[] allTools =
[
    .. new SquadTools(connectionString).All,
    .. new AccountTools(connectionString).All,
    .. new PlanTools(connectionString).All,
    .. new PendingTools(connectionString).All,
    .. new TrendTools(connectionString).All
];
var tools = allTools.ToDictionary(t => t.Name);

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithListToolsHandler((_, _) => ValueTask.FromResult(new ListToolsResult
    {
        Tools = tools.Values.Select(t => new Tool
        {
            Name = t.Name,
            Description = t.Description,
            InputSchema = JsonSerializer.SerializeToElement(t.InputSchema)
        }).ToList()
    }))
    .WithCallToolHandler(async (context, ct) =>
    {
        var name = context.Params!.Name;
        if (!tools.TryGetValue(name, out var tool))
            return Error($"Herramienta desconocida: {name}");

        var input = new JsonObject();
        if (context.Params.Arguments is { } args)
            foreach (var (key, value) in args)
                input[key] = JsonNode.Parse(value.GetRawText());

        try
        {
            var json = await tool.Execute(input, ct);
            return new CallToolResult { Content = [new TextContentBlock { Text = json }] };
        }
        catch (Exception ex)
        {
            return Error($"{name} falló: {ex.Message}");
        }
    });

await builder.Build().RunAsync();
return 0;

static CallToolResult Error(string message) => new()
{
    IsError = true,
    Content = [new TextContentBlock { Text = message }]
};
