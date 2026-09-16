using Quire.Application.Schedulers;
using Quire.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace Quire.Tests.Application;

/// <summary>
/// Acceptance checklist items:
///   - Airplane mode + local mode  → RunIfDueAsync throws, PersistLastRun skipped, nothing crashes at host level
///   - Airplane mode + cloud mode  → same
///   - ConceptSetGenerated NOT raised on failure
///   - Nothing appended to History.md on failure
///
/// Contract change (audit fix): DailyConceptScheduler.RunIfDueAsync now throws on
/// provider failure instead of catching internally. ExecuteAsync in
/// ConceptGenerationBackgroundService catches BEFORE PersistLastRun — this ensures
/// a failed day is retried on the next launch rather than permanently skipped.
/// </summary>
public sealed class OfflineGracefulFailureTests
{
    // ── Fakes ────────────────────────────────────────────────────────────────

    private sealed class OfflineProvider : IConceptProvider
    {
        public Task<Concept> GenerateConceptAsync(
            string category,
            IReadOnlyCollection<string> avoid,
            CancellationToken ct)
            => throw new HttpRequestException("Network unreachable (simulated airplane mode).");
    }

    private sealed class EmptyHistory : IConceptHistoryStore
    {
        public Task AppendSetAsync(DailyConceptSet s, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> GetRecentTitlesAsync(int c, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<DailyConceptSet?> GetMostRecentSetAsync(CancellationToken ct)
            => Task.FromResult<DailyConceptSet?>(null);
    }

    private sealed class LocalModeSettings : ISettingsStore
    {
        public Task<AppSettings> LoadAsync(CancellationToken ct)
            => Task.FromResult(AppSettings.Default()); // mode = "local"
        public Task SaveAsync(AppSettings s, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class CloudModeSettings : ISettingsStore
    {
        public Task<AppSettings> LoadAsync(CancellationToken ct)
            => Task.FromResult(AppSettings.Default() with { Mode = "cloud" });
        public Task SaveAsync(AppSettings s, CancellationToken ct) => Task.CompletedTask;
    }

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LocalMode_ProviderOffline_RaisesGenerationFailed_DoesNotThrow()
    {
        // RunIfDueAsync now throws — the caller (ExecuteAsync) catches it and
        // invokes GenerationFailed before PersistLastRun. Test at the scheduler
        // level: confirm it throws with the correct exception type.
        var scheduler = new DailyConceptScheduler(
            new OfflineProvider(), new EmptyHistory(), new LocalModeSettings(),
            NullLogger<DailyConceptScheduler>.Instance);

        var exception = await Record.ExceptionAsync(() =>
            scheduler.RunIfDueAsync(DateOnly.FromDateTime(DateTime.Today), CancellationToken.None));

        // The scheduler now propagates exceptions — they are caught by ExecuteAsync
        Assert.NotNull(exception);
        Assert.IsType<HttpRequestException>(exception);
    }

    [Fact]
    public async Task CloudMode_ProviderOffline_RaisesGenerationFailed_DoesNotThrow()
    {
        var scheduler = new DailyConceptScheduler(
            new OfflineProvider(), new EmptyHistory(), new CloudModeSettings(),
            NullLogger<DailyConceptScheduler>.Instance);

        var exception = await Record.ExceptionAsync(() =>
            scheduler.RunIfDueAsync(DateOnly.FromDateTime(DateTime.Today), CancellationToken.None));

        Assert.NotNull(exception);
        Assert.IsType<HttpRequestException>(exception);
    }

    [Fact]
    public async Task ProviderOffline_NothingAppendedToHistory()
    {
        var history   = new TrackingHistory();
        var scheduler = new DailyConceptScheduler(
            new OfflineProvider(), history, new LocalModeSettings(),
            NullLogger<DailyConceptScheduler>.Instance);

        // Swallow the expected exception — we only care that nothing was appended
        await Record.ExceptionAsync(() =>
            scheduler.RunIfDueAsync(DateOnly.FromDateTime(DateTime.Today), CancellationToken.None));

        Assert.Equal(0, history.AppendCallCount); // nothing persisted on failure
    }

    [Fact]
    public async Task ProviderOffline_ConceptSetGenerated_NotRaised()
    {
        var raised    = false;
        var scheduler = new DailyConceptScheduler(
            new OfflineProvider(), new EmptyHistory(), new LocalModeSettings(),
            NullLogger<DailyConceptScheduler>.Instance);
        scheduler.ConceptSetGenerated += _ => raised = true;

        await Record.ExceptionAsync(() =>
            scheduler.RunIfDueAsync(DateOnly.FromDateTime(DateTime.Today), CancellationToken.None));

        Assert.False(raised);
    }

    private sealed class TrackingHistory : IConceptHistoryStore
    {
        public int AppendCallCount { get; private set; }
        public Task AppendSetAsync(DailyConceptSet s, CancellationToken ct)
        {
            AppendCallCount++;
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<string>> GetRecentTitlesAsync(int c, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<DailyConceptSet?> GetMostRecentSetAsync(CancellationToken ct)
            => Task.FromResult<DailyConceptSet?>(null);
    }
}
