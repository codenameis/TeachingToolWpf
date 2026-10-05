using System.Globalization;
using System.Numerics;
using System.Windows.Media;
using TeachingToolWpf.Presentation;
using VisionCanvas.Core.Editing;
using VisionCanvas.Imaging.Filters;
using VisionCanvas.Presentation;
using VisionCanvas.Rendering;
using VisionGeometry;
using VisionImaging;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Tests;

public class RoiStyleSettingsTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("0.49")]
    [InlineData("8.01")]
    [InlineData("not a number")]
    public void OneInvalidFieldPreservesAllPublishedStyles(string invalid) => OnSta(() =>
    {
        var settings = new RoiStyleSettingsViewModel();
        var before = settings.Palette;
        var changes = 0;
        settings.Changed += () => changes++;
        settings.DefaultThickness = "2";
        settings.ChildThickness = "3";
        settings.ParentThickness = invalid;
        settings.Apply.Execute(null);
        Assert.Same(before, settings.Palette);
        Assert.Equal(0, changes);
        Assert.Contains("부모 ROI", settings.Error);
        Assert.Contains("0.5~8", settings.Error);
        Assert.Equal("2", settings.DefaultThickness);
        Assert.Equal("3", settings.ChildThickness);
    });

    [Fact]
    public void ThreeIndependentWidthsPublishOnceAndKeepFixedColors() => OnSta(() =>
    {
        var settings = new RoiStyleSettingsViewModel();
        var original = settings.Palette;
        var changes = 0;
        settings.Changed += () => changes++;
        settings.DefaultThickness = "2";
        settings.ChildThickness = "3";
        settings.ParentThickness = "4";
        settings.Apply.Execute(null);
        Assert.Equal(2f, settings.Palette.Default.ThicknessDip);
        Assert.Equal(3f, settings.Palette.Child.ThicknessDip);
        Assert.Equal(4f, settings.Palette.Parent.ThicknessDip);
        Assert.Equal(1, changes);
        Assert.Empty(settings.Error);
        Assert.Equal(original.Default.Color, settings.Palette.Default.Color);
        Assert.Equal(original.Default.SelectedColor, settings.Palette.Default.SelectedColor);
        Assert.Equal(original.Child.Color, settings.Palette.Child.Color);
        Assert.Equal(original.Child.SelectedColor, settings.Palette.Child.SelectedColor);
        Assert.Equal(original.Parent.Color, settings.Palette.Parent.Color);
        Assert.Equal(original.Parent.SelectedColor, settings.Palette.Parent.SelectedColor);
        AssertSwatch(settings.DefaultColor, settings.Palette.Default.Color);
        AssertSwatch(settings.ChildColor, settings.Palette.Child.Color);
        AssertSwatch(settings.ParentColor, settings.Palette.Parent.Color);
    });

    [Fact]
    public void UnchangedApplyAndResetDoNotRequestRedundantRendering() => OnSta(() =>
    {
        var settings = new RoiStyleSettingsViewModel();
        var changes = 0;
        settings.Changed += () => changes++;
        settings.Apply.Execute(null);
        settings.Reset.Execute(null);
        Assert.Equal(0, changes);
        settings.ParentThickness = "1.234567";
        settings.Apply.Execute(null);
        var palette = settings.Palette;
        settings.Apply.Execute(null);
        Assert.Same(palette, settings.Palette);
        Assert.Equal(1, changes);
        settings.ChildThickness = "";
        settings.Apply.Execute(null);
        Assert.NotEmpty(settings.Error);
        settings.Reset.Execute(null);
        Assert.Equal(2, changes);
        Assert.Empty(settings.Error);
        Assert.Equal(RoiStylePalette.Standard.Default.ThicknessDip, settings.Palette.Default.ThicknessDip);
        Assert.Equal(RoiStylePalette.Standard.Child.ThicknessDip, settings.Palette.Child.ThicknessDip);
        Assert.Equal(RoiStylePalette.Standard.Parent.ThicknessDip, settings.Palette.Parent.ThicknessDip);
        settings.Apply.Execute(null);
        settings.Reset.Execute(null);
        Assert.Equal(2, changes);
    });

    [Fact]
    public void DisplaySettingsPublishWithoutEditingDocumentOrHistoryAndDetachOnDispose() => OnSta(() =>
    {
        var surface = new Surface();
        var model = new EditorViewModel(surface, new UnusedLoader(), new LivePreview(surface, _ => throw new NotSupportedException()), new UnusedFilter());
        try
        {
            model.Session.InitializeImage(new("test", "test", 1000, 1000), ViewportTransform.Identity);
            var id = model.Session.Add(new(new(400, 400), 50, 30, 0));
            model.Session.Rename(id, "changed");
            model.Session.Undo();
            var before = model.Session.State;
            var bytes = model.Session.HistoryBytes;
            var undo = model.Session.CanUndo;
            var redo = model.Session.CanRedo;
            var publishes = surface.Submissions;
            model.StyleSettings.ParentThickness = "7";
            model.StyleSettings.Apply.Execute(null);
            Assert.Equal(publishes + 1, surface.Submissions);
            Assert.Same(model.StyleSettings.Palette, surface.Last!.RoiStyles);
            Assert.Same(before, model.Session.State);
            Assert.Equal(bytes, model.Session.HistoryBytes);
            Assert.Equal(undo, model.Session.CanUndo);
            Assert.Equal(redo, model.Session.CanRedo);
        }
        finally
        {
            model.Dispose();
        }
        var afterDispose = surface.Submissions;
        model.StyleSettings.Reset.Execute(null);
        Assert.Equal(afterDispose, surface.Submissions);
    });

    private static void AssertSwatch(Brush brush, Vector4 color)
    {
        var swatch = Assert.IsType<SolidColorBrush>(brush);
        Assert.True(swatch.IsFrozen);
        Assert.InRange(Math.Abs((swatch.Color.R / 255f) - color.X), 0, 1f / 255);
        Assert.InRange(Math.Abs((swatch.Color.G / 255f) - color.Y), 0, 1f / 255);
        Assert.InRange(Math.Abs((swatch.Color.B / 255f) - color.Z), 0, 1f / 255);
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            try
            {
                action();
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

    private sealed class Surface : IViewportSurface, IFramePresenter
    {
        public double ViewWidth => 1000;

        public double ViewHeight => 700;

        public double Dpi => 1;

        public int Submissions
        {
            get;
            private set;
        }

        public RenderScene? Last
        {
            get;
            private set;
        }

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
            Last = scene;
            Submissions++;
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
