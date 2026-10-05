using VisionCanvas.Core.Editing;
using VisionCanvas.Presentation;
using VisionGeometry;
using VisionImaging;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Presentation;

/// <summary>Prepares a source before moving the edit history cursor. Failed loads leave both intact.</summary>
public sealed class ImageCoordinator(EditorSession editor, IViewportSurface viewport, IImageLoader loader,
    Func<Task>? beforeSourceChange = null, Func<DocumentState, CancellationToken, Task>? preparePublication = null) : ObservableObject, IDisposable
{
    private CancellationTokenSource? _loading;
    private bool _busy;

    public bool IsBusy
    {
        get => _busy;
        private set => Set(ref _busy, value);
    }

    public ImageInfo? Image
    {
        get;
        private set;
    }

    public long Generation
    {
        get;
        private set;
    }

    public event Action? Changed;
    public event Action<string>? Status;

    public async Task ReplaceAsync(ImageReference reference)
    {
        await RunAsync(token => LoadAndCommitAsync(reference, info =>
        {
            var actual = reference with
            {
                Name = info.Name,
                Width = info.Width,
                Height = info.Height
            };
            var view = ViewportTransform.Fit(info.Width, info.Height, viewport.ViewWidth, viewport.ViewHeight);
            if (editor.State.Image is null && editor.State.Rois.Count == 0)
            {
                editor.InitializeImage(actual, view);
            }
            else
            {
                editor.SetImage(actual, view);
            }
        }, token));
    }

    public async Task HistoryAsync(bool redo)
    {
        if (IsBusy || (redo ? !editor.CanRedo : !editor.CanUndo))
        {
            return;
        }

        await RunAsync(async token =>
        {
            Status?.Invoke("이력 복원 준비 중… 화면 이동·확대 가능 · Esc로 취소");
            var prepared = await editor.PrepareHistoryAsync(redo, token);
            if (preparePublication is not null)
            {
                await preparePublication(prepared.State, token);
            }

            void Validate() => editor.ValidatePreparedHistory(prepared);
            token.ThrowIfCancellationRequested();
            Validate();
            if (prepared.State.Image == editor.State.Image)
            {
                editor.CommitPreparedHistory(prepared);
                return;
            }
            var source = prepared.State.Image ?? throw new InvalidOperationException("첫 이미지 이전 상태는 초기화 이력에서 제외되어야 합니다.");
            await LoadAndCommitAsync(source, _ => editor.CommitPreparedHistory(prepared), token, Validate);
        });
    }

    private async Task RunAsync(Func<CancellationToken, Task> operation)
    {
        if (IsBusy)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _loading = cancellation;
        IsBusy = true;
        Changed?.Invoke();
        try
        {
            await operation(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Status?.Invoke("작업을 취소했습니다.");
        }
        finally
        {
            _loading = null;
            IsBusy = false;
            Changed?.Invoke();
        }
    }

    private async Task LoadAndCommitAsync(ImageReference reference, Action<ImageInfo> commit, CancellationToken token, Action? validate = null)
    {
        ITileSource? prepared = null;
        try
        {
            prepared = await loader.LoadAsync(reference, new Progress<string>(text => Status?.Invoke(text)), token);
            token.ThrowIfCancellationRequested();
            validate?.Invoke();
            var info = prepared.Info;
            // Stop a live source only after replacement decoding succeeds, so failed opens preserve it.
            if (beforeSourceChange is not null)
            {
                await beforeSourceChange();
            }
            // Closing the old source is the commit boundary. A late Esc must not discard the
            // prepared replacement after the old live source has already been stopped.
            validate?.Invoke();
            // This transfer has one owner; the decoder retires the previous source after its read ends.
            Generation = viewport.SetSource(prepared);
            prepared = null;
            Image = info;
            commit(info);
            Changed?.Invoke();
            Status?.Invoke($"{info.Name} · {info.Width:N0} × {info.Height:N0} · {info.Pixels}");
        }
        finally
        {
            prepared?.Dispose();
        }
    }

    public void Cancel() => _loading?.Cancel();

    public void Dispose() => Cancel();
}
