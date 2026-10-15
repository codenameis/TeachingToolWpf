using System.Globalization;
using System.Numerics;
using System.Windows.Media;
using TeachingToolWpf.Services;
using TeachingToolWpf.ViewModels;
using VisionCanvas.Core.Editing;
using VisionCanvas.Imaging.Filters;
using VisionCanvas.Presentation;
using VisionCanvas.Rendering;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Tests;

public class RoiStyleSettingsTests
{
    [Fact]
    public void HandleScaleAppliesImmediatelyAndResetRestoresDefault() => OnSta(() =>
    {
        var settings = new RoiStyleSettingsViewModel();
        var changes = 0;
        settings.Changed += () => changes++;
        Assert.Equal(1, settings.HandleScale);
        settings.HandleScale = 2;
        Assert.Equal(1, changes);
        settings.HandleScale = 2;
        settings.Apply.Execute(null);
        Assert.Equal(1, changes);
        settings.Reset.Execute(null);
        Assert.Equal(1, settings.HandleScale);
        Assert.Equal(2, changes);
    });

    [Fact]
    public void AllHierarchyColorsAreDistinctAndModuleRootSettingsAreAtomic() => OnSta(() =>
    {
        var settings = new RoiStyleSettingsViewModel();
        Assert.Equal(5, settings.Roles.Count);
        var roles = new[] { RoiRole.Child, RoiRole.Parent, RoiRole.Module, RoiRole.Root };
        Assert.Equal(4, roles.Select(role => settings.Palette.For(role).Color).Distinct().Count());
        Assert.Equal(4, roles.Select(role => settings.Palette.For(role).SelectedColor).Distinct().Count());
        Assert.Equal(4, roles.Select(role => settings.Palette.For(role).MultiSelectedColor).Distinct().Count());
        settings.Module.Color = "#123456";
        settings.Root.Color = "#654321";
        settings.Module.Thickness = "2";
        settings.Root.Thickness = "3";
        settings.Apply.Execute(null);
        Assert.Empty(settings.Error);
        Assert.Equal(2, settings.Palette.Module.ThicknessDip);
        Assert.Equal(3, settings.Palette.Root.ThicknessDip);
        var before = settings.Palette;
        settings.Root.Color = "bad";
        settings.Module.Thickness = "4";
        settings.Apply.Execute(null);
        Assert.Same(before, settings.Palette);
        Assert.NotEmpty(settings.Error);
        settings.Reset.Execute(null);
        Assert.Equal(RoiStylePalette.Standard, settings.Palette);
    });

    [Fact]
    public void RoleAndSelectionColorsApplyTogetherAndInvalidColorPreservesThePreviousPalette() => OnSta(() =>
    {
        var settings = new RoiStyleSettingsViewModel();
        var changes = 0;
        settings.Changed += () => changes++;
        settings.Default.Color = "#102030";
        settings.Default.SelectedColor = "#405060";
        settings.Default.MultiSelectedColor = "#708090";
        settings.Child.Color = "#11AA33";
        settings.Parent.MultiSelectedColor = "#CC55EE";
        settings.Apply.Execute(null);
        Assert.Empty(settings.Error);
        Assert.Equal(new Vector4(16 / 255f, 32 / 255f, 48 / 255f, 1), settings.Palette.Default.Color);
        Assert.Equal(new Vector4(64 / 255f, 80 / 255f, 96 / 255f, 1), settings.Palette.Default.SelectedColor);
        Assert.Equal(new Vector4(112 / 255f, 128 / 255f, 144 / 255f, 1), settings.Palette.Default.MultiSelectedColor);
        Assert.NotEqual(settings.Palette.Default.MultiSelectedColor, settings.Palette.Parent.MultiSelectedColor);
        var before = settings.Palette;
        settings.DefaultThickness = "5";
        settings.Child.MultiSelectedColor = "#BAD";
        settings.Apply.Execute(null);
        Assert.Same(before, settings.Palette);
        Assert.Equal(1, changes);
        Assert.Contains("자식 ROI 다중 선택", settings.Error);
        settings.Reset.Execute(null);
        Assert.Equal(RoiStylePalette.Standard, settings.Palette);
        Assert.Empty(settings.Error);
        settings.Apply.Execute(null);
        Assert.Equal(2, changes);
    });

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
        Assert.Contains("ROI 그룹", settings.Error);
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
        var model = new MainWindowViewModel(surface, new(surface.ViewWidth, surface.ViewHeight, surface.Dpi), new UnusedLoader(), new LivePreview(surface, _ => throw new NotSupportedException()), new UnusedFilter(), EditorTestServices.Inspections());
        var sceneChanges = 0;
        model.PropertyChanged += (_, args) => sceneChanges += args.PropertyName == nameof(model.Scene) ? 1 : 0;
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
            var publishes = sceneChanges;
            model.StyleSettings.ParentThickness = "7";
            model.StyleSettings.Apply.Execute(null);
            Assert.Equal(publishes + 1, sceneChanges);
            Assert.Same(model.StyleSettings.Palette, model.Scene!.RoiStyles);
            Assert.Same(before, model.Session.State);
            Assert.Equal(bytes, model.Session.HistoryBytes);
            Assert.Equal(undo, model.Session.CanUndo);
            Assert.Equal(redo, model.Session.CanRedo);
            model.StyleSettings.HandleScale = 2;
            Assert.Equal(2, model.Input.HandleScale);
            Assert.Equal(2, model.Scene.Overlay.HandleScale);
            Assert.Same(before, model.Session.State);
            Assert.Equal(bytes, model.Session.HistoryBytes);
            Assert.Equal(redo, model.Session.CanRedo);
        }
        finally
        {
            model.Dispose();
        }
        var afterDispose = sceneChanges;
        model.StyleSettings.Reset.Execute(null);
        Assert.Equal(afterDispose, sceneChanges);
        Assert.Equal(2, model.Input.HandleScale);
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
