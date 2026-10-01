using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using TeachingToolWpf.Rendering;
using TeachingToolWpf.Imaging;
using TeachingToolWpf.Core.Geometry;
using TeachingToolWpf.Core.Imaging;
using TeachingToolWpf.Core.Interaction;
using TeachingToolWpf.Presentation;
using System.Windows.Input;

namespace TeachingToolWpf.Controls;

public sealed class Dx12Viewport : HwndHost, IViewportSurface, IFramePresenter
{
    private RenderWorker? renderer;
    private RenderScene? scene;
    private bool closed;
    private Window? ownerWindow;
    public Dx12Viewport()
    {
        Loaded += (_, _) =>
        {
            var owner = Window.GetWindow(this);
            if (ownerWindow != owner)
            {
                if (ownerWindow is not null) ownerWindow.StateChanged -= WindowStateChanged;
                ownerWindow = owner;
                if (ownerWindow is not null) ownerWindow.StateChanged += WindowStateChanged;
            }
            UpdateSize();
        };
        IsVisibleChanged += (_, _) => UpdateSize();
    }
    private void WindowStateChanged(object? sender, EventArgs args) => UpdateSize();
    public TileWorker Tiles { get; } = new();
    private readonly LatestFrameMailbox frames = new();
    public void PublishFrame(VideoFrame frame) => frames.Publish(frame);
    public void ClearFrames() => frames.Clear();
    public VideoFrame? RetainFrame() => frames.RetainLatest();
    public double ViewWidth => ActualWidth;
    public double ViewHeight => ActualHeight;
    public double Dpi => VisualTreeHelper.GetDpi(this).DpiScaleX;
    public event Action? LayoutChanged;
    public InteractionController? Input { get; set; }
    public Func<bool>? InputEnabled { get; set; }
    public Func<Key, bool, bool, bool>? KeyAction { get; set; }
    public event Action<Exception>? InputFailed;
    public long SetSource(ITileSource source) => Tiles.SetSource(source);
    public void Submit(RenderScene value) { scene = value; renderer?.Submit(value); }
    public void Capture(string path) => renderer?.Capture(path);
    public event Action<string>? RendererStatusChanged;
    public event Action<Diagnostics.RenderMetrics>? MetricsChanged;
    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        var handle = ViewportWindow.Create(hwndParent.Handle);
        StartRenderer(handle);
        UpdateSize();
        return new HandleRef(this, handle);
    }
    private void StartRenderer(nint handle)
    {
        renderer = new RenderWorker(handle, Tiles, new WpfTextRasterizer(), frames, new VisibleFramePreparer());
        renderer.StatusChanged += status => Dispatcher.BeginInvoke(() => RendererStatusChanged?.Invoke(status));
        renderer.MetricsChanged += value => Dispatcher.BeginInvoke(() => MetricsChanged?.Invoke(value));
        renderer.Start();
        if (scene is not null) renderer.Submit(scene);
    }
    public async Task RestartAsync()
    {
        var previous = renderer; renderer = null;
        if (previous is not null) await Task.Run(previous.Dispose);
        if (closed || Handle == 0) return;
        StartRenderer(Handle); UpdateSize();
    }
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo) { base.OnRenderSizeChanged(sizeInfo); UpdateSize(); }
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi) { base.OnDpiChanged(oldDpi, newDpi); UpdateSize(); }
    private void UpdateSize()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var suspended = !IsVisible || ownerWindow?.WindowState == WindowState.Minimized;
        renderer?.Resize(suspended ? 0 : (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX), suspended ? 0 : (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY));
        LayoutChanged?.Invoke();
    }
    private static bool Pressed(int key) => NativeMethods.GetKeyState(key) < 0;
    private static InputModifiers Modifiers => (Pressed(0x11) ? InputModifiers.Control : 0) | (Pressed(0x10) ? InputModifiers.Shift : 0) | (Pressed(0x12) ? InputModifiers.Alt : 0);
    protected override nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x87) { handled = true; return 4; }
        if (message == 0x20 && (lParam.ToInt64() & 0xffff) == 1)
        {
            ViewportWindow.UseCursor(Input?.Tool switch { EditorTool.Draw => 32515, EditorTool.Pan => 32649, _ => 32512 });
            handled = true;
            return 1;
        }
        if (Input is null) return base.WndProc(hwnd, message, wParam, lParam, ref handled);
        try
        {
            var point = new PointD((short)(lParam.ToInt64() & 0xffff) / Dpi, (short)((lParam.ToInt64() >> 16) & 0xffff) / Dpi);
            if (message is 0x201 or 0x207 && (InputEnabled?.Invoke() ?? true))
            {
                NativeMethods.SetFocus(hwnd); NativeMethods.SetCapture(hwnd);
                Input.Down(point, Modifiers, message == 0x207 || Pressed(0x20)); handled = true;
            }
            else if (message is 0x202 or 0x208)
            {
                if (Input.IsDragging) Input.Up(point, Modifiers);
                NativeMethods.ReleaseCapture(); handled = true;
            }
            else if (message == 0x200 && (InputEnabled?.Invoke() ?? true)) { Input.Move(point, Modifiers); handled = true; }
            else if (message == 0x20A)
            {
                var location = new NativeMethods.NativePoint { X = (short)(lParam.ToInt64() & 0xffff), Y = (short)((lParam.ToInt64() >> 16) & 0xffff) };
                NativeMethods.ScreenToClient(hwnd, ref location);
                Input.Wheel(new(location.X / Dpi, location.Y / Dpi), (short)((wParam.ToInt64() >> 16) & 0xffff) / 120d); handled = true;
            }
            else if (message == 0x100)
            {
                handled = KeyAction?.Invoke(KeyInterop.KeyFromVirtualKey((int)wParam), Pressed(0x11), Pressed(0x10)) ?? false;
                if ((int)wParam == 0x1B) NativeMethods.ReleaseCapture();
            }
            else if (message is 0x215 or 0x8 && Input.IsDragging) Input.Cancel();
        }
        catch (Exception ex) { Input.Cancel(); NativeMethods.ReleaseCapture(); InputFailed?.Invoke(ex); }
        return handled ? 0 : base.WndProc(hwnd, message, wParam, lParam, ref handled);
    }
    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        closed = true; frames.Dispose(); renderer?.Dispose(); renderer = null; Tiles.Dispose(); NativeMethods.DestroyWindow(hwnd.Handle);
        if (ownerWindow is not null) ownerWindow.StateChanged -= WindowStateChanged;
    }
}
