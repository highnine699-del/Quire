using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Quire.Domain;
using Microsoft.Extensions.Logging;

namespace Quire.Infrastructure.AI;

/// <summary>
/// Single implementation that covers any OpenAI-compatible /v1/chat/completions endpoint:
/// LM Studio, Ollama (OpenAI-compat mode), OpenAI, Anthropic via proxy, etc.
/// Only BaseUrl / Model / ApiKey differ between providers — nothing else in the app changes.
///
/// Error contract: this class throws on any failure (network, non-2xx, bad JSON).
/// The Application layer catches, logs, and surfaces Retry/Settings UI.
///
/// Settings are loaded once per call to GenerateConceptAsync and cached for
/// 30 seconds to avoid 21 disk reads during a cloud prefetch batch (7 days × 3 concepts).
/// </summary>
public sealed class OpenAiCompatibleProvider : IConceptProvider
{
    private readonly HttpClient _http;
    private readonly ISettingsStore _settingsStore;
    private readonly ILogger<OpenAiCompatibleProvider> _logger;

    // Settings cache — avoids a disk read on every concept generation call.
    // 30-second TTL: short enough that a Settings save mid-run is picked up quickly,
    // long enough to cover an entire 21-concept prefetch batch (which runs in < 60 s).
    private ProviderSettings? _cachedSettings;
    private DateTime _cacheExpiry = DateTime.MinValue;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public OpenAiCompatibleProvider(
        HttpClient http,
        ISettingsStore settingsStore,
        ILogger<OpenAiCompatibleProvider> logger)
    {
        _http          = http;
        _settingsStore = settingsStore;
        _logger        = logger;
    }

    private async Task<ProviderSettings> GetEffectiveSettingsAsync(CancellationToken cancellationToken)
    {
        // Return cached settings if still fresh
        if (_cachedSettings is not null && DateTime.UtcNow < _cacheExpiry)
            return _cachedSettings;

        var settings         = await _settingsStore.LoadAsync(cancellationToken);
        var providerSettings = settings.Mode == "cloud"
            ? settings.EffectiveCloudProvider
            : settings.Provider;

        _cachedSettings = providerSettings;
        _cacheExpiry    = DateTime.UtcNow + CacheTtl;

        _logger.LogInformation(
            "Provider resolved (cache miss): Mode={Mode}, BaseUrl={BaseUrl}, Model={Model}",
            settings.Mode, providerSettings.BaseUrl, providerSettings.Model);

        return providerSettings;
    }

    public async Task<Concept> GenerateConceptAsync(
        string category,
        IReadOnlyCollection<string> recentTitlesToAvoid,
        CancellationToken cancellationToken)
    {
        var settings = await GetEffectiveSettingsAsync(cancellationToken);

        var avoidClause = recentTitlesToAvoid.Count > 0
            ? $" Avoid repeating any of these previous titles: {string.Join(", ", recentTitlesToAvoid)}."
            : string.Empty;

        var prompt =
            $"Explain one specific, highly technical {category} concept in 5-8 sentences, " +
            $"in a way a curious beginner can follow. " +
            $"Respond ONLY as a JSON object with exactly two fields: " +
            $"{{\"title\": \"...\", \"explanation\": \"...\"}}.{avoidClause}";

        var request = new ChatRequest(
            Model: settings.Model,
            Messages: [new ChatMessage(Role: "user", Content: prompt)],
            Temperature: 0.8);

        var url = $"{settings.BaseUrl.TrimEnd('/')}/chat/completions";
        _logger.LogDebug("Requesting concept for category '{Category}' from {Url}", category, url);

        // Set Authorization header dynamically based on current settings
        var requestMessage = new HttpRequestMessage(HttpMethod.Post, url);
        requestMessage.Content = JsonContent.Create(request);

        if (!string.IsNullOrEmpty(settings.ApiKey))
            requestMessage.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", settings.ApiKey);

        // Add the Quire client secret header when calling the default shared proxy.
        // This protects the Groq quota from direct abuse — the Worker rejects requests
        // without this header. Only sent to the default proxy, never to user-configured
        // endpoints (advanced cloud override or local mode).
        if (settings.BaseUrl.Contains(AppSettings.DefaultProxyBaseUrl.Split('/')[2],
                StringComparison.OrdinalIgnoreCase))
        {
            requestMessage.Headers.TryAddWithoutValidation(
                "X-Quire-Client", AppSettings.QuireClientSecret);
        }

        var response = await _http.SendAsync(requestMessage, cancellationToken);

        // Detect shared-quota exhaustion (HTTP 429) before the generic EnsureSuccessStatusCode.
        // This gets a distinct exception type so callers can show a friendly "try tomorrow"
        // message instead of the generic error view — and log it separately from real failures.
        if ((int)response.StatusCode == 429)
        {
            _logger.LogWarning("HTTP 429 from {Url} — shared quota reached.", url);
            throw new QuotaExceededException(
                $"The cloud provider rate limit was reached (HTTP 429 from {settings.BaseUrl}). " +
                "Try again tomorrow, or switch to Local mode in Settings for unlimited use.");
        }

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<ChatResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Empty response body from AI provider.");

        if (payload.Choices is not { Length: > 0 })
            throw new InvalidOperationException("AI response contained no choices.");

        var content = payload.Choices[0].Message?.Content
            ?? throw new InvalidOperationException("AI response choice had no content.");

        var raw = content.Trim();

        // Strip markdown code fences if the model wraps the JSON in ```json ... ```
        if (raw.StartsWith("```"))
        {
            var firstNewline = raw.IndexOf('\n');
            var lastFence    = raw.LastIndexOf("```");
            if (firstNewline > 0 && lastFence > firstNewline)
                raw = raw[(firstNewline + 1)..lastFence].Trim();
        }

        var parsed = JsonSerializer.Deserialize<ConceptPayload>(raw, JsonOptions)
            ?? throw new InvalidOperationException("AI response could not be parsed as JSON.");

        if (string.IsNullOrWhiteSpace(parsed.Title) || string.IsNullOrWhiteSpace(parsed.Explanation))
            throw new InvalidOperationException(
                "AI response JSON was missing required 'title' or 'explanation' fields.");

        return new Concept(
            Title:       parsed.Title,
            Explanation: parsed.Explanation,
            Category:    category,
            GeneratedOn: DateOnly.FromDateTime(DateTime.Now));
    }

    // ── Private DTOs (never leak outside this class) ──────────────────────────

    private sealed record ChatRequest(
        [property: JsonPropertyName("model")]    string Model,
        [property: JsonPropertyName("messages")] ChatMessage[] Messages,
        [property: JsonPropertyName("temperature")] double Temperature);

    private sealed record ChatMessage(
        [property: JsonPropertyName("role")]    string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record ChatResponse(
        [property: JsonPropertyName("choices")] ChatChoice[] Choices);

    private sealed record ChatChoice(
        [property: JsonPropertyName("message")] ChatMessage Message);

    private sealed record ConceptPayload(
        [property: JsonPropertyName("title")]       string Title,
        [property: JsonPropertyName("explanation")] string Explanation);
}
