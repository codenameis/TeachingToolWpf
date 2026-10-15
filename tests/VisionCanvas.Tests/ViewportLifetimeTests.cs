using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VisionCanvas.Controls;
using VisionCanvas.Core.Editing;
using VisionCanvas.Imaging;
using VisionCanvas.Rendering;

namespace VisionCanvas.Tests;

public sealed class ViewportLifetimeTests
{
    [Fact]
    public Task RepeatedPartialSummaryRemainsVisibleAndUsesConfiguredThickness() => OnStaAsync(async () =>
    {
        using var viewport = new Dx12Viewport { DevicePreference = RenderDevicePreference.Software };
        var window = CreateWindow(viewport);
        window.Width = 128;
        window.Height = 128;
        var ready = NewCompletion();
        viewport.RendererStatusChanged += status => CompleteStartup(ready, status);
        try
        {
            window.Show();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var batch = new RepeatedRoiBatch(Guid.NewGuid(), "partial", 1, new(100, 100, 1, new(100, 100)))
                .Slice(RoiCellSet.Create([new(202, 204)]))!;
            Assert.True(await RedPixels(batch, .5f) > 0, "A two-cell block inside a summary tile must not disappear.");
            batch = new RepeatedRoiBatch(Guid.NewGuid(), "detail", 1, new(3, 3, 24, new(64, 64)));
            batch = batch.WithGeometry(4, new(new(64, 64), 10, 12, 30));
            var thin = await RedPixels(batch, .5f);
            var thick = await RedPixels(batch, 4);
            Assert.True(thick > thin * 2, $"Configured thickness must affect base cells and overrides: {thin} -> {thick}.");
            batch = new RepeatedRoiBatch(Guid.NewGuid(), "outer stroke", 1, new(1, 1, 10, new(64, 64)));
            await RedPixels(batch, 8, 70);
        }
        finally
        {
            window.Close();
        }

        async Task<int> RedPixels(RepeatedRoiBatch batch, float thickness, double minimumRightDip = 0)
        {
            var path = Path.Combine(Path.GetTempPath(), $"repeated-render-{Guid.NewGuid():N}.png");
            try
            {
                var state = DocumentState.Empty with
                {
                    RepeatedRois = DocumentState.Empty.RepeatedRois.Add(batch.Id, batch)
                };
                var style = new RoiStrokeStyle(new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1), thickness);
                viewport.Submit(new(state, null, 0, viewport.ActualWidth, viewport.ActualHeight, viewport.Dpi, new(), Styles: new(style, style, style)));
                viewport.Capture(path);
                var deadline = Stopwatch.StartNew();
                while (deadline.Elapsed < TimeSpan.FromSeconds(10))
                {
                    await Task.Delay(20);
                    try
                    {
                        using var stream = File.OpenRead(path);
                        var capture = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                        var pixels = new byte[capture.PixelWidth * capture.PixelHeight * 4];
                        capture.CopyPixels(pixels, capture.PixelWidth * 4, 0);
                        var red = 0;
                        var right = 0;
                        for (var i = 0; i < pixels.Length; i += 4)
                        {
                            if (pixels[i + 2] > 80 && pixels[i + 2] > pixels[i + 1] * 2 && pixels[i + 2] > pixels[i] * 2)
                            {
                                red++;
                                right = Math.Max(right, i / 4 % capture.PixelWidth);
                            }
                        }
                        Assert.True(right >= Math.Floor(minimumRightDip * viewport.Dpi), "Outer stroke must extend beyond the cell pitch quad.");
                        return red;
                    }
                    catch (IOException) { /* Wait for the PNG writer to finish. */ }
                }
                throw new TimeoutException("Repeated ROI capture did not complete.");
            }
            finally
            {
                File.Delete(path);
            }
        }
    });

    [Fact]
    public Task CaptureAfterResizeAndMinimizeUsesRestoredBufferSize() => OnStaAsync(async () =>
    {
        using var viewport = new Dx12Viewport { DevicePreference = RenderDevicePreference.Software };
        var window = CreateWindow(viewport);
        var ready = NewCompletion();
        var errors = new List<string>();
        var document = new EditorSession().State;
        var path = Path.Combine(Path.GetTempPath(), $"viewport-resize-{Guid.NewGuid():N}.png");
        viewport.RendererStatusChanged += status =>
        {
            CompleteStartup(ready, status);
            if (status.StartsWith("렌더 오류:", StringComparison.Ordinal))
            {
                errors.Add(status);
            }
        };
        viewport.LayoutChanged += Submit;
        try
        {
            window.Show();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            for (var i = 0; i < 12; i++)
            {
                window.Width = 80 + i;
                window.Height = 70 + i;
                window.UpdateLayout();
                Submit();
                await Task.Delay(2);
            }
            window.WindowState = WindowState.Minimized;
            await Task.Delay(50);
            window.WindowState = WindowState.Normal;
            window.Width = 160;
            window.Height = 120;
            window.UpdateLayout();
            Submit();
            viewport.Capture(path);

            BitmapFrame? capture = null;
            var deadline = Stopwatch.StartNew();
            while (capture is null && deadline.Elapsed < TimeSpan.FromSeconds(10))
            {
                await Task.Delay(20);
                try
                {
                    using var stream = File.OpenRead(path);
                    capture = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                }
                catch (IOException)
                {
                    // 파일 생성과 PNG 저장 완료 사이에는 reader가 아직 열 수 없다.
                }
            }
            Assert.Empty(errors);
            Assert.NotNull(capture);
            Assert.Equal((int)Math.Ceiling(viewport.ActualWidth * viewport.Dpi), capture.PixelWidth);
            Assert.Equal((int)Math.Ceiling(viewport.ActualHeight * viewport.Dpi), capture.PixelHeight);
            Submit();
            viewport.Dispose();
            Assert.Equal(nint.Zero, viewport.Handle);
        }
        finally
        {
            viewport.LayoutChanged -= Submit;
            window.Close();
            viewport.Dispose();
            File.Delete(path);
        }

        void Submit() => viewport.Submit(new(document, null, 0, viewport.ActualWidth, viewport.ActualHeight, viewport.Dpi, new()));
    });

    [Fact]
    public Task ConcurrentRestartsShareCompletionAndLaterRestartStillWorks() => OnStaAsync(async () =>
    {
        using var viewport = new Dx12Viewport { DevicePreference = RenderDevicePreference.Software };
        var window = CreateWindow(viewport);
        var ready = NewCompletion();
        var starts = 0;
        viewport.RendererStatusChanged += status =>
        {
            if (status.StartsWith("렌더 오류:", StringComparison.Ordinal))
            {
                ready.TrySetException(new InvalidOperationException(status));
            }
            else if (status.StartsWith("DX12 ·", StringComparison.Ordinal))
            {
                starts++;
                ready.TrySetResult();
            }
        };
        try
        {
            window.Show();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            ready = NewCompletion();

            var first = viewport.RestartAsync();
            var second = viewport.RestartAsync();
            Assert.Same(first, second);
            await first.WaitAsync(TimeSpan.FromSeconds(10));
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(2, starts);

            ready = NewCompletion();
            var later = viewport.RestartAsync();
            Assert.NotSame(first, later);
            Assert.Same(later, viewport.RestartAsync());
            await later.WaitAsync(TimeSpan.FromSeconds(10));
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(3, starts);
        }
        finally
        {
            window.Close();
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task DisposeDuringRestartWaitsForRetiringWorkerWithoutWaitingForUiContinuation(bool closeWindowFirst) => OnStaAsync(async () =>
    {
        using var viewport = new Dx12Viewport { DevicePreference = RenderDevicePreference.Software };
        using var frames = new LatestFrameMailbox();
        using var release = new ManualResetEventSlim();
        var window = CreateWindow(viewport);
        var initialReady = NewCompletion();
        var retiringReady = NewCompletion();
        Task? restart = null;
        RenderWorker? retiring = null;
        viewport.RendererStatusChanged += status => CompleteStartup(initialReady, status);
        try
        {
            window.Show();
            await initialReady.Task.WaitAsync(TimeSpan.FromSeconds(10));
            GetRenderer(viewport).Dispose();

            // 실제 렌더 스레드를 GPU 초기화 직후 멈춰 종료가 아직 진행 중인 순서를 고정한다.
            // 생산 코드에 테스트 전용 수명 인터페이스를 추가하지 않도록 테스트에서만 소유 필드를 교체한다.
            retiring = new RenderWorker(viewport.Handle, viewport.Tiles, new TextRasterizer(), frames,
                new VisibleFramePreparer(), RenderDevicePreference.Software);
            retiring.StatusChanged += status =>
            {
                CompleteStartup(retiringReady, status);
                if (status.StartsWith("DX12 ·", StringComparison.Ordinal) && !release.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("종료 시험의 렌더 스레드 해제 신호가 없습니다.");
                }
            };
            SetRenderer(viewport, retiring);
            retiring.Start();
            await retiringReady.Task.WaitAsync(TimeSpan.FromSeconds(10));

            restart = viewport.RestartAsync();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.False(restart.IsCompleted);
            var renderThread = GetRenderThread(retiring);
            Assert.True(renderThread.IsAlive);
            var handle = viewport.Handle;
            if (closeWindowFirst)
            {
                // WPF는 부모 창을 닫을 때 HwndHost를 알림 창으로 옮긴다. 실제 파괴 경계는 Dispose다.
                window.Close();
                Assert.Equal(handle, viewport.Handle);
                Assert.True(renderThread.IsAlive);
            }
            Assert.True(IsWindow(handle));

            var releaseTask = Task.Run(async () =>
            {
                await Task.Delay(200);
                var windowAliveBeforeRelease = IsWindow(handle);
                release.Set();
                return windowAliveBeforeRelease;
            });
            viewport.Dispose();

            Assert.True(release.IsSet);
            Assert.False(renderThread.IsAlive);
            Assert.True(await releaseTask);
            Assert.False(IsWindow(handle));
            await restart.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(nint.Zero, viewport.Handle);
            await viewport.RestartAsync();
            Assert.Equal(nint.Zero, viewport.Handle);
        }
        finally
        {
            release.Set();
            window.Close();
            if (restart is not null)
            {
                await restart.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    });

    private static Window CreateWindow(Dx12Viewport viewport) => new()
    {
        Content = viewport,
        Width = 64,
        Height = 64,
        Left = -32000,
        Top = -32000,
        ShowActivated = false,
        ShowInTaskbar = false,
        WindowStyle = WindowStyle.None
    };

    private static TaskCompletionSource NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void CompleteStartup(TaskCompletionSource completion, string status)
    {
        if (status.StartsWith("렌더 오류:", StringComparison.Ordinal))
        {
            completion.TrySetException(new InvalidOperationException(status));
        }
        else if (status.StartsWith("DX12 ·", StringComparison.Ordinal))
        {
            completion.TrySetResult();
        }
    }

    private static RenderWorker GetRenderer(Dx12Viewport viewport)
        => Assert.IsType<RenderWorker>(RendererField.GetValue(viewport));

    private static void SetRenderer(Dx12Viewport viewport, RenderWorker renderer) => RendererField.SetValue(viewport, renderer);

    private static FieldInfo RendererField => typeof(Dx12Viewport).GetField("_renderer", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static Thread GetRenderThread(RenderWorker renderer)
        => Assert.IsType<Thread>(typeof(RenderWorker).GetField("_thread", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(renderer));

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hwnd);

    private static Task OnStaAsync(Func<Task> action)
    {
        var completion = NewCompletion();
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
                completion.TrySetResult();
            }
            catch (Exception error)
            {
                completion.TrySetException(error);
            }
            finally
            {
                if (!dispatcher.HasShutdownStarted)
                {
                    dispatcher.InvokeShutdown();
                }
            }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
