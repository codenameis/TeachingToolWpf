using System.Windows.Controls;
using TeachingToolWpf.Presentation;

namespace TeachingToolWpf.Controls;

/// <summary>Adapts WPF's SelectedItems collection without putting selection rules in the view.</summary>
internal sealed class RoiListBinding : IDisposable
{
    private readonly ListBox list;
    private readonly EditorViewModel model;
    private bool synchronizing;
    private object? previousSelection;
    public RoiListBinding(ListBox list, EditorViewModel model)
    {
        this.list = list; this.model = model;
        list.ItemsSource = model.Rows;
        list.SelectionChanged += FromView; model.Session.Changed += FromModel;
    }
    private void FromView(object sender, SelectionChangedEventArgs args)
    {
        if (!synchronizing) model.SelectRows(list.SelectedItems.Cast<RoiRow>().Select(row => row.Id));
    }
    private void FromModel(object? sender, EventArgs args)
    {
        var selected = model.Session.State.Selection;
        if (ReferenceEquals(previousSelection, selected) && ReferenceEquals(list.ItemsSource, model.Rows)) return;
        synchronizing = true;
        try
        {
            if (!ReferenceEquals(list.ItemsSource, model.Rows)) list.ItemsSource = model.Rows;
            foreach (var row in list.SelectedItems.Cast<RoiRow>().Where(r => !selected.Contains(r.Id)).ToArray()) list.SelectedItems.Remove(row);
            var alreadySelected = list.SelectedItems.Cast<RoiRow>().Select(row => row.Id).ToHashSet();
            foreach (var id in selected) if (!alreadySelected.Contains(id) && model.FindRow(id) is { } row) list.SelectedItems.Add(row);
            previousSelection = selected;
        }
        finally { synchronizing = false; }
    }
    public void Dispose() { list.SelectionChanged -= FromView; model.Session.Changed -= FromModel; }
}
