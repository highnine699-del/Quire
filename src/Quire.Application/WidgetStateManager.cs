using Quire.Domain;

namespace Quire.Application;

/// <summary>
/// Enforces the widget state machine. Only the six legal transitions are allowed;
/// any other trigger/state combination is silently ignored — never thrown.
///
/// Legal transitions:
///   Compact  + Click        → Expanded
///   Expanded + OutsideClick → Compact
///   Expanded + Timeout      → Compact
///   Expanded + Pin          → Pinned
///   Pinned   + Unpin        → Expanded  (returns to expanded view, not compact)
///   Pinned   + OutsideClick → Compact   (deactivation while pinned collapses fully)
/// </summary>
public sealed class WidgetStateManager
{
    public WidgetState Current { get; private set; } = WidgetState.Compact;

    /// <summary>Raised after every successful state change, with the new state.</summary>
    public event Action<WidgetState>? StateChanged;

    public void Fire(WidgetTrigger trigger)
    {
        var next = (Current, trigger) switch
        {
            (WidgetState.Compact,  WidgetTrigger.Click)        => WidgetState.Expanded,
            (WidgetState.Expanded, WidgetTrigger.OutsideClick) => WidgetState.Compact,
            (WidgetState.Expanded, WidgetTrigger.Timeout)      => WidgetState.Compact,
            (WidgetState.Expanded, WidgetTrigger.Pin)          => WidgetState.Pinned,
            // Unpin returns to Expanded (the view is still open) so the timer restarts
            // and the user can keep reading. Use OutsideClick/Timeout to collapse fully.
            (WidgetState.Pinned,   WidgetTrigger.Unpin)        => WidgetState.Expanded,
            // Losing focus while pinned collapses to Compact (e.g. clicking away).
            (WidgetState.Pinned,   WidgetTrigger.OutsideClick) => WidgetState.Compact,
            _                                                  => Current  // illegal — ignored
        };

        if (next == Current) return;
        Current = next;
        StateChanged?.Invoke(Current);
    }
}
