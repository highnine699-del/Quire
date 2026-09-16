using Quire.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Quire.Application.Schedulers;

/// <summary>
/// BackgroundService that drives daily concept delivery for BOTH modes.
///
/// Local mode:
///   Calls DailyConceptScheduler.RunIfDueAsync exactly as before — generates
///   3 concepts on demand, appends to History.md.
///
/// Cloud mode:
///   1. On startup, asks CloudPrefetchService to fill the buffer to 7 days.
///   2. Each day, consumes the next buffered DailyConceptSet instead of calling
///      the AI API live. After consuming, CloudPrefetchService triggers a silent
///      background refill if the buffer drops below the threshold (3 days).
///   3. If the buffer is empty (internet was unavailable for too long), falls back
///      to the existing GenerationFailed / error-view behavior.
///
/// Uses a date-file check rather than a naive 24h timer — laptops sleep, timers drift.
/// </summary>
public class ConceptGenerationBackgroundService : BackgroundService
{
    private readonly DailyConceptScheduler  _scheduler;
    private readonly CloudPrefetchService   _prefetch;
    private readonly ISettingsStore         _settings;
    private readonly IConceptHistoryStore   _historyStore;
    private readonly ILogger<ConceptGenerationBackgroundService> _logger;

    // Raised for both modes when a set is ready — WidgetWindow subscribes to this
    public event Action<DailyConceptSet>? ConceptSetReady;
    public event Action<Exception>?       GenerationFailed;

    /// <summary>
    /// Raised when HTTP 429 is returned by the cloud provider (shared quota reached).
    /// Distinct from GenerationFailed — the UI shows a friendly "try tomorrow" message,
    /// not the generic error view, and the failure is not retried.
    /// </summary>
    public event Action? QuotaExceeded;

    private static readonly string LastRunPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Quire", "last_run.txt");

    public ConceptGenerationBackgroundService(
        DailyConceptScheduler  scheduler,
        CloudPrefetchService   prefetch,
        ISettingsStore         settings,
        IConceptHistoryStore   historyStore,
        ILogger<ConceptGenerationBackgroundService> logger)
    {
        _scheduler    = scheduler;
        _prefetch     = prefetch;
        _settings     = settings;
        _historyStore = historyStore;
        _logger       = logger;

        // Forward DailyConceptScheduler's ConceptSetGenerated (local mode).
        // GenerationFailed is no longer forwarded from the scheduler because
        // RunIfDueAsync now throws instead of catching — ExecuteAsync handles it.
        _scheduler.ConceptSetGenerated += set => ConceptSetReady?.Invoke(set);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var settings = await _settings.LoadAsync(stoppingToken);

            // Cloud mode: pre-fill the buffer on startup before the first daily check.
            // Quota and network failures here are non-fatal — the daily loop handles them.
            if (settings.Mode == "cloud")
            {
                _logger.LogInformation("Cloud mode: checking prefetch buffer on startup.");
                try
                {
                    await _prefetch.FillToTargetAsync(stoppingToken);
                }
                catch (QuotaExceededException)
                {
                    // Quota on startup prefetch is not a user-facing error — we may still have
                    // enough buffer for today's concept. Do NOT fire QuotaExceeded to the UI here;
                    // the daily consumption path will raise it if the buffer is truly empty.
                    _logger.LogWarning("Quota exceeded during startup prefetch — will use existing buffer or history.");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Startup prefetch failed — will continue with existing buffer.");
                }
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                var today = DateOnly.FromDateTime(DateTime.Now);

                if (!ShouldSkipToday())
                {
                    _logger.LogInformation("Daily generation due for {Today}.", today);

                    // Re-read settings each iteration — user may have switched mode
                    settings = await _settings.LoadAsync(stoppingToken);

                    try
                    {
                        if (settings.Mode == "cloud")
                            await RunCloudDayAsync(today, stoppingToken);
                        else
                            await _scheduler.RunIfDueAsync(today, stoppingToken);

                        PersistLastRun(today);
                    }
                    catch (QuotaExceededException)
                    {
                        _logger.LogWarning("Quota exceeded during daily generation for {Today}.", today);
                        QuotaExceeded?.Invoke();
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Daily generation failed for {Today}.", today);
                        GenerationFailed?.Invoke(ex);
                    }
                }
                else
                {
                    _logger.LogInformation("Already ran for {Today}. Loading today's concept set from history.", today);

                    var recentSet = await _historyStore.GetMostRecentSetAsync(stoppingToken);
                    if (recentSet is not null)
                    {
                        _logger.LogInformation("Loaded concept set for {Date} from history.", recentSet.Date);
                        ConceptSetReady?.Invoke(recentSet);
                    }
                    else
                    {
                        _logger.LogWarning("No concept set found in history for {Today}.", today);
                    }

                    _logger.LogInformation("Sleeping until tomorrow.");
                }

                // Sleep until 1 minute after next midnight
                var nextRun = DateTime.Today.AddDays(1).AddMinutes(1);
                var delay   = nextRun - DateTime.Now;
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown — not an error
        }
        catch (Exception ex)
        {
            // Last-resort catch — log and signal failure to UI rather than letting
            // the exception propagate to the host and stop the entire application.
            _logger.LogCritical(ex, "ConceptGenerationBackgroundService encountered an unhandled exception.");
            GenerationFailed?.Invoke(ex);
        }
    }

    // ── Cloud daily flow ──────────────────────────────────────────────────────

    private async Task RunCloudDayAsync(DateOnly today, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Cloud mode: consuming next buffered set for {Today}.", today);

        var set = await _prefetch.TryConsumeAsync(cancellationToken);

        if (set is not null)
        {
            _logger.LogInformation("Consumed buffered set for {Date}.", set.Date);

            // Persist to History.md so on restart the set can be reloaded without
            // touching the buffer or the network again.
            // Guard: only append if today's set is not already in history
            // (prevents double-append on ForceRetry when a prior run succeeded).
            await AppendIfNotAlreadyPersistedAsync(set, cancellationToken);

            ConceptSetReady?.Invoke(set);
        }
        else
        {
            // Buffer exhausted — attempt a live refill to distinguish quota vs. offline
            _logger.LogWarning("Buffer empty for {Today} — attempting live refill.", today);
            try
            {
                await _prefetch.FillToTargetAsync(cancellationToken);
                var freshSet = await _prefetch.TryConsumeAsync(cancellationToken);
                if (freshSet is not null)
                {
                    await AppendIfNotAlreadyPersistedAsync(freshSet, cancellationToken);
                    ConceptSetReady?.Invoke(freshSet);
                    return;
                }
            }
            catch (QuotaExceededException qex)
            {
                _logger.LogWarning(qex, "Shared cloud quota reached for {Today}.", today);
                // Throw a QuotaExceededException so ExecuteAsync's catch intercepts it
                // before PersistLastRun. The quota catch in ExecuteAsync fires QuotaExceeded
                // event and skips PersistLastRun — today will retry tomorrow.
                throw;
            }

            // Still empty after refill attempt and no quota signal → real failure.
            // THROW here instead of invoking GenerationFailed directly so that
            // ExecuteAsync's catch block handles it — that catch sits BEFORE
            // PersistLastRun, which must never run on a failed day.
            _logger.LogWarning("Cloud buffer exhausted and prefetch unavailable for {Today}.", today);
            throw new InvalidOperationException(
                "Cloud concept buffer is empty. Connect to the internet to refill.");
        }
    }

    /// <summary>
    /// Appends <paramref name="set"/> to History.md only if today's entry is not
    /// already present. Prevents duplicate history entries when ForceRetryAsync
    /// is called after a set was already successfully generated and persisted.
    /// </summary>
    private async Task AppendIfNotAlreadyPersistedAsync(
        DailyConceptSet set, CancellationToken cancellationToken)
    {
        var existing = await _historyStore.GetMostRecentSetAsync(cancellationToken);
        if (existing?.Date == set.Date)
        {
            _logger.LogDebug(
                "History already contains an entry for {Date} — skipping duplicate append.", set.Date);
            return;
        }
        await _historyStore.AppendSetAsync(set, cancellationToken);
    }

    // ── Date persistence ──────────────────────────────────────────────────────

    /// <summary>
    /// Returns true when the daily generation has already run for today.
    /// Protected virtual so tests can override it to force execution.
    /// </summary>
    protected virtual bool ShouldSkipToday()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        return AlreadyRanToday(today);
    }

    /// <summary>
    /// Explicitly re-runs the daily generation for today, bypassing the
    /// <see cref="ShouldSkipToday"/> guard. Call this only from error-retry UI
    /// paths — never from the normal daily schedule loop.
    /// </summary>
    public async Task ForceRetryAsync(CancellationToken cancellationToken)
    {
        var settings = await _settings.LoadAsync(cancellationToken);
        var today    = DateOnly.FromDateTime(DateTime.Now);

        _logger.LogInformation(
            "ForceRetry: re-running generation for {Today} on user request.", today);

        if (settings.Mode == "cloud")
            await RunCloudDayAsync(today, cancellationToken);
        else
            await _scheduler.RunIfDueAsync(today, cancellationToken);
    }

    private static bool AlreadyRanToday(DateOnly today)
    {
        if (!File.Exists(LastRunPath)) return false;
        var raw = File.ReadAllText(LastRunPath).Trim();
        return DateOnly.TryParse(raw, out var last) && last == today;
    }

    private static void PersistLastRun(DateOnly date)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LastRunPath)!);
        File.WriteAllText(LastRunPath, date.ToString("yyyy-MM-dd"));
    }
}
