using System.Collections;
using TeachingToolWpf.Presentation;

namespace TeachingToolWpf.Imaging.Tests;

public class RoiRowsTests
{
    [Fact] public void IndexedRowsPreserveOrderIdentityAndReadOnlyListContract()
    {
        var values = Enumerable.Range(0, 10_000).Select(i => new RoiRow(Guid.NewGuid(), $"R{i}", "ROI", 0)).ToArray();
        var rows = new RoiRows(values); var list = (IList)rows;
        Assert.Equal(values.Length, list.Count);
        Assert.Same(values[^1], rows.Find(values[^1].Id));
        Assert.Equal(9999, list.IndexOf(values[^1]));
        Assert.Equal(-1, list.IndexOf(values[^1] with { Label = "changed" }));
        Assert.Throws<NotSupportedException>(() => list.Add(values[0]));
        Assert.Equal(values, rows.ToArray());
        Assert.Equal(values.Skip(512).Take(512), rows.Page(1, 512));
        Assert.Equal(9999, rows.IndexOf(values[^1].Id));
    }
    [Fact] public void WpfSelectionCanShrinkToItsExistingFirstIndex()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var rows = new RoiRows(Enumerable.Range(0, 512).Select(i => new RoiRow(Guid.NewGuid(), $"R{i}", "ROI", 0)));
                var list = new TeachingToolWpf.Controls.RoiListBox { ItemsSource = rows, SelectionMode = System.Windows.Controls.SelectionMode.Extended };
                list.ApplySelection([rows[10], rows[500]]);
                Assert.Equal(2, list.SelectedItems.Count);
                list.ApplySelection([rows[10]]);
                Assert.Single(list.SelectedItems.Cast<object>());
                Assert.Equal(10, list.SelectedIndex);
                list.ApplySelection([]); Assert.Empty(list.SelectedItems);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
