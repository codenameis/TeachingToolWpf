using System.Windows.Controls;
using System.Windows;
using System.Windows.Input;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using TeachingToolWpf.Presentation;

namespace TeachingToolWpf.Controls;

/// <summary>Batches WPF selection changes as one control update.</summary>
public sealed class RoiListBox : ListBox
{
    private readonly RoiListSlots slots = [];
    private RoiRows? pageRows;
    public event Action<RoiRow>? SingleSelectionRequested;
    internal static RoiRow? RowFromItem(object? item) => item switch { RoiListSlot slot => slot.Row, RoiRow row => row, _ => null };
    internal void ShowPage(RoiRows rows)
    {
        if (ReferenceEquals(pageRows, rows)) return;
        if (!ReferenceEquals(ItemsSource, slots)) ItemsSource = slots;
        // Keep data items and realized containers stable while another page occupies the same slots.
        // The binding adapter suppresses selection events throughout this update and its selection restore.
        slots.Update(rows);
        pageRows = rows;
    }
    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && Keyboard.Modifiers == ModifierKeys.None &&
            ItemsControl.ContainerFromElement(this, e.OriginalSource as DependencyObject) is ListBoxItem item && RowFromItem(item.Content) is { } row)
            SingleSelectionRequested?.Invoke(row);
        base.OnPreviewMouseDown(e);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Keyboard.Modifiers == ModifierKeys.None && e.Key is Key.Up or Key.Down or Key.Home or Key.End or Key.Space && RowFromItem(SelectedItem) is { } row)
            SingleSelectionRequested?.Invoke(row);
    }
    public void ApplySelection(IEnumerable<RoiRow> rows)
    {
        var selected = rows.ToArray();
        // WPF resolves SetSelectedItems by enumerating Items, even when the source has an indexed IList.
        // SelectedIndex supplies a resolved item and keeps ordinary canvas clicks independent of document size.
        var source = ReferenceEquals(ItemsSource, slots) ? pageRows : ItemsSource as RoiRows;
        if (selected.Length == 1 && source is not null)
        {
            if (SelectedItems.Count > 1) UnselectAll();
            SelectedIndex = source.IndexOf(selected[0]);
        }
        else if (selected.Length == 0) UnselectAll();
        else if (selected.Length == Items.Count) SelectAll();
        else if (ReferenceEquals(ItemsSource, slots) && source is not null)
            SetSelectedItems(selected.Select(row => source.IndexOf(row.Id)).Where(index => index >= 0).Select(index => slots[index]).ToArray());
        else SetSelectedItems(selected);
    }
}

/// <summary>Page-size changes invalidate the WPF collection once; ordinary paging keeps every container.</summary>
internal sealed class RoiListSlots : ObservableCollection<RoiListSlot>
{
    public void Update(RoiRows rows)
    {
        CheckReentrancy();
        var countChanged = Count != rows.Count;
        while (Items.Count > rows.Count) Items.RemoveAt(Items.Count - 1);
        for (var i = 0; i < Items.Count; i++) Items[i].Update(rows[i]);
        for (var i = Items.Count; i < rows.Count; i++) Items.Add(new(rows[i]));
        if (!countChanged) return;
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

/// <summary>A UI slot owns only the current immutable row; changing pages preserves its visual bindings.</summary>
internal sealed class RoiListSlot(RoiRow row) : ObservableObject
{
    public RoiRow Row { get; private set; } = row;
    public string Label => Row.Label;
    public string Kind => Row.Kind;
    public double IndentWidth => Row.IndentWidth;
    public override string ToString() => Label;
    public void Update(RoiRow next)
    {
        var previous = Row; Row = next;
        if (previous.Label != next.Label) Notify(nameof(Label));
        if (previous.Kind != next.Kind) Notify(nameof(Kind));
        if (previous.Depth != next.Depth) Notify(nameof(IndentWidth));
    }
}
