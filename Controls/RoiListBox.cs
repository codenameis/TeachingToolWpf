using System.Windows.Controls;
using System.Windows;
using System.Windows.Input;
using TeachingToolWpf.Presentation;

namespace TeachingToolWpf.Controls;

/// <summary>Batches WPF selection changes as one control update.</summary>
public sealed class RoiListBox : ListBox
{
    public event Action<RoiRow>? SingleSelectionRequested;
    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && Keyboard.Modifiers == ModifierKeys.None &&
            ItemsControl.ContainerFromElement(this, e.OriginalSource as DependencyObject) is ListBoxItem { Content: RoiRow row })
            SingleSelectionRequested?.Invoke(row);
        base.OnPreviewMouseDown(e);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Keyboard.Modifiers == ModifierKeys.None && e.Key is Key.Up or Key.Down or Key.Home or Key.End or Key.Space && SelectedItem is RoiRow row)
            SingleSelectionRequested?.Invoke(row);
    }
    public void ApplySelection(IEnumerable<RoiRow> rows)
    {
        var selected = rows.ToArray();
        // WPF resolves SetSelectedItems by enumerating Items, even when the source has an indexed IList.
        // SelectedIndex supplies a resolved item and keeps ordinary canvas clicks independent of document size.
        if (selected.Length == 1 && ItemsSource is RoiRows source)
        {
            if (SelectedItems.Count > 1) UnselectAll();
            SelectedIndex = source.IndexOf(selected[0]);
        }
        else if (selected.Length == 0) UnselectAll();
        else SetSelectedItems(selected);
    }
}
