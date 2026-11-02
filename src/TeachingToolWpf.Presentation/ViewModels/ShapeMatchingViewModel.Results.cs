using System.Collections.Immutable;

namespace TeachingToolWpf.ViewModels;

public sealed partial class ShapeMatchingViewModel
{
    private string? _resultSummary;
    public ImmutableArray<string> ResultRows { get; private set; } = [];
    public bool HasResultRows => !ResultRows.IsEmpty;
    public string ResultSummary => HasResultRows ? _resultSummary ?? "" : Results;

    private void ResetResultRows()
    {
        ResultRows = [];
        _resultSummary = null;
        _inspectionNotice = null;
    }

    private void SetResultDetails(string summary, IEnumerable<string> rows)
    {
        _resultSummary = summary;
        ResultRows = rows.ToImmutableArray();
        Results = summary + "\n\n" + string.Join("\n\n", ResultRows);
    }

    private void NotifyResultRows()
    {
        Notify(nameof(ResultRows));
        Notify(nameof(HasResultRows));
        Notify(nameof(ResultSummary));
    }
}
