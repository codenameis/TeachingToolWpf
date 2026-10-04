using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using TeachingToolWpf.Core.Geometry;
using TeachingToolWpf.Core.Imaging;
using TeachingToolWpf.Core.Interaction;
using TeachingToolWpf.Imaging;
using TeachingToolWpf.Presentation;
using TeachingToolWpf.Rendering;

namespace TeachingToolWpf.Controls;

public sealed class Dx12Viewport : HwndHost, IViewportSurface, IFramePresenter
{
    private RenderWorker? _renderer;
    private RenderScene? _scene;
    private bool _closed;
    private Window? _ownerWindow;

    public Dx12Viewport()
    {
        Loaded += (_, _) =>
        {
            var owner = Window.GetWindow(this);
            if (_ownerWindow != owner)
            {
                if (_ownerWindow is not null)
                {
                    _ownerWindow.StateChanged -= WindowStateChanged;
                }

                _ownerWindow = owner;
                if (_ownerWindow is not null)
                {
                    _ownerWindow.StateChanged += WindowStateChanged;
                }
            }
            UpdateSize();
        };
        IsVisibleChanged += (_, _) => UpdateSize();
    }

    private void WindowStateChanged(object? sender, EventArgs args) => UpdateSize();

    public TileWorker Tiles
    {
        get;
    } = new();

    private readonly LatestFrameMailbox _frames = new();

    public void PublishFrame(VideoFrame frame) => _frames.Publish(frame);

    public void ClearFrames() => _frames.Clear();

    public VideoFrame? RetainFrame() => _frames.RetainLatest();

    public double ViewWidth => ActualWidth;

    public double ViewHeight => ActualHeight;

    public double Dpi => VisualTreeHelper.GetDpi(this).DpiScaleX;

    public event Action? LayoutChanged;

    public InteractionController? Input
    {
        get;
        set;
    }

    public Func<bool>? InputEnabled
    {
        get;
        set;
    }

    public Func<Key, bool, bool, bool>? KeyAction
    {
        get;
        set;
    }

    public event Action<Exception>? InputFailed;

    public long SetSource(ITileSource source) => Tiles.SetSource(source);

    public void Submit(RenderScene value)
    {
        _scene = value;
        _renderer?.Submit(value);
    }

    public void Capture(string path) => _renderer?.Capture(path);

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
        _renderer = new RenderWorker(handle, Tiles, new WpfTextRasterizer(), _frames, new VisibleFramePreparer());
        _renderer.StatusChanged += status => Dispatcher.BeginInvoke(() => RendererStatusChanged?.Invoke(status));
        _renderer.MetricsChanged += value => Dispatcher.BeginInvoke(() => MetricsChanged?.Invoke(value));
        _renderer.Start();
        if (_scene is not null)
        {
            _renderer.Submit(_scene);
        }
    }

    public async Task RestartAsync()
    {
        var previous = _renderer;
        _renderer = null;
        if (previous is not null)
        {
            await Task.Run(previous.Dispose);
        }

        if (_closed || Handle == 0)
        {
            return;
        }

        StartRenderer(Handle);
        UpdateSize();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        UpdateSize();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        UpdateSize();
    }

    private void UpdateSize()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var suspended = !IsVisible || _ownerWindow?.WindowState == WindowState.Minimized;
        // 크기 0은 렌더 중단을 뜻한다. 편집기에는 변경된 레이아웃을 계속 알린다.
        _renderer?.Resize(
            suspended ? 0 : (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX),
            suspended ? 0 : (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY));
        LayoutChanged?.Invoke();
    }

    private static InputModifiers Modifiers =>
        (NativeMethods.IsKeyPressed(NativeVirtualKey.Control) ? InputModifiers.Control : 0) |
        (NativeMethods.IsKeyPressed(NativeVirtualKey.Shift) ? InputModifiers.Shift : 0) |
        (NativeMethods.IsKeyPressed(NativeVirtualKey.Alt) ? InputModifiers.Alt : 0);

    protected override nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        var windowMessage = (NativeWindowMessage)message;
        if (windowMessage == NativeWindowMessage.GetDialogCode)
        {
            handled = true;
            return NativeMethods.WantAllDialogKeys;
        }
        if (windowMessage == NativeWindowMessage.SetCursor && NativeMethods.IsClientCursorMessage(lParam))
        {
            ViewportWindow.UseCursor(Input?.Tool switch
            {
                EditorTool.Draw => NativeCursor.Cross,
                EditorTool.Pan => NativeCursor.Hand,
                _ => NativeCursor.Arrow
            });
            handled = true;
            return 1;
        }
        if (Input is null)
        {
            return base.WndProc(hwnd, message, wParam, lParam, ref handled);
        }

        try
        {
            var point = ClientPixelsToDips(NativeMethods.DecodePoint(lParam));
            if ((windowMessage is NativeWindowMessage.LeftButtonDown or NativeWindowMessage.MiddleButtonDown) &&
                (InputEnabled?.Invoke() ?? true))
            {
                NativeMethods.SetFocus(hwnd);
                NativeMethods.SetCapture(hwnd);
                Input.Down(point, Modifiers, windowMessage == NativeWindowMessage.MiddleButtonDown ||
                    NativeMethods.IsKeyPressed(NativeVirtualKey.Space));
                handled = true;
            }
            else if (windowMessage is NativeWindowMessage.LeftButtonUp or NativeWindowMessage.MiddleButtonUp)
            {
                // ReleaseCapture가 CaptureChanged를 재진입시킬 수 있으므로 먼저 편집을 끝낸다.
                if (Input.IsDragging)
                {
                    Input.Up(point, Modifiers);
                }

                NativeMethods.ReleaseCapture();
                handled = true;
            }
            else if (windowMessage == NativeWindowMessage.MouseMove && (InputEnabled?.Invoke() ?? true))
            {
                Input.Move(point, Modifiers);
                handled = true;
            }
            else if (windowMessage == NativeWindowMessage.MouseWheel)
            {
                var wheelPoint = WheelPositionInDips(hwnd, lParam);
                Input.Wheel(wheelPoint, NativeMethods.DecodeWheelNotches(wParam));
                handled = true;
            }
            else if (windowMessage == NativeWindowMessage.KeyDown)
            {
                var virtualKey = (int)wParam;
                handled = DispatchKeyAction(virtualKey);
                if (virtualKey == (int)NativeVirtualKey.Escape)
                {
                    NativeMethods.ReleaseCapture();
                }
            }
            else if ((windowMessage is NativeWindowMessage.CaptureChanged or NativeWindowMessage.KillFocus) && Input.IsDragging)
            {
                Input.Cancel();
            }
        }
        catch (Exception ex)
        {
            Input.Cancel();
            NativeMethods.ReleaseCapture();
            InputFailed?.Invoke(ex);
        }
        return handled ? 0 : base.WndProc(hwnd, message, wParam, lParam, ref handled);
    }

    private PointD ClientPixelsToDips(NativeMethods.NativePoint point) => new(point.X / Dpi, point.Y / Dpi);

    private PointD WheelPositionInDips(nint hwnd, nint lParam)
    {
        // 휠 메시지는 버튼/이동 메시지와 달리 화면 전체의 픽셀 좌표를 전달한다.
        var screenPoint = NativeMethods.DecodePoint(lParam);
        NativeMethods.ScreenToClient(hwnd, ref screenPoint);
        return ClientPixelsToDips(screenPoint);
    }

    private bool DispatchKeyAction(int virtualKey) =>
        KeyAction?.Invoke(KeyInterop.KeyFromVirtualKey(virtualKey),
            NativeMethods.IsKeyPressed(NativeVirtualKey.Control),
            NativeMethods.IsKeyPressed(NativeVirtualKey.Shift)) ?? false;

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        _closed = true;
        _frames.Dispose();
        _renderer?.Dispose();
        _renderer = null;
        Tiles.Dispose();
        NativeMethods.DestroyWindow(hwnd.Handle);
        if (_ownerWindow is not null)
        {
            _ownerWindow.StateChanged -= WindowStateChanged;
        }
    }
}
