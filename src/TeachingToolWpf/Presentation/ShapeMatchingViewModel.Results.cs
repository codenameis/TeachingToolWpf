using System.Collections.Immutable;

namespace TeachingToolWpf.Presentation;

public sealed partial class ShapeMatchingViewModel
{
    private string? _paperResultSummary;
    public ImmutableArray<string> ResultRows { get; private set; } = [];
    public bool HasResultRows => Method == PatternMatchingMethod.Shape && !ResultRows.IsEmpty;
    public string ResultSummary => HasResultRows ? _paperResultSummary ?? "" : Results;

    private void ResetResultRows()
    {
        ResultRows = [];
        _paperResultSummary = null;
    }

    private void NotifyResultRows()
    {
        Notify(nameof(ResultRows));
        Notify(nameof(HasResultRows));
        Notify(nameof(ResultSummary));
    }
}
