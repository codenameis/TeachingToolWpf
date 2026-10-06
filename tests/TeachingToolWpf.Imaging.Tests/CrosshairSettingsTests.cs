using System.Globalization;
using TeachingToolWpf.Controls;
using TeachingToolWpf.Presentation;
using VisionCanvas.Controls;

namespace TeachingToolWpf.Imaging.Tests;

public sealed class CrosshairSettingsTests
{
    [Fact]
    public void ToggleStyleResetAndBindingLifetimeKeepOneIndependentGraphic()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
                using var viewport = new Dx12Viewport();
                var settings = new CrosshairSettingsViewModel();
                var changes = 0;
                settings.Changed += () => changes++;
                var binding = new CrosshairLayerBinding(viewport, settings);
                var layer = viewport.Layers["Guide"];
                Assert.False(layer.IsVisible);
                settings.Apply.Execute(null);
                Assert.Equal(0, changes);
                settings.Toggle.Execute(null);
                Assert.True(layer.IsVisible);
                Assert.Contains("끄기", settings.ToggleLabel);
                var original = settings.Graphic;
                settings.Color = "#804080FF";
                settings.Thickness = "3";
                settings.Apply.Execute(null);
                Assert.Equal(3, settings.Graphic.Thickness);
                Assert.Equal(128 / 255f, settings.Graphic.Color.W);
                Assert.Equal(1, layer.Count);
                var valid = settings.Graphic;
                settings.Color = "#INVALID";
                settings.Thickness = "4";
                settings.Apply.Execute(null);
                Assert.Same(valid, settings.Graphic);
                Assert.Equal(2, changes);
                Assert.NotEmpty(settings.Error);
                settings.Reset.Execute(null);
                Assert.Equal(original, settings.Graphic);
                Assert.True(settings.IsVisible);
                settings.Toggle.Execute(null);
                Assert.False(layer.IsVisible);
                Assert.Equal(1, layer.Count);
                binding.Dispose();
                viewport.Dispose();
                settings.Toggle.Execute(null); // 종료된 Canvas에 게시하지 않는다.
                Assert.True(settings.IsVisible);
            }
            catch (Exception error)
            {
                failure = error;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
