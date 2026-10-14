using System.Windows.Controls;
using System.Windows.Input;
using TeachingToolWpf.Models;
using TeachingToolWpf.ViewModels;

namespace TeachingToolWpf.Controls;

/// <summary>Adapts WPF's SelectedItems collection without putting selection rules in the view.</summary>
internal sealed class RoiListSelectionLink : IDisposable
{
    private readonly RoiListBox _roiList;
    private readonly MainWindowViewModel _editor;
    private bool _isSelectionSyncActive;
    private object? _previousSelection;
    private object? _previousRepeatedSelection;
    private RoiRows? _previousRows;

    public RoiListSelectionLink(RoiListBox list, MainWindowViewModel model)
    {
        _roiList = list;
        _editor = model;
        list.ShowPage(model.VisibleRows);
        list.SelectionChanged += FromView;
        model.Session.Changed += FromModel;
        model.RowPageChanged += Synchronize;
        list.SingleSelectionRequested += SelectSingle;
    }

    private void SelectSingle(RoiRow row)
    {
        if (!_isSelectionSyncActive)
        {
            _editor.SelectRows([row.Id]);
        }
    }

    private void FromView(object sender, SelectionChangedEventArgs args)
    {
        if (_isSelectionSyncActive)
        {
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            _editor.SelectRows(_editor.Session.State.Selection.Concat(_editor.Session.State.RepeatedSelection.Keys).Except(args.RemovedItems.Cast<object>().Select(RoiListBox.RowFromItem).OfType<RoiRow>().Select(row => row.Id))
                .Union(args.AddedItems.Cast<object>().Select(RoiListBox.RowFromItem).OfType<RoiRow>().Select(row => row.Id)), preserveRepeatedRanges: true);
        }
        else
        {
            _editor.SelectRows(_roiList.SelectedItems.Cast<object>().Select(RoiListBox.RowFromItem).OfType<RoiRow>().Select(row => row.Id));
        }
    }

    private void FromModel(object? sender, EventArgs args) => Synchronize();

    private void Synchronize()
    {
        var selected = _editor.Session.State.Selection;
        var repeated = _editor.Session.State.RepeatedSelection;
        if (ReferenceEquals(_previousSelection, selected) && ReferenceEquals(_previousRepeatedSelection, repeated) && ReferenceEquals(_previousRows, _editor.VisibleRows))
        {
            return;
        }

        _isSelectionSyncActive = true;
        try
        {
            _roiList.ShowPage(_editor.VisibleRows);
            _roiList.ApplySelection(!repeated.IsEmpty ? _editor.VisibleRows.Where(row => selected.Contains(row.Id) || repeated.ContainsKey(row.Id)) : selected.Count < _editor.VisibleRows.Count
                ? selected.Select(_editor.VisibleRows.Find).OfType<RoiRow>()
                : _editor.VisibleRows.Where(row => selected.Contains(row.Id)));
            _previousSelection = selected;
            _previousRepeatedSelection = repeated;
            _previousRows = _editor.VisibleRows;
        }
        finally
        {
            _isSelectionSyncActive = false;
        }
    }

    public void Dispose()
    {
        _roiList.SelectionChanged -= FromView;
        _editor.Session.Changed -= FromModel;
        _editor.RowPageChanged -= Synchronize;
        _roiList.SingleSelectionRequested -= SelectSingle;
    }
}
