namespace Quire.Domain;

public interface ISettingsStore
{
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken);
}

/// <summary>
/// Full application settings. Validated on load — corrupt JSON falls back to Default().
///
/// IsFirstRun:            true until the user completes the setup-time mode choice screen.
/// HasSeenTrayHint:       true once the first-run "× closes to tray" banner has been dismissed.
/// AdvancedCloudProvider: null = use the default shared proxy (zero setup for users).
///                        non-null = user has chosen to override with their own provider key.
/// </summary>
public sealed record AppSettings(
    string Mode,                            // "local" | "cloud"
    string Theme,
    bool IsFirstRun,                        // true = setup choice not yet made
    bool HasSeenTrayHint,                   // false = tray-hint banner not yet shown
    ProviderSettings Provider,              // local AI endpoint
    ProviderSettings CloudProvider,         // default shared proxy (no user key required)
    ProviderSettings? AdvancedCloudProvider, // optional: user's own key/endpoint override
    WeekdayTopicMap Topics,
    WindowPosition? WidgetPosition,         // saved widget position (null = use default)
    double WidgetOpacity,                   // 0.4 to 1.0, default 1.0 (fully opaque)
    bool PinBehindDesktopIcons)             // experimental WorkerW mode, default false
{
    /// <summary>
    /// The Groq proxy base URL.
    /// This is a Cloudflare Worker that forwards to Groq with a server-held key.
    /// Users never see or enter an API key for the default cloud experience.
    /// Shared quota is owned by the app developers, not each individual user.
    /// </summary>
    public const string DefaultProxyBaseUrl = "https://groqapikey.highnine699.workers.dev/v1";

    /// <summary>
    /// Client secret sent as X-Quire-Client header to the default proxy.
    /// Value is injected at build time — see Directory.Build.props + secrets.props.
    /// Protects the shared Groq quota from direct abuse of the proxy URL.
    /// Only sent to the default proxy — never to user-configured endpoints.
    /// </summary>
    public const string QuireClientSecret = BuildConstants.ClientSecret;

    /// <summary>
    /// Returns the effective cloud ProviderSettings to use at runtime.
    /// If the user has set an AdvancedCloudProvider, that takes priority.
    /// Otherwise the default shared proxy is used (no key needed).
    /// </summary>
    public ProviderSettings EffectiveCloudProvider =>
        AdvancedCloudProvider is { ApiKey: { Length: > 0 } }
            ? AdvancedCloudProvider
            : CloudProvider;

    public static AppSettings Default() => new(
        Mode:            "local",
        Theme:           "dark",
        IsFirstRun:      true,
        HasSeenTrayHint: false,
        Provider: new ProviderSettings(
            BaseUrl: "http://localhost:1234/v1",
            Model:   "phi-3-mini",
            ApiKey:  null),
        // Default cloud: shared Groq proxy via Cloudflare Worker.
        // Free for every user — no API key, no billing, no signup required.
        // Uses openai/gpt-oss-120b — a plain non-agentic model on Groq's free tier.
        CloudProvider: new ProviderSettings(
            BaseUrl: DefaultProxyBaseUrl,
            Model:   "openai/gpt-oss-120b",
            ApiKey:  null),
        AdvancedCloudProvider: null,
        Topics: new WeekdayTopicMap(new Dictionary<DayOfWeek, string>
        {
            [DayOfWeek.Monday]    = "Programming",
            [DayOfWeek.Tuesday]   = "Cybersecurity",
            [DayOfWeek.Wednesday] = "Networking",
            [DayOfWeek.Thursday]  = "AI",
            [DayOfWeek.Friday]    = "Operating Systems",
            [DayOfWeek.Saturday]  = "Mathematics",
            [DayOfWeek.Sunday]    = "Computer Engineering",
        }),
        WidgetPosition: null,           // null = use default top-right position
        WidgetOpacity: 1.0,             // fully opaque by default
        PinBehindDesktopIcons: false);   // experimental feature off by default
}

public sealed record ProviderSettings(
    string BaseUrl,
    string Model,
    string? ApiKey);

public sealed record WindowPosition(
    double Left,
    double Top);
