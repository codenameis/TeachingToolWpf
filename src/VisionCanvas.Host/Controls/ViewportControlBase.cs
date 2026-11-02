using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Interaction;
using VisionCanvas.Diagnostics;
using VisionCanvas.Imaging;
using VisionCanvas.Layers;
using VisionCanvas.Presentation;
using VisionCanvas.Rendering;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace VisionCanvas.Controls;

/// <summary>그래픽스 API에 종속되지 않는 순수 WPF/Win32 HWND 호스트 및 입력/레이어/타일 브리지 기반 컨트롤.</summary>
public abstract class ViewportControlBase : HwndHost, IViewportSurface, IFramePresenter
{
    public static readonly DependencyProperty SceneProperty = DependencyProperty.Register(
        nameof(Scene), typeof(RenderScene), typeof(ViewportControlBase), new PropertyMetadata(null, SceneChanged));
    public static readonly DependencyProperty LayoutStateProperty = DependencyProperty.Register(
        nameof(LayoutState), typeof(ViewportLayout), typeof(ViewportControlBase),
        new PropertyMetadata(null, (owner, _) => ((ViewportControlBase)owner).UpdateLayoutState()));
    public static readonly DependencyProperty PointerInputProperty = DependencyProperty.Register(
        nameof(PointerInput), typeof(IViewportInput), typeof(ViewportControlBase));
    public static readonly DependencyProperty KeyActionProperty = DependencyProperty.Register(
        nameof(KeyAction), typeof(Func<Key, bool, bool, bool>), typeof(ViewportControlBase));
    public static readonly DependencyProperty IsInputEnabledProperty = DependencyProperty.Register(
        nameof(IsInputEnabled), typeof(bool), typeof(ViewportControlBase), new PropertyMetadata(true, InputAvailabilityChanged));

    private IRenderWorker? _renderer;
    private RenderScene? _scene;
    private RenderScene? _sourceScene;
    private ResultOverlay? _submittedInspection;
    private LayerSnapshot? _cachedInspectionSnapshot;
    private ResultOverlay? _cachedPresentedInspection;
    private bool _submitting;
    private bool _closed;
    private Window? _ownerWindow;
    private Task? _restart;
    private Task _rendererShutdown = Task.CompletedTask;
    private readonly LatestFrameMailbox _frames = new();

    protected ViewportControlBase()
    {
        Layers = new(VerifyLayerAccess, PublishLayers);
        Layers.Create("Inspection", LayerSpace.Image);
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
        IsEnabledChanged += (_, _) =>
        {
            if (!IsEnabled)
            {
                CancelInput();
            }
        };
    }

    protected bool HasActiveRenderer => _renderer is not null;

    private void WindowStateChanged(object? sender, EventArgs args) => UpdateSize();

    public TileWorker Tiles { get; } = new();

    public void PublishFrame(VideoFrame frame) => _frames.Publish(frame);

    public void ClearFrames() => _frames.Clear();

    public VideoFrame? RetainFrame() => _frames.RetainLatest();

    public double ViewWidth => ActualWidth;

    public double ViewHeight => ActualHeight;

    public double Dpi => VisualTreeHelper.GetDpi(this).DpiScaleX;

    public event Action? LayoutChanged;

    public IViewportInput? Input
    {
        get;
        set;
    }

    /// <summary>별도 문서의 포인터 처리. 지정하면 기본 ROI Input보다 우선한다.</summary>
    public IViewportInput? PointerInput
    {
        get => (IViewportInput?)GetValue(PointerInputProperty);
        set => SetValue(PointerInputProperty, value);
    }

    public Func<bool>? InputEnabled
    {
        get;
        set;
    }

    public Func<Key, bool, bool, bool>? KeyAction
    {
        get => (Func<Key, bool, bool, bool>?)GetValue(KeyActionProperty);
        set => SetValue(KeyActionProperty, value);
    }

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

    public event Action<Exception>? InputFailed;

    /// <summary>이름 있는 표시 레이어. 변경은 UI 스레드에서 하며 ROI·이력과 독립적으로 즉시 게시된다.</summary>
    public DrawingLayerCollection Layers { get; }

    private static void SceneChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        var viewport = (ViewportControlBase)owner;
        if (!viewport._closed)
        {
            viewport.Submit(args.NewValue as RenderScene ?? new(DocumentState.Empty, null, 0,
                viewport.ViewWidth, viewport.ViewHeight, viewport.Dpi, new()));
        }
    }

    private static void InputAvailabilityChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        if (args.NewValue is false)
        {
            ((ViewportControlBase)owner).CancelInput();
        }
    }

    private void UpdateLayoutState()
    {
        if (!_closed)
        {
            LayoutState?.Update(ViewWidth, ViewHeight, Dpi);
        }
    }

    private void VerifyLayerAccess()
    {
        VerifyAccess();
        ObjectDisposedException.ThrowIf(_closed, this);
    }

    private void PublishLayers()
    {
        if (!_submitting && _sourceScene is not null)
        {
            PublishScene();
        }
    }

    private void PublishScene()
    {
        var inspection = Layers["Inspection"].Snapshot;
        if (!ReferenceEquals(inspection, _cachedInspectionSnapshot))
        {
            ResultOverlay? resolved = null;
            if (inspection.IsVisible)
            {
                foreach (var (_, item) in inspection.Items)
                {
                    if (item.Graphic is ResultGraphic result)
                    {
                        resolved = result.Result;
                        break;
                    }
                }
            }

            _cachedInspectionSnapshot = inspection;
            _cachedPresentedInspection = resolved;
        }

        _scene = _sourceScene! with
        {
            Layers = Layers.Snapshot,
            Inspection = _cachedPresentedInspection,
            InspectionInLayer = true
        };
        _renderer?.Submit(_scene);
    }

    public long SetSource(ITileSource source) => Tiles.SetSource(source);

    public void Submit(RenderScene value)
    {
        VerifyLayerAccess();
        ArgumentNullException.ThrowIfNull(value);
        _submitting = true;
        try
        {
            if (!ReferenceEquals(value.Inspection, _submittedInspection))
            {
                var inspectionLayer = Layers["Inspection"];
                inspectionLayer.ReplaceAll(value.Inspection is { } result ? [new ResultGraphic(result)] : []);
                _submittedInspection = value.Inspection;
                _cachedInspectionSnapshot = inspectionLayer.Snapshot;
                _cachedPresentedInspection = inspectionLayer.Snapshot.IsVisible ? value.Inspection : null;
            }
            _sourceScene = value;
        }
        finally
        {
            _submitting = false;
        }
        PublishScene();
    }

    public void Capture(string path) => _renderer?.Capture(path);

    public event Action<string>? RendererStatusChanged;
    public event Action<RenderMetrics>? MetricsChanged;
    public event Action<OverlayPresentation>? OverlayPresented;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        var handle = ViewportWindow.Create(hwndParent.Handle);
        try
        {
            StartRenderer(handle);
            UpdateSize();
            return new HandleRef(this, handle);
        }
        catch
        {
            _renderer?.Dispose();
            _renderer = null;
            NativeMethods.DestroyWindow(handle);
            throw;
        }
    }

    private void StartRenderer(nint handle)
    {
        _renderer = CreateRenderer(handle, Tiles, new TextRasterizer(), _frames, new VisibleFramePreparer());
        _renderer.StatusChanged += status => Dispatcher.BeginInvoke(() => RendererStatusChanged?.Invoke(status));
        _renderer.MetricsChanged += value => Dispatcher.BeginInvoke(() => MetricsChanged?.Invoke(value));
        _renderer.OverlayPresented += value => Dispatcher.BeginInvoke(() => OverlayPresented?.Invoke(value));
        _renderer.Start();
        if (_scene is not null)
        {
            _renderer.Submit(_scene);
        }
    }

    /// <summary>각 그래픽스 백엔드 컨트롤이 자신의 렌더 워커를 생성한다.</summary>
    protected abstract IRenderWorker CreateRenderer(
        nint handle,
        TileWorker tiles,
        ITextRasterizer rasterizer,
        LatestFrameMailbox frames,
        IFramePreparer preparer);

    /// <summary>UI 스레드에서 호출한다. 진행 중인 재시작 요청은 같은 완료 작업을 공유한다.</summary>
    public Task RestartAsync()
    {
        VerifyAccess();
        if (_closed || Handle == 0)
        {
            return Task.CompletedTask;
        }

        return _restart is { IsCompleted: false } ? _restart : _restart = RestartRendererAsync();
    }

    private async Task RestartRendererAsync()
    {
        await Task.Yield();
        if (_closed || Handle == 0)
        {
            return;
        }

        var previous = _renderer;
        _renderer = null;
        if (previous is not null)
        {
            _rendererShutdown = Task.Run(previous.Dispose);
            await _rendererShutdown;
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
        _renderer?.Resize(
            suspended ? 0 : (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX),
            suspended ? 0 : (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY));
        LayoutChanged?.Invoke();
        UpdateLayoutState();
    }

    private static InputModifiers Modifiers =>
        (NativeMethods.IsKeyPressed(NativeVirtualKey.Control) ? InputModifiers.Control : 0) |
        (NativeMethods.IsKeyPressed(NativeVirtualKey.Shift) ? InputModifiers.Shift : 0) |
        (NativeMethods.IsKeyPressed(NativeVirtualKey.Alt) ? InputModifiers.Alt : 0);

    protected override nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        var windowMessage = (NativeWindowMessage)message;
        var input = PointerInput ?? Input;
        try
        {
            var inputEnabled = IsInputAvailable(input);
            if (!inputEnabled && IsMouseOrKeyboardMessage(message))
            {
                CancelInput();
                handled = true;
                return 0;
            }
            if (windowMessage == NativeWindowMessage.GetDialogCode)
            {
                handled = true;
                return NativeMethods.WantAllDialogKeys;
            }
            if (windowMessage == NativeWindowMessage.SetCursor && NativeMethods.IsClientCursorMessage(lParam))
            {
                ApplyToolCursor(inputEnabled ? input?.Tool : null);
                handled = true;
                return 1;
            }
            if (input is not null)
            {
                handled = DispatchInputMessage(hwnd, windowMessage, wParam, lParam, input);
            }
        }
        catch (Exception ex)
        {
            input?.Cancel();
            NativeMethods.ReleaseCapture();
            InputFailed?.Invoke(ex);
        }
        return handled ? 0 : base.WndProc(hwnd, message, wParam, lParam, ref handled);
    }

    private bool IsInputAvailable(IViewportInput? input)
        => IsEnabled && IsInputEnabled && (InputEnabled?.Invoke() ?? true) && (input?.IsEnabled ?? true);

    private static bool IsMouseOrKeyboardMessage(int message)
        => (message >= 0x0200 && message <= 0x020E) || (message >= 0x0100 && message <= 0x0109);

    private static void ApplyToolCursor(EditorTool? tool)
    {
        ViewportWindow.UseCursor(tool switch
        {
            EditorTool.Draw or EditorTool.Mask => NativeCursor.Cross,
            EditorTool.Pan => NativeCursor.Hand,
            _ => NativeCursor.Arrow
        });
    }

    private bool DispatchInputMessage(nint hwnd, NativeWindowMessage windowMessage, nint wParam, nint lParam, IViewportInput input)
    {
        var point = ClientPixelsToDips(NativeMethods.DecodePoint(lParam));
        switch (windowMessage)
        {
            case NativeWindowMessage.LeftButtonDown:
            case NativeWindowMessage.LeftButtonDoubleClick:
            case NativeWindowMessage.MiddleButtonDown:
            case NativeWindowMessage.MiddleButtonDoubleClick:
                HandlePointerDown(hwnd, windowMessage, point, input);
                return true;
            case NativeWindowMessage.LeftButtonUp:
            case NativeWindowMessage.MiddleButtonUp:
                HandlePointerUp(point, input);
                return true;
            case NativeWindowMessage.MouseMove:
                input.Move(point, Modifiers);
                return true;
            case NativeWindowMessage.MouseWheel:
                input.Wheel(WheelPositionInDips(hwnd, lParam), NativeMethods.DecodeWheelNotches(wParam));
                return true;
            case NativeWindowMessage.KeyDown:
                return HandleKeyDown((int)wParam);
            case NativeWindowMessage.CaptureChanged:
            case NativeWindowMessage.KillFocus:
                if (input.IsDragging)
                {
                    input.Cancel();
                }
                return false;
            default:
                return false;
        }
    }

    private static void HandlePointerDown(nint hwnd, NativeWindowMessage windowMessage, PointD point, IViewportInput input)
    {
        NativeMethods.SetFocus(hwnd);
        NativeMethods.SetCapture(hwnd);
        var pan = windowMessage is NativeWindowMessage.MiddleButtonDown or NativeWindowMessage.MiddleButtonDoubleClick ||
            NativeMethods.IsKeyPressed(NativeVirtualKey.Space);
        if (windowMessage == NativeWindowMessage.LeftButtonDoubleClick && !pan)
        {
            input.DoubleClick(point, Modifiers);
        }
        else
        {
            input.Down(point, Modifiers, pan ? PointerButton.Middle : PointerButton.Left);
        }
    }

    private static void HandlePointerUp(PointD point, IViewportInput input)
    {
        if (input.IsDragging)
        {
            input.Up(point, Modifiers);
        }
        NativeMethods.ReleaseCapture();
    }

    private bool HandleKeyDown(int virtualKey)
    {
        var handled = DispatchKeyAction(virtualKey);
        if (virtualKey == (int)NativeVirtualKey.Escape)
        {
            NativeMethods.ReleaseCapture();
        }
        return handled;
    }

    private PointD ClientPixelsToDips(NativeMethods.NativePoint point) => new(point.X / Dpi, point.Y / Dpi);

    private void CancelInput()
    {
        var input = PointerInput ?? Input;
        if (input is { HasActiveState: true })
        {
            input.Cancel();
        }
        if (Handle != 0 && NativeMethods.GetCapture() == Handle)
        {
            NativeMethods.ReleaseCapture();
        }
    }

    private PointD WheelPositionInDips(nint hwnd, nint lParam)
    {
        var screenPoint = NativeMethods.DecodePoint(lParam);
        NativeMethods.ScreenToClient(hwnd, ref screenPoint);
        return ClientPixelsToDips(screenPoint);
    }

    private bool DispatchKeyAction(int virtualKey)
    {
        if (NativeMethods.IsKeyPressed(NativeVirtualKey.Alt))
        {
            return false;
        }
        var key = KeyInterop.KeyFromVirtualKey(virtualKey);
        var control = NativeMethods.IsKeyPressed(NativeVirtualKey.Control);
        var shift = NativeMethods.IsKeyPressed(NativeVirtualKey.Shift);
        if (KeyAction is { } action)
        {
            return action(key, control, shift);
        }
        var input = PointerInput ?? Input;
        return input is not null && ViewportKeyboard.Nudge(input, key, control, shift);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        _closed = true;
        _frames.Dispose();
        _renderer?.Dispose();
        _renderer = null;
        _rendererShutdown.GetAwaiter().GetResult();
        Tiles.Dispose();
        NativeMethods.DestroyWindow(hwnd.Handle);
        if (_ownerWindow is not null)
        {
            _ownerWindow.StateChanged -= WindowStateChanged;
        }
    }
}
