using Quire.Application;
using Quire.Application.Schedulers;
using Quire.Domain;
using Quire.Infrastructure.AI;
using Quire.Infrastructure.Storage;
using Quire.UI.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace Quire.UI;

public partial class App : System.Windows.Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            await StartupCoreAsync();
        }
        catch (Exception ex)
        {
            // Unhandled startup failure — show a message before the process dies.
            // async void would otherwise swallow this and crash silently.
            Log.Fatal(ex, "Fatal error during startup.");
            Log.CloseAndFlush();
            MessageBox.Show(
                $"Quire failed to start:\n\n{ex.Message}\n\nSee logs for details.",
                "Quire — Startup Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private async Task StartupCoreAsync()
    {
        var processStart = Stopwatch.GetTimestamp();

        // ── Serilog rolling file logger ───────────────────────────────────────
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Quire", "Logs", "log-.txt");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                logPath,
                rollingInterval:        RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
                encoding:               System.Text.Encoding.UTF8)
            .CreateLogger();

        // ── Load settings before DI graph build ───────────────────────────────
        var settingsStore = new JsonSettingsStore(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<JsonSettingsStore>.Instance);

        // ── Build DI host ─────────────────────────────────────────────────────
        _host = Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureServices((_, services) =>
            {
                // Prevent a BackgroundService exception from stopping the entire host.
                // All exceptions in ExecuteAsync are caught and surfaced to the UI via events.
                services.Configure<HostOptions>(opts =>
                    opts.BackgroundServiceExceptionBehavior =
                        BackgroundServiceExceptionBehavior.Ignore);
                // ── Infrastructure ────────────────────────────────────────────
                services.AddSingleton<ISettingsStore>(_ => settingsStore);
                services.AddSingleton<IConceptHistoryStore, MarkdownHistoryStore>();
                services.AddSingleton<IConceptBufferStore, JsonConceptBufferStore>();

                // AI provider — generic OpenAI-compatible endpoint.
                // 45s timeout: covers slow local LM Studio cold-starts without letting
                // a stalled server block the background service indefinitely.
                services.AddHttpClient<IConceptProvider, OpenAiCompatibleProvider>(client =>
                {
                    client.Timeout = TimeSpan.FromSeconds(45);
                });

                // Model download service (local first-run)
                services.AddHttpClient<ModelDownloadService>();

                // Update checker with GitHub API User-Agent header
                services.AddHttpClient("GitHubUpdate", client =>
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("Quire-UpdateChecker/1.0");
                });

                // Download client for update installer — pre-configured with 10-minute timeout
                // so RefreshScheduler.PerformUpdateAsync never sets Timeout on a live client
                services.AddHttpClient("UpdateDownload", client =>
                {
                    client.Timeout = TimeSpan.FromMinutes(10);
                });

                // Lightweight connectivity probe client for CloudPrefetchService (#8 audit fix)
                // Short 5s timeout — only needs a HEAD response, not a body.
                services.AddHttpClient("ConnectivityProbe", client =>
                {
                    client.Timeout = TimeSpan.FromSeconds(5);
                });

                // ── Application ───────────────────────────────────────────────
                services.AddSingleton<WidgetStateManager>();
                services.AddSingleton<DailyConceptScheduler>();
                services.AddSingleton<CloudPrefetchService>();
                services.AddSingleton<RotationScheduler>();

                // BackgroundService: drives daily delivery for both modes.
                // Register as an explicit singleton FIRST — same pattern as RefreshScheduler —
                // so the same instance is both hosted AND injectable into WidgetWindow.
                services.AddSingleton<ConceptGenerationBackgroundService>();
                services.AddHostedService(sp => sp.GetRequiredService<ConceptGenerationBackgroundService>());

                // Auto-update check (24 h cadence, raises event for user consent).
                // Register as an explicit singleton FIRST so the same instance is used
                // both as a hosted service and as the direct injection into WidgetWindow.
                // AddHostedService<T> alone creates an internal instance that can't be
                // retrieved via GetRequiredService<RefreshScheduler>() — this pattern fixes that.
                services.AddSingleton<RefreshScheduler>();
                services.AddHostedService(sp => sp.GetRequiredService<RefreshScheduler>());

                // ── UI ────────────────────────────────────────────────────────
                services.AddSingleton<WidgetWindow>();
                services.AddTransient<SettingsWindow>();

                // Factory for SettingsWindow — avoids injecting IServiceProvider into WidgetWindow
                services.AddSingleton<Func<SettingsWindow>>(
                    sp => () => sp.GetRequiredService<SettingsWindow>());
            })
            .Build();

        // ── Resolve services BEFORE StartAsync ────────────────────────────────
        // Wiring events before StartAsync guarantees no delivery can be missed —
        // background threads haven't started yet when we attach the handlers.
        var bgService = _host.Services
            .GetRequiredService<ConceptGenerationBackgroundService>();

        var refreshScheduler = _host.Services
            .GetRequiredService<RefreshScheduler>();

        var window = _host.Services.GetRequiredService<WidgetWindow>();

        // Wire concept delivery events before the host starts
        bgService.ConceptSetReady  += set => window.OnConceptSetReady(set);
        bgService.GenerationFailed += ex  => window.OnGenerationFailed(ex);
        bgService.QuotaExceeded    += ()  => window.OnQuotaExceeded();

        // Wire update-available event so the UI shows consent banner, not silent install
        refreshScheduler.UpdateAvailable += (ver, url) => window.OnUpdateAvailable(ver, url);

        // ── Now safe to start — all subscriptions are in place ────────────────
        await _host.StartAsync();

        var elapsedMs = Stopwatch.GetElapsedTime(processStart).TotalMilliseconds;
        Log.Information("Host started in {ElapsedMs:F1} ms.", elapsedMs);

        MainWindow = window;
        window.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_host is not null)
            {
                await _host.StopAsync();
                _host.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during host shutdown.");
        }
        finally
        {
            Log.CloseAndFlush();
            base.OnExit(e);
        }
    }
}
