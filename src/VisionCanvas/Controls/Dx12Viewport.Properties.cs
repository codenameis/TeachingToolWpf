using System.Windows;
using System.Windows.Input;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Interaction;
using VisionCanvas.Presentation;
using VisionCanvas.Rendering;

namespace VisionCanvas.Controls;

public sealed partial class Dx12Viewport
{
    public static readonly DependencyProperty SceneProperty = DependencyProperty.Register(
        nameof(Scene), typeof(RenderScene), typeof(Dx12Viewport), new PropertyMetadata(null, SceneChanged));
    public static readonly DependencyProperty LayoutStateProperty = DependencyProperty.Register(
        nameof(LayoutState), typeof(ViewportLayout), typeof(Dx12Viewport),
        new PropertyMetadata(null, (owner, _) => ((Dx12Viewport)owner).UpdateLayoutState()));
    public static readonly DependencyProperty PointerInputProperty = DependencyProperty.Register(
        nameof(PointerInput), typeof(IViewportInput), typeof(Dx12Viewport));
    public static readonly DependencyProperty KeyActionProperty = DependencyProperty.Register(
        nameof(KeyAction), typeof(Func<Key, bool, bool, bool>), typeof(Dx12Viewport));
    public static readonly DependencyProperty IsInputEnabledProperty = DependencyProperty.Register(
        nameof(IsInputEnabled), typeof(bool), typeof(Dx12Viewport), new PropertyMetadata(true));

    public RenderScene? Scene
    {
        get => (RenderScene?)GetValue(SceneProperty);
        set => SetValue(SceneProperty, value);
    }
    public ViewportLayout? LayoutState
    {
        get => (ViewportLayout?)GetValue(LayoutStateProperty);
        set => SetValue(LayoutStateProperty, value);
    }
    public bool IsInputEnabled
    {
        get => (bool)GetValue(IsInputEnabledProperty);
        set => SetValue(IsInputEnabledProperty, value);
    }

    private static void SceneChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        var viewport = (Dx12Viewport)owner;
        if (!viewport._closed)
        {
            viewport.Submit(args.NewValue as RenderScene ?? new(DocumentState.Empty, null, 0,
                viewport.ViewWidth, viewport.ViewHeight, viewport.Dpi, new()));
        }
    }

    private void UpdateLayoutState()
    {
        if (!_closed)
        {
            LayoutState?.Update(ViewWidth, ViewHeight, Dpi);
        }
    }
}
