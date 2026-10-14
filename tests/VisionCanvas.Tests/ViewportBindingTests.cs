using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using VisionCanvas.Controls;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Interaction;
using VisionCanvas.Presentation;
using VisionCanvas.Rendering;

namespace VisionCanvas.Tests;

public class ViewportBindingTests
{
    [Fact]
    public void BindingPublishesScenesInputsAndLayoutAndFollowsDataContextReplacement()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var viewport = new Dx12Viewport();
                viewport.SetBinding(Dx12Viewport.SceneProperty, new Binding(nameof(Model.Scene)));
                viewport.SetBinding(Dx12Viewport.PointerInputProperty, new Binding(nameof(Model.Input)));
                viewport.SetBinding(Dx12Viewport.LayoutStateProperty, new Binding(nameof(Model.Layout)));
                viewport.SetBinding(Dx12Viewport.IsInputEnabledProperty, new Binding(nameof(Model.CanNavigate)));
                viewport.SetBinding(Dx12Viewport.KeyActionProperty, new Binding(nameof(Model.KeyHandler)));
                var first = new Model();
                viewport.DataContext = first;
                DrainBindings();
                Assert.Same(first.Scene, viewport.Scene);
                Assert.Same(first.Input, viewport.PointerInput);
                Assert.Same(first.Layout, viewport.LayoutState);
                Assert.Equal(viewport.ViewWidth, first.Layout.Width);
                Assert.Equal(viewport.Dpi, first.Layout.Dpi);
                Assert.True(viewport.KeyAction!(Key.Escape, false, false));
                Assert.Equal(1, first.Keys);

                first.DisableAndPublish();
                DrainBindings();
                Assert.Same(first.Scene, viewport.Scene);
                Assert.False(viewport.IsInputEnabled);
                var second = new Model();
                viewport.DataContext = second;
                first.DisableAndPublish();
                DrainBindings();
                Assert.Same(second.Scene, viewport.Scene);
                Assert.Same(second.Input, viewport.PointerInput);
                Assert.True(viewport.IsInputEnabled);
                viewport.DataContext = null;
                DrainBindings();
                Assert.Null(viewport.Scene);
                Assert.Null(viewport.PointerInput);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static void DrainBindings() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

    [Theory]
    [InlineData(double.NaN, 100, 1)]
    [InlineData(100, -1, 1)]
    [InlineData(100, 100, 0)]
    public void LayoutRejectsInvalidDimensions(double width, double height, double dpi)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ViewportLayout(width, height, dpi));
        var layout = new ViewportLayout(800, 600, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => layout.Update(width, height, dpi));
        Assert.Equal(800, layout.Width);
        Assert.Equal(600, layout.Height);
        Assert.Equal(1, layout.Dpi);
    }

    private sealed class Model : INotifyPropertyChanged
    {
        public RenderScene Scene { get; private set; } = new(DocumentState.Empty, null, 0, 800, 600, 1, new());
        public InteractionController Input { get; } = new(new());
        public ViewportLayout Layout { get; } = new(800, 600);
        public bool CanNavigate { get; private set; } = true;
        public int Keys
        {
            get; private set;
        }
        public Func<Key, bool, bool, bool> KeyHandler => (_, _, _) => { Keys++; return true; };
        public event PropertyChangedEventHandler? PropertyChanged;
        public void DisableAndPublish()
        {
            CanNavigate = false;
            Scene = Scene with
            {
                Width = Scene.Width + 1
            };
            PropertyChanged?.Invoke(this, new(nameof(Scene)));
            PropertyChanged?.Invoke(this, new(nameof(CanNavigate)));
        }
    }
}
