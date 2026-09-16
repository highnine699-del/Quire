using Quire.Domain;
using Microsoft.Extensions.Logging;

namespace Quire.Application.Schedulers;

/// <summary>
/// One job: generate today's DailyConceptSet (exactly 3 concepts) once per calendar day.
///
/// Calls IConceptProvider 3 times, accumulating the avoid-list between calls so all three
/// concepts within the same day are distinct from each other and from history.
/// </summary>
public sealed class DailyConceptScheduler
{
    private readonly IConceptProvider _provider;
    private readonly IConceptHistoryStore _history;
    private readonly ISettingsStore _settings;
    private readonly ILogger<DailyConceptScheduler> _logger;

    /// <summary>Raised when a full DailyConceptSet has been generated and persisted.</summary>
    public event Action<DailyConceptSet>? ConceptSetGenerated;

    public DailyConceptScheduler(
        IConceptProvider provider,
        IConceptHistoryStore history,
        ISettingsStore settings,
        ILogger<DailyConceptScheduler> logger)
    {
        _provider = provider;
        _history  = history;
        _settings = settings;
        _logger   = logger;
    }

    /// <summary>
    /// Generates three concepts for <paramref name="today"/> if not already done.
    /// Called by <see cref="ConceptGenerationBackgroundService"/> on startup and daily check.
    /// </summary>
    public async Task RunIfDueAsync(DateOnly today, CancellationToken cancellationToken)
    {
        var settings = await _settings.LoadAsync(cancellationToken);
        var category = settings.Topics.CategoryFor(today.DayOfWeek);

        var avoidList = new List<string>(
            await _history.GetRecentTitlesAsync(30, cancellationToken));

        _logger.LogInformation(
            "Generating DailyConceptSet for {Date} ({Category}). Avoiding {Count} recent titles.",
            today, category, avoidList.Count);

        // No try/catch here — exceptions propagate to ExecuteAsync's catch block,
        // which sits BEFORE PersistLastRun. This ensures a failed day is never
        // marked as "already ran" and will be retried on the next launch.
        var concepts = new List<Concept>(3);
        for (var i = 0; i < 3; i++)
        {
            var concept = await _provider.GenerateConceptAsync(
                category, avoidList, cancellationToken);

            concepts.Add(concept);
            avoidList.Add(concept.Title);
            _logger.LogInformation("  [{Slot}/3] Generated: {Title}", i + 1, concept.Title);
        }

        var set = new DailyConceptSet(today, concepts.AsReadOnly());
        await _history.AppendSetAsync(set, cancellationToken);

        ConceptSetGenerated?.Invoke(set);
    }
}
