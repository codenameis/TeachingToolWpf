using System.Windows.Controls;
using System.Windows.Input;
using TeachingToolWpf.Presentation;

namespace TeachingToolWpf.Controls;

/// <summary>Adapts WPF's SelectedItems collection without putting selection rules in the view.</summary>
internal sealed class RoiListBinding : IDisposable
{
    private readonly RoiListBox list;
    private readonly EditorViewModel model;
    private bool synchronizing;
    private object? previousSelection;
    public RoiListBinding(RoiListBox list, EditorViewModel model)
    {
        this.list = list; this.model = model;
        list.ItemsSource = model.VisibleRows;
        list.SelectionChanged += FromView; model.Session.Changed += FromModel;
        model.RowPageChanged += Synchronize;
        list.SingleSelectionRequested += SelectSingle;
    }
    private void SelectSingle(RoiRow row) { if (!synchronizing) model.SelectRows([row.Id]); }
    private void FromView(object sender, SelectionChangedEventArgs args)
    {
        if (synchronizing) return;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            model.SelectRows(model.Session.State.Selection.Except(args.RemovedItems.Cast<RoiRow>().Select(row => row.Id))
                .Union(args.AddedItems.Cast<RoiRow>().Select(row => row.Id)));
        else model.SelectRows(list.SelectedItems.Cast<RoiRow>().Select(row => row.Id));
    }
    private void FromModel(object? sender, EventArgs args) => Synchronize();
    private void Synchronize()
    {
        var selected = model.Session.State.Selection;
        if (ReferenceEquals(previousSelection, selected) && ReferenceEquals(list.ItemsSource, model.VisibleRows)) return;
        synchronizing = true;
        try
        {
            if (!ReferenceEquals(list.ItemsSource, model.VisibleRows)) list.ItemsSource = model.VisibleRows;
            list.ApplySelection(selected.Count < model.VisibleRows.Count
                ? selected.Select(model.VisibleRows.Find).OfType<RoiRow>()
                : model.VisibleRows.Where(row => selected.Contains(row.Id)));
            previousSelection = selected;
        }
        finally { synchronizing = false; }
    }
    public void Dispose()
    {
        list.SelectionChanged -= FromView; model.Session.Changed -= FromModel;
        model.RowPageChanged -= Synchronize; list.SingleSelectionRequested -= SelectSingle;
    }
}
