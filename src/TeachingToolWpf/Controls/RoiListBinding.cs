using System.Windows.Controls;
using System.Windows.Input;
using TeachingToolWpf.Presentation;

namespace TeachingToolWpf.Controls;

/// <summary>Adapts WPF's SelectedItems collection without putting selection rules in the view.</summary>
internal sealed class RoiListBinding : IDisposable
{
    private readonly RoiListBox _list;
    private readonly EditorViewModel _model;
    private bool _synchronizing;
    private object? _previousSelection;
    private object? _previousRepeatedSelection;
    private RoiRows? _previousRows;

    public RoiListBinding(RoiListBox list, EditorViewModel model)
    {
        _list = list;
        _model = model;
        list.ShowPage(model.VisibleRows);
        list.SelectionChanged += FromView;
        model.Session.Changed += FromModel;
        model.RowPageChanged += Synchronize;
        list.SingleSelectionRequested += SelectSingle;
    }

    private void SelectSingle(RoiRow row)
    {
        if (!_synchronizing)
        {
            _model.SelectRows([row.Id]);
        }
    }

    private void FromView(object sender, SelectionChangedEventArgs args)
    {
        if (_synchronizing)
        {
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            _model.SelectRows(_model.Session.State.Selection.Concat(_model.Session.State.RepeatedSelection.Keys).Except(args.RemovedItems.Cast<object>().Select(RoiListBox.RowFromItem).OfType<RoiRow>().Select(row => row.Id))
                .Union(args.AddedItems.Cast<object>().Select(RoiListBox.RowFromItem).OfType<RoiRow>().Select(row => row.Id)), preserveRepeatedRanges: true);
        }
        else
        {
            _model.SelectRows(_list.SelectedItems.Cast<object>().Select(RoiListBox.RowFromItem).OfType<RoiRow>().Select(row => row.Id));
        }
    }

    private void FromModel(object? sender, EventArgs args) => Synchronize();

    private void Synchronize()
    {
        var selected = _model.Session.State.Selection;
        var repeated = _model.Session.State.RepeatedSelection;
        if (ReferenceEquals(_previousSelection, selected) && ReferenceEquals(_previousRepeatedSelection, repeated) && ReferenceEquals(_previousRows, _model.VisibleRows))
        {
            return;
        }

        _synchronizing = true;
        try
        {
            _list.ShowPage(_model.VisibleRows);
            _list.ApplySelection(!repeated.IsEmpty ? _model.VisibleRows.Where(row => selected.Contains(row.Id) || repeated.ContainsKey(row.Id)) : selected.Count < _model.VisibleRows.Count
                ? selected.Select(_model.VisibleRows.Find).OfType<RoiRow>()
                : _model.VisibleRows.Where(row => selected.Contains(row.Id)));
            _previousSelection = selected;
            _previousRepeatedSelection = repeated;
            _previousRows = _model.VisibleRows;
        }
        finally
        {
            _synchronizing = false;
        }
    }

    public void Dispose()
    {
        _list.SelectionChanged -= FromView;
        _model.Session.Changed -= FromModel;
        _model.RowPageChanged -= Synchronize;
        _list.SingleSelectionRequested -= SelectSingle;
    }
}
