using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using TeachingToolWpf.Controls;
using TeachingToolWpf.Presentation;
using VisionCanvas.Core.Editing;
using VisionCanvas.Imaging.Filters;
using VisionCanvas.Presentation;
using VisionCanvas.Rendering;
using VisionGeometry;
using VisionImaging;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Tests;

public class RoiRowsTests
{
    [Fact]
    public void IndexedRowsPreserveOrderIdentityAndReadOnlyListContract()
    {
        var values = Enumerable.Range(0, 10_000).Select(i => new RoiRow(Guid.NewGuid(), $"R{i}", "ROI", 0)).ToArray();
        var rows = new RoiRows(values);
        var list = (IList)rows;
        Assert.Equal(values.Length, list.Count);
        Assert.Same(values[^1], rows.Find(values[^1].Id));
        Assert.Equal(9999, list.IndexOf(values[^1]));
        Assert.Equal(-1, list.IndexOf(values[^1] with
        {
            Label = "changed"
        }));
        Assert.Throws<NotSupportedException>(() => list.Add(values[0]));
        Assert.Equal(values, rows.ToArray());
        Assert.Equal(values.Skip(512).Take(512), rows.Page(1, 512));
        Assert.Equal(9999, rows.IndexOf(values[^1].Id));
    }

    [Fact]
    public void WpfSelectionCanShrinkToItsExistingFirstIndex()
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
                list.ApplySelection([]);
                Assert.Empty(list.SelectedItems);
                list.ApplySelection(rows);
                Assert.Equal(rows.Count, list.SelectedItems.Count);
                list.ApplySelection([rows[500]]);
                Assert.Single(list.SelectedItems.Cast<object>());
                Assert.Equal(500, list.SelectedIndex);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    [Fact]
    public void PageReplacementReusesContainersAndUpdatesDisplayedRows() => OnSta(() =>
    {
        var first = CreateRows(512, "첫 페이지");
        var second = CreateRows(512, "다음 페이지");
        var list = new RoiListBox
        {
            Width = 240,
            Height = 180,
            SelectionMode = SelectionMode.Extended,
            ItemTemplate = (DataTemplate)XamlReader.Parse("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><TextBlock Text='{Binding Label}'/></DataTemplate>")
        };
        list.ShowPage(first);
        list.ApplySelection([first[0]]);
        Layout(list);
        var container = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(0));
        var source = list.ItemsSource;
        var slot = list.Items[0];
        var text = Assert.IsType<TextBlock>(FindText(container));
        Assert.Equal(first[0].Label, text.Text);

        list.ShowPage(second);
        list.ApplySelection([second[0]]);
        Layout(list);

        Assert.Same(source, list.ItemsSource);
        Assert.Same(slot, list.Items[0]);
        Assert.Same(container, list.ItemContainerGenerator.ContainerFromIndex(0));
        Assert.Equal(second[0].Label, text.Text);
        Assert.Equal(second[0].Id, RoiListBox.RowFromItem(list.SelectedItem)!.Id);
        Assert.DoesNotContain(first[0].Id, list.Items.Cast<object>().Select(item => RoiListBox.RowFromItem(item)!.Id));
    });

    [Fact]
    public void ShortLastPageAndRestoredFullPageSelectOnlyCurrentRowIds() => OnSta(() =>
    {
        var full = CreateRows(512, "전체");
        var last = CreateRows(3, "마지막");
        var list = new RoiListBox { SelectionMode = SelectionMode.Extended };
        list.ShowPage(full);
        list.ApplySelection(full);
        Assert.Equal(512, list.SelectedItems.Count);
        var firstSlot = list.Items[0];
        list.ShowPage(last);
        list.ApplySelection([last[0], last[2]]);
        Assert.Equal(3, list.Items.Count);
        Assert.Same(firstSlot, list.Items[0]);
        Assert.True(list.SelectedItems.Cast<object>().Select(item => RoiListBox.RowFromItem(item)!.Id).ToHashSet().SetEquals([last[0].Id, last[2].Id]));
        list.ShowPage(full);
        list.ApplySelection([full[0], full[500]]);
        Assert.Equal(512, list.Items.Count);
        Assert.Equal(2, list.SelectedItems.Count);
        list.ApplySelection([full[0]]);
        Assert.Single(list.SelectedItems.Cast<object>());
        Assert.Equal(full[0].Id, RoiListBox.RowFromItem(list.SelectedItem)!.Id);
        list.ApplySelection([]);
        Assert.Empty(list.SelectedItems);
    });

    [Fact]
    public void PageSynchronizationKeepsOffPageSelectionAndPlainClickReplacesIt() => OnStaAsync(async () =>
    {
        var surface = new Surface();
        using var model = new EditorViewModel(surface, new UnusedLoader(), new LivePreview(surface, _ => throw new NotSupportedException()), new UnusedFilter());
        var list = new RoiListBox { Width = 240, Height = 180, SelectionMode = SelectionMode.Extended };
        using var binding = new RoiListBinding(list, model);
        model.Session.InitializeImage(new("rows", "rows", 10000, 10000), ViewportTransform.Identity);
        model.Session.AddRange(Enumerable.Range(0, 4600).Select(i => new RoiGeometry(new(50 + (i % 80 * 80), 50 + (i / 80 * 80)), 30, 30, 0)));
        var first = model.Rows[0].Id;
        var otherPage = model.Rows[550].Id;
        model.Session.Select([first, otherPage]);
        model.NextRowPage.Execute(null);
        Assert.True(model.Session.State.Selection.SetEquals([first, otherPage]));
        Assert.Equal(otherPage, RoiListBox.RowFromItem(Assert.Single(list.SelectedItems.Cast<object>()))!.Id);
        model.PreviousRowPage.Execute(null);
        Assert.True(model.Session.State.Selection.SetEquals([first, otherPage]));
        Assert.Equal(first, RoiListBox.RowFromItem(Assert.Single(list.SelectedItems.Cast<object>()))!.Id);
        Layout(list);
        var item = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(0));
        item.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = Mouse.PreviewMouseDownEvent,
            Source = item
        });
        Assert.True(model.Session.State.Selection.SetEquals([first]));

        await model.SelectAll.ExecuteAsync();
        model.NextRowPage.Execute(null);
        Assert.Equal(4600, model.Session.State.Selection.Count);
        Assert.Equal(512, list.SelectedItems.Count);
        while (model.NextRowPage.CanExecute(null))
        {
            model.NextRowPage.Execute(null);
        }

        Assert.Equal(4600, model.Session.State.Selection.Count);
        Assert.Equal(504, list.SelectedItems.Count);
        model.PreviousRowPage.Execute(null);
        Assert.Equal(4600, model.Session.State.Selection.Count);
        Assert.Equal(512, list.SelectedItems.Count);
    });

    [Fact]
    public void CancelledRowBuildDoesNotPublishPartialRowsOrChangeTheDocument()
    {
        var editor = new EditorSession();
        var parent = editor.Add(new(new(100, 100), 80, 80, 0));
        editor.AddChild(parent, new(new(100, 100), 20, 20, 0));
        var before = editor.State;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => RoiRow.Build(before, cancellation.Token));
        Assert.Same(before, editor.State);
        var rows = RoiRow.Build(before);
        Assert.Equal(new[] { 0, 1 }, rows.Select(row => row.Depth));
    }

    private static RoiRows CreateRows(int count, string label) => new(Enumerable.Range(0, count).Select(i => new RoiRow(Guid.NewGuid(), $"{label} {i}", "ROI", 0)));

    private static void Layout(RoiListBox list)
    {
        // A standalone STA control has no Application theme lookup. Supply real presenters explicitly;
        // initialize them once so the second layout still verifies reuse of the original containers.
        if (list.Template is null)
        {
            const string Schema = "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'";
            list.Template = (ControlTemplate)XamlReader.Parse($"<ControlTemplate {Schema} TargetType='ListBox'><ScrollViewer CanContentScroll='True'><ItemsPresenter/></ScrollViewer></ControlTemplate>");
            list.ItemsPanel = (ItemsPanelTemplate)XamlReader.Parse($"<ItemsPanelTemplate {Schema}><VirtualizingStackPanel/></ItemsPanelTemplate>");
            list.ItemContainerStyle = (Style)XamlReader.Parse($"<Style {Schema} TargetType='ListBoxItem'><Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ListBoxItem'><Border><ContentPresenter/></Border></ControlTemplate></Setter.Value></Setter></Style>");
        }
        list.ApplyTemplate();
        list.Measure(new(240, 180));
        list.Arrange(new(0, 0, 240, 180));
        list.UpdateLayout();
    }

    private static TextBlock? FindText(DependencyObject parent)
    {
        for (var i = 0;
i < VisualTreeHelper.GetChildrenCount(parent);
i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is TextBlock text)
            {
                return text;
            }

            if (FindText(child) is { } nested)
            {
                return nested;
            }
        }
        return null;
    }

    private static void OnSta(Action action) => OnStaAsync(() =>
    {
        action();
        return Task.CompletedTask;
    });

    private static void OnStaAsync(Func<Task> action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            try
            {
                var task = action();
                _ = task.ContinueWith(_ => dispatcher.BeginInvokeShutdown(DispatcherPriority.Background), TaskScheduler.Default);
                Dispatcher.Run();
                task.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                if (!dispatcher.HasShutdownStarted)
                {
                    dispatcher.InvokeShutdown();
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private sealed class Surface : IViewportSurface, IFramePresenter
    {
        public double ViewWidth => 1000;

        public double ViewHeight => 700;

        public double Dpi => 1;

        public event Action? LayoutChanged
        {
            add
            {
            }
            remove
            {
            }
        }

        public long SetSource(ITileSource source) => throw new NotSupportedException();

        public void Submit(RenderScene scene)
        {
        }

        public Task RestartAsync() => Task.CompletedTask;

        public void PublishFrame(VideoFrame frame) => throw new NotSupportedException();

        public void ClearFrames()
        {
        }
    }
    private sealed class UnusedLoader : IImageLoader
    {
        public Task<ITileSource> LoadAsync(ImageReference reference, IProgress<string> progress, CancellationToken token) => throw new NotSupportedException();
    }
    private sealed class UnusedFilter : IRoiFilterPreview
    {
        public Task<FilterPreviewResult> ProcessAsync(FilterRequest request, CancellationToken token) => throw new NotSupportedException();
    }
}
