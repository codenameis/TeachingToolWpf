using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using VisionCanvas.Controls;
using VisionCanvas.Imaging;
using VisionCanvas.Rendering;

namespace VisionCanvas.Tests;

public sealed class ViewportLifetimeTests
{
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
            retiring = new RenderWorker(viewport.Handle, viewport.Tiles, new WpfTextRasterizer(), frames,
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
