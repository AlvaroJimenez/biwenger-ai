using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Biwenger.Ingest;

/// <summary>
/// Configuración del cliente autenticado. Los valores viven en user-secrets,
/// nunca en appsettings.json ni en el repo.
/// </summary>
public sealed class BiwengerAuthOptions
{
    public required string Email { get; init; }
    public required string Password { get; init; }
    public required long LeagueId { get; init; }
    public required long UserId { get; init; }

    /// <summary>Valor de la cabecera X-Version que espera el backend.</summary>
    public string Version { get; init; } = "628";

    /// <summary>
    /// El JWT de Biwenger no trae claim "exp", así que no hay caducidad declarada.
    /// Tratamos el token como viejo pasado este tiempo desde su "iat".
    /// </summary>
    public TimeSpan TokenMaxAge { get; init; } = TimeSpan.FromDays(7);
}

public sealed record LoginResponse(string Token);

/// <summary>
/// Cliente para los endpoints autenticados de Biwenger (tu cuenta, tu liga).
/// Deliberadamente separado de BiwengerClient: aquel consume un CDN público y
/// tolera llamadas frecuentes; este actúa como tu sesión y debe ser conservador.
///
/// SOLO LECTURA. Este cliente no ejecuta fichajes, ventas ni alineaciones.
/// </summary>
public sealed class BiwengerAuthClient : IDisposable
{
    private const string BaseUrl = "https://biwenger.as.com";

    private readonly HttpClient _http;
    private readonly BiwengerAuthOptions _options;
    private readonly SemaphoreSlim _loginLock = new(1, 1);

    private string? _token;
    private DateTimeOffset _tokenIssuedAt;

    public BiwengerAuthClient(HttpClient http, BiwengerAuthOptions options)
    {
        _http = http;
        _options = options;
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    // ---------------------------------------------------------------
    // Endpoints
    // ---------------------------------------------------------------

    /// <summary>Datos de la cuenta: usuario, ligas a las que perteneces.</summary>
    public Task<string> GetAccountRawAsync(CancellationToken ct = default)
        => GetRawAsync("/api/v2/account", ct);

    /// <summary>
    /// Tu equipo en la liga: jugadores, saldo, valor de plantilla.
    /// OJO: la ruta exacta y los "fields" hay que confirmarlos en DevTools
    /// antes de dar por buena la respuesta.
    /// </summary>
    public Task<string> GetSquadRawAsync(CancellationToken ct = default)
        => GetRawAsync("/api/v2/user?fields=*,players(id,owner,price)", ct);

    /// <summary>Mercado actual de la liga (jugadores en venta y sus pujas).</summary>
    public Task<string> GetMarketRawAsync(CancellationToken ct = default)
        => GetRawAsync("/api/v2/market", ct);

    public async Task<UserResponse> GetSquadAsync(CancellationToken ct = default)
    {
        var json = await GetSquadRawAsync(ct);
        return JsonSerializer.Deserialize<UserResponse>(json)
               ?? throw new InvalidOperationException("Respuesta de plantilla vacía.");
    }

    // ---------------------------------------------------------------
    // Fontanería HTTP
    // ---------------------------------------------------------------

    private async Task<string> GetRawAsync(string path, CancellationToken ct)
    {
        var response = await SendAuthenticatedAsync(path, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>
    /// Envía la petición con el token cacheado. Si el backend responde 401,
    /// fuerza un login nuevo y reintenta UNA vez: sin claim "exp" no hay forma
    /// fiable de anticipar la caducidad, así que el 401 es la señal real.
    /// </summary>
    private async Task<HttpResponseMessage> SendAuthenticatedAsync(string path, CancellationToken ct)
    {
        var token = await EnsureTokenAsync(forceRefresh: false, ct);
        var response = await SendOnceAsync(path, token, ct);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            token = await EnsureTokenAsync(forceRefresh: true, ct);
            response = await SendOnceAsync(path, token, ct);
        }

        response.EnsureSuccessStatusCode();
        return response;
    }

    private Task<HttpResponseMessage> SendOnceAsync(string path, string token, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + path);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        request.Headers.TryAddWithoutValidation("X-League", _options.LeagueId.ToString());
        request.Headers.TryAddWithoutValidation("X-User", _options.UserId.ToString());
        request.Headers.TryAddWithoutValidation("X-Version", _options.Version);
        return _http.SendAsync(request, ct);
    }

    // ---------------------------------------------------------------
    // Token
    // ---------------------------------------------------------------

    private async Task<string> EnsureTokenAsync(bool forceRefresh, CancellationToken ct)
    {
        if (!forceRefresh && _token is not null && !IsStale())
            return _token;

        await _loginLock.WaitAsync(ct);
        try
        {
            // Otro hilo pudo renovarlo mientras esperábamos el semáforo.
            if (!forceRefresh && _token is not null && !IsStale())
                return _token;

            _token = await LoginAsync(ct);
            _tokenIssuedAt = ReadIssuedAt(_token) ?? DateTimeOffset.UtcNow;
            return _token;
        }
        finally
        {
            _loginLock.Release();
        }
    }

    private bool IsStale()
        => DateTimeOffset.UtcNow - _tokenIssuedAt > _options.TokenMaxAge;

    private async Task<string> LoginAsync(CancellationToken ct)
    {
        var payload = new { email = _options.Email, password = _options.Password };

        using var response = await _http.PostAsJsonAsync($"{BaseUrl}/api/v2/auth/login", payload, ct);
        response.EnsureSuccessStatusCode();

        var login = await response.Content.ReadFromJsonAsync<LoginResponse>(cancellationToken: ct);

        if (string.IsNullOrWhiteSpace(login?.Token))
            throw new InvalidOperationException("El login de Biwenger no devolvió token.");

        return login.Token;
    }

    /// <summary>
    /// Extrae el claim "iat" del JWT sin validar firma: no somos el emisor,
    /// solo queremos saber la edad del token para decidir cuándo renovarlo.
    /// </summary>
    internal static DateTimeOffset? ReadIssuedAt(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2) return null;

        try
        {
            var json = Base64UrlDecode(parts[1]);
            using var doc = JsonDocument.Parse(json);

            return doc.RootElement.TryGetProperty("iat", out var iat) && iat.TryGetInt64(out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Extrae el claim "iss", que en Biwenger es el id de usuario.</summary>
    internal static long? ReadUserId(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2) return null;

        try
        {
            using var doc = JsonDocument.Parse(Base64UrlDecode(parts[1]));
            return doc.RootElement.TryGetProperty("iss", out var iss) && iss.TryGetInt64(out var id)
                ? id
                : null;
        }
        catch (Exception e) when (e is JsonException or FormatException)
        {
            return null;
        }
    }

    private static byte[] Base64UrlDecode(string input)
    {
        var padded = input.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch
        {
            2 => padded + "==",
            3 => padded + "=",
            _ => padded
        };
        return Convert.FromBase64String(padded);
    }

    public void Dispose() => _loginLock.Dispose();
}