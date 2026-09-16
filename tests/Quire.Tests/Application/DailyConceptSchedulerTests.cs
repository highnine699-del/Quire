using Quire.Application.Schedulers;
using Quire.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace Quire.Tests.Application;

/// <summary>
/// Scheduler: generates exactly 3 distinct concepts, accumulates avoid-list correctly,
/// raises ConceptSetGenerated, and appends via history store.
/// Uses in-memory fakes — no I/O, no network.
/// </summary>
public sealed class DailyConceptSchedulerTests
{
    // ── Fakes ────────────────────────────────────────────────────────────────

    private sealed class FakeProvider : IConceptProvider
    {
        public List<IReadOnlyCollection<string>> ReceivedAvoidLists { get; } = [];
        public List<string> ReceivedCategories { get; } = [];
        private int _call;

        public Task<Concept> GenerateConceptAsync(
            string category,
            IReadOnlyCollection<string> recentTitlesToAvoid,
            CancellationToken cancellationToken)
        {
            ReceivedCategories.Add(category);
            ReceivedAvoidLists.Add(recentTitlesToAvoid.ToList());
            var concept = new Concept(
                $"Concept {++_call}",
                "Explanation",
                category,
                DateOnly.FromDateTime(DateTime.Today));
            return Task.FromResult(concept);
        }
    }

    private sealed class FakeHistory : IConceptHistoryStore
    {
        public List<DailyConceptSet> AppendedSets { get; } = [];

        public Task AppendSetAsync(DailyConceptSet set, CancellationToken ct)
        {
            AppendedSets.Add(set);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> GetRecentTitlesAsync(int count, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>(["OldTitle1", "OldTitle2"]);

        public Task<DailyConceptSet?> GetMostRecentSetAsync(CancellationToken ct)
            => Task.FromResult(AppendedSets.LastOrDefault());
    }

    private sealed class FakeSettings : ISettingsStore
    {
        public Task<AppSettings> LoadAsync(CancellationToken ct)
            => Task.FromResult(AppSettings.Default());

        public Task SaveAsync(AppSettings settings, CancellationToken ct)
            => Task.CompletedTask;
    }

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunIfDueAsync_GeneratesExactlyThreeConcepts()
    {
        var provider  = new FakeProvider();
        var history   = new FakeHistory();
        var scheduler = new DailyConceptScheduler(
            provider, history, new FakeSettings(),
            NullLogger<DailyConceptScheduler>.Instance);

        await scheduler.RunIfDueAsync(
            DateOnly.FromDateTime(DateTime.Today), CancellationToken.None);

        Assert.Equal(3, provider.ReceivedCategories.Count);
    }

    [Fact]
    public async Task RunIfDueAsync_AccumulatesAvoidListAcrossThreeCalls()
    {
        var provider  = new FakeProvider();
        var history   = new FakeHistory();
        var scheduler = new DailyConceptScheduler(
            provider, history, new FakeSettings(),
            NullLogger<DailyConceptScheduler>.Instance);

        await scheduler.RunIfDueAsync(
            DateOnly.FromDateTime(DateTime.Today), CancellationToken.None);

        // Call 1: has the 2 history titles only (no intra-day titles yet)
        Assert.Equal(2, provider.ReceivedAvoidLists[0].Count);

        // Call 2: history + concept-1's title
        Assert.Equal(3, provider.ReceivedAvoidLists[1].Count);
        Assert.Contains("Concept 1", provider.ReceivedAvoidLists[1]);

        // Call 3: history + concept-1 + concept-2
        Assert.Equal(4, provider.ReceivedAvoidLists[2].Count);
        Assert.Contains("Concept 2", provider.ReceivedAvoidLists[2]);
    }

    [Fact]
    public async Task RunIfDueAsync_RaisesConceptSetGenerated()
    {
        var provider  = new FakeProvider();
        var history   = new FakeHistory();
        DailyConceptSet? raised = null;
        var scheduler = new DailyConceptScheduler(
            provider, history, new FakeSettings(),
            NullLogger<DailyConceptScheduler>.Instance);
        scheduler.ConceptSetGenerated += s => raised = s;

        await scheduler.RunIfDueAsync(
            DateOnly.FromDateTime(DateTime.Today), CancellationToken.None);

        Assert.NotNull(raised);
        Assert.Equal(3, raised!.Count);
    }

    [Fact]
    public async Task RunIfDueAsync_AppendsSetToHistory()
    {
        var provider  = new FakeProvider();
        var history   = new FakeHistory();
        var scheduler = new DailyConceptScheduler(
            provider, history, new FakeSettings(),
            NullLogger<DailyConceptScheduler>.Instance);

        await scheduler.RunIfDueAsync(
            DateOnly.FromDateTime(DateTime.Today), CancellationToken.None);

        Assert.Single(history.AppendedSets);
        Assert.Equal(3, history.AppendedSets[0].Count);
    }

    [Fact]
    public async Task RunIfDueAsync_ThrowsOnProviderException()
    {
        var brokenProvider = new BrokenProvider();
        var history        = new FakeHistory();
        var scheduler = new DailyConceptScheduler(
            brokenProvider, history, new FakeSettings(),
            NullLogger<DailyConceptScheduler>.Instance);

        // RunIfDueAsync now throws — callers (ExecuteAsync) catch before PersistLastRun.
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            scheduler.RunIfDueAsync(
                DateOnly.FromDateTime(DateTime.Today), CancellationToken.None));

        Assert.Empty(history.AppendedSets); // nothing persisted on failure
    }

    private sealed class BrokenProvider : IConceptProvider
    {
        public Task<Concept> GenerateConceptAsync(
            string category, IReadOnlyCollection<string> avoid, CancellationToken ct)
            => throw new HttpRequestException("Endpoint unreachable");
    }
}
