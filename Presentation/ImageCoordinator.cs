using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Geometry;
using TeachingToolWpf.Core.Imaging;
using TeachingToolWpf.Imaging;

namespace TeachingToolWpf.Presentation;

/// <summary>Prepares a source before moving the edit history cursor. Failed loads leave both intact.</summary>
public sealed class ImageCoordinator(EditorSession editor, IViewportSurface viewport, IImageLoader loader, Func<Task>? beforeSourceChange = null) : ObservableObject, IDisposable
{
    private CancellationTokenSource? loading;
    private bool busy;
    public bool IsBusy { get => busy; private set => Set(ref busy, value); }
    public ImageInfo? Image { get; private set; }
    public long Generation { get; private set; }
    public event Action? Changed;
    public event Action<string>? Status;
    public async Task ReplaceAsync(ImageReference reference)
    {
        await PrepareAsync(reference, info =>
        {
            var actual = reference with { Name = info.Name, Width = info.Width, Height = info.Height };
            var view = ViewportTransform.Fit(info.Width, info.Height, viewport.ViewWidth, viewport.ViewHeight);
            if (editor.State.Image is null && editor.State.Rois.Count == 0) editor.InitializeImage(actual, view);
            else editor.SetImage(actual, view);
        });
    }
    public async Task HistoryAsync(bool redo)
    {
        if (IsBusy || (redo ? !editor.CanRedo : !editor.CanUndo)) return;
        var next = redo ? editor.PreviewRedo() : editor.PreviewUndo();
        void Apply() { if (redo) editor.Redo(); else editor.Undo(); }
        if (next.Image == editor.State.Image) { Apply(); return; }
        if (next.Image is null) throw new InvalidOperationException("첫 이미지 이전 상태는 초기화 이력에서 제외되어야 합니다.");
        await PrepareAsync(next.Image, _ => Apply());
    }
    private async Task PrepareAsync(ImageReference reference, Action<ImageInfo> commit)
    {
        if (IsBusy) return;
        loading = new(); IsBusy = true; Changed?.Invoke();
        ITileSource? prepared = null;
        try
        {
            prepared = await loader.LoadAsync(reference, new Progress<string>(text => Status?.Invoke(text)), loading.Token);
            loading.Token.ThrowIfCancellationRequested();
            var info = prepared.Info;
            // Stop a live source only after replacement decoding succeeds, so failed opens preserve it.
            if (beforeSourceChange is not null) await beforeSourceChange();
            // This transfer has one owner; the decoder retires the previous source after its read ends.
            Generation = viewport.SetSource(prepared); prepared = null; Image = info;
            commit(info); Changed?.Invoke(); Status?.Invoke($"{info.Name} · {info.Width:N0} × {info.Height:N0} · {info.Pixels}");
        }
        catch (OperationCanceledException) { Status?.Invoke("이미지 열기를 취소했습니다."); }
        finally { prepared?.Dispose(); loading.Dispose(); loading = null; IsBusy = false; Changed?.Invoke(); }
    }
    public void Cancel() => loading?.Cancel();
    public void Dispose() => Cancel();
}
