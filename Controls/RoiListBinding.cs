using System.Windows.Controls;
using TeachingToolWpf.Presentation;

namespace TeachingToolWpf.Controls;

/// <summary>Adapts WPF's SelectedItems collection without putting selection rules in the view.</summary>
internal sealed class RoiListBinding : IDisposable
{
    private readonly ListBox list;
    private readonly EditorViewModel model;
    private bool synchronizing;
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
        synchronizing = true;
        try
        {
            if (!ReferenceEquals(list.ItemsSource, model.Rows)) list.ItemsSource = model.Rows;
            var selected = model.Session.State.Selection;
            foreach (var row in list.SelectedItems.Cast<RoiRow>().Where(r => !selected.Contains(r.Id)).ToArray()) list.SelectedItems.Remove(row);
            foreach (var row in model.Rows.Where(r => selected.Contains(r.Id))) if (!list.SelectedItems.Contains(row)) list.SelectedItems.Add(row);
        }
        finally { synchronizing = false; }
    }
    public void Dispose() { list.SelectionChanged -= FromView; model.Session.Changed -= FromModel; }
}
