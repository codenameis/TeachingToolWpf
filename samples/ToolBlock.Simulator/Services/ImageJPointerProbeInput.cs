// File: samples/ToolBlock.Simulator/Services/ImageJPointerProbeInput.cs
using System;
using VisionCanvas.Core.Interaction;
using VisionGeometry;

namespace ToolBlock.Simulator.Services;

/// <summary>
/// GoF Decorator around <see cref="IViewportInput"/> that forwards all canvas gestures unchanged
/// while reporting live pointer screen coordinates for ImageJ-style pixel value probing.
/// </summary>
public sealed class ImageJPointerProbeInput : IViewportInput
{
    private readonly IViewportInput _inner;
    private readonly Action<PointD> _onPointerMoved;

    public ImageJPointerProbeInput(IViewportInput inner, Action<PointD> onPointerMoved)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(onPointerMoved);
        _inner = inner;
        _onPointerMoved = onPointerMoved;
    }

    public bool IsEnabled
    {
        get => _inner.IsEnabled;
        set => _inner.IsEnabled = value;
    }

    public EditorTool Tool => _inner.Tool;
    public bool IsDragging => _inner.IsDragging;
    public bool HasActiveState => _inner.HasActiveState;

    public void Down(PointD screen, InputModifiers modifiers, PointerButton button)
    {
        _onPointerMoved(screen);
        _inner.Down(screen, modifiers, button);
    }

    public void DoubleClick(PointD screen, InputModifiers modifiers = InputModifiers.None)
    {
        _onPointerMoved(screen);
        _inner.DoubleClick(screen, modifiers);
    }

    public void Move(PointD screen, InputModifiers modifiers = InputModifiers.None)
    {
        _onPointerMoved(screen);
        _inner.Move(screen, modifiers);
    }

    public void Up(PointD screen, InputModifiers modifiers = InputModifiers.None)
    {
        _onPointerMoved(screen);
        _inner.Up(screen, modifiers);
    }

    public void Wheel(PointD screen, double notches)
    {
        _onPointerMoved(screen);
        _inner.Wheel(screen, notches);
    }

    public bool NudgeSelection(PointD delta) => _inner.NudgeSelection(delta);

    public void Cancel() => _inner.Cancel();
}
