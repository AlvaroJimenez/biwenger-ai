using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Biwenger.Advisor;

/// <summary>
/// Bucle de tool calling contra la API de Anthropic.
///
/// El protocolo tiene cuatro pasos y conviene tenerlos presentes:
///   1. Enviamos la conversación junto con las definiciones de herramientas.
///   2. El modelo responde con stop_reason = "tool_use" y uno o más bloques
///      tool_use, cada uno con su id.
///   3. Ejecutamos cada herramienta y devolvemos un mensaje de rol "user" con
///      bloques tool_result, referenciando el tool_use_id correspondiente.
///   4. El modelo redacta la respuesta final (o pide otra herramienta, y se
///      repite el ciclo).
///
/// El punto que más se equivoca al implementarlo: el mensaje del assistant con
/// los bloques tool_use DEBE volver a enviarse en la siguiente petición. La API
/// no tiene estado; si se omite, el tool_result queda huérfano y falla.
/// </summary>
public sealed class AnthropicClient
{
    private const string Endpoint = "https://api.anthropic.com/v1/messages";
    private const string ApiVersion = "2023-06-01";

    /// <summary>Modelo barato mientras se depura el bucle.</summary>
    public string Model { get; init; } = "claude-sonnet-4-6";
    public int MaxTokens { get; init; } = 2000;

    /// <summary>Cortafuegos: evita un bucle infinito de llamadas a herramientas.</summary>
    public int MaxIterations { get; init; } = 12;
    private readonly HttpClient _http;
    private readonly IReadOnlyDictionary<string, ToolDefinition> _tools;
    private readonly JsonArray _toolSchemas;

    public AnthropicClient(HttpClient http, string apiKey, IReadOnlyList<ToolDefinition> tools)
    {
        _http = http;
        _http.DefaultRequestHeaders.Add("x-api-key", apiKey);
        _http.DefaultRequestHeaders.Add("anthropic-version", ApiVersion);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.Timeout = TimeSpan.FromSeconds(120);

        _tools = tools.ToDictionary(t => t.Name);

        _toolSchemas = [];
        foreach (var tool in tools)
        {
            _toolSchemas.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["input_schema"] = tool.InputSchema.DeepClone()
            });
        }
    }

    /// <summary>Se invoca en cada paso para que la consola muestre qué ocurre.</summary>
    public Action<string>? OnTrace { get; init; }

    public async Task<string> AskAsync(string systemPrompt, string question, CancellationToken ct = default)
    {
        // El historial crece con cada vuelta y se reenvía completo: la API no
        // guarda estado entre peticiones.
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = question }
        };

        var totalInputTokens = 0;
        var totalOutputTokens = 0;

        for (var iteration = 1; iteration <= MaxIterations; iteration++)
        {
            var request = new JsonObject
            {
                ["model"] = Model,
                ["max_tokens"] = MaxTokens,
                ["system"] = systemPrompt,
                ["messages"] = messages.DeepClone(),
                ["tools"] = _toolSchemas.DeepClone()
            };

            using var response = await _http.PostAsJsonAsync(Endpoint, request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"API {(int)response.StatusCode}: {body}");

            var parsed = JsonNode.Parse(body)!.AsObject();

            if (parsed["usage"] is JsonObject usage)
            {
                totalInputTokens += usage["input_tokens"]?.GetValue<int>() ?? 0;
                totalOutputTokens += usage["output_tokens"]?.GetValue<int>() ?? 0;
            }

            var stopReason = parsed["stop_reason"]?.GetValue<string>();
            var content = parsed["content"]?.AsArray() ?? [];

            // El turno del assistant se conserva tal cual: los bloques tool_use
            // deben viajar de vuelta en la siguiente petición.
            messages.Add(new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = content.DeepClone()
            });

            if (stopReason != "tool_use")
            {
                OnTrace?.Invoke($"Tokens: {totalInputTokens} entrada / {totalOutputTokens} salida " +
                                $"en {iteration} llamada(s)");
                return ExtractText(content);
            }

            // Cada tool_use necesita su tool_result, todos en un único mensaje
            // de rol "user".
            var results = new JsonArray();

            foreach (var block in content)
            {
                if (block?["type"]?.GetValue<string>() != "tool_use") continue;

                var toolUseId = block["id"]!.GetValue<string>();
                var toolName = block["name"]!.GetValue<string>();
                var toolInput = block["input"]?.AsObject() ?? [];

                OnTrace?.Invoke($"→ {toolName}({toolInput.ToJsonString()})");

                string resultText;
                var isError = false;

                try
                {
                    if (!_tools.TryGetValue(toolName, out var tool))
                        throw new InvalidOperationException($"Herramienta desconocida: {toolName}");

                    resultText = await tool.Execute(toolInput.DeepClone().AsObject(), ct);
                }
                catch (Exception ex)
                {
                    // Devolver el error al modelo en lugar de propagarlo le
                    // permite reaccionar: reintentar con otros argumentos o
                    // explicar al usuario qué ha fallado.
                    resultText = $"Error al ejecutar la herramienta: {ex.Message}";
                    isError = true;
                    OnTrace?.Invoke($"  ✗ {ex.Message}");
                }

                results.Add(new JsonObject
                {
                    ["type"] = "tool_result",
                    ["tool_use_id"] = toolUseId,
                    ["content"] = resultText,
                    ["is_error"] = isError
                });
            }

            messages.Add(new JsonObject { ["role"] = "user", ["content"] = results });
        }

        return $"Se alcanzó el límite de {MaxIterations} iteraciones sin respuesta final.";
    }

    private static string ExtractText(JsonArray content)
    {
        var parts = content
            .Where(b => b?["type"]?.GetValue<string>() == "text")
            .Select(b => b!["text"]!.GetValue<string>());

        return string.Join("\n", parts);
    }
}