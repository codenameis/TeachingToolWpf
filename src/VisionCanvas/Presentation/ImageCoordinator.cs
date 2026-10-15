using System.ComponentModel;
using VisionCanvas.Core.Editing;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace VisionCanvas.Presentation;

/// <summary>
/// 새 이미지 준비와 문서의 Undo/Redo 순서를 맞춘다. 파일 읽기 실패로 기존 문서를 잃지 않게 한다.
/// 문서를 관리하는 실행 문맥에서 호출하고, IsBusy 동안 다른 문서 변경을 겹쳐 실행하지 않아야 한다.
/// Dispose는 로딩 취소를 요청한다. display와 loader 자체의 수명은 이 객체를 만든 쪽이 관리한다.
/// </summary>
// 초보자 읽기 순서: ReplaceAsync → RunAsync → LoadAndCommitAsync → HistoryAsync.
// '소유권'은 여기서 이미지 소스를 최종적으로 Dispose(해제)할 책임이 누구에게 있는지를 뜻한다.
// beforeSourceChange/preparePublication은 기존 호스트가 교체 전 준비를 연결할 때 쓰는 선택 함수다.
// 단순 MVVM 샘플에서는 전달하지 않으므로 null이며 해당 단계는 건너뛴다.
public sealed class ImageCoordinator(EditorSession editor, IImageDisplay display, ViewportLayout layout, IImageLoader loader,
    Func<Task>? beforeSourceChange = null, Func<DocumentState, CancellationToken, Task>? preparePublication = null) : INotifyPropertyChanged, IDisposable
{
    private CancellationTokenSource? _loading;
    private bool _busy;

    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (_busy == value)
            {
                return;
            }
            _busy = value;
            PropertyChanged?.Invoke(this, new(nameof(IsBusy)));
        }
    }

    public ImageInfo? Image
    {
        get;
        private set;
    }

    // 이미지가 바뀔 때 받는 세대 번호다. 이전 이미지에서 늦게 도착한 타일과 새 타일을 구별한다.
    public long Generation
    {
        get;
        private set;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? Changed;
    public event Action<string>? Status;

    public async Task ReplaceAsync(ImageReference reference)
    {
        // reference에는 경로·이름 등만 있다. 실제 파일을 읽은 뒤 info의 크기로 문서를 갱신한다.
        await RunAsync(token => LoadAndCommitAsync(reference, info =>
        {
            // with는 기존 값을 직접 고치지 않고, 지정한 속성만 바꾼 새 값을 만든다.
            var actual = reference with
            {
                Name = info.Name,
                Width = info.Width,
                Height = info.Height
            };
            var view = ViewportTransform.Fit(info.Width, info.Height, layout.Width, layout.Height);
            if (editor.State.Image is null && editor.State.Rois.Count == 0)
            {
                // 첫 이미지는 시작 상태다. Undo로 '이미지도 없는 상태'로 돌아가게 만들지 않는다.
                editor.InitializeImage(actual, view);
            }
            else
            {
                // 이미 편집 중이었다면 이미지 교체를 이력에 남겨 Undo/Redo할 수 있게 한다.
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
            // 먼저 되돌릴 상태를 준비만 한다. 아직 이력의 현재 위치를 옮기지 않는다.
            var prepared = await editor.PrepareHistoryAsync(redo, token);
            if (preparePublication is not null)
            {
                await preparePublication(prepared.State, token);
            }

            // 기다리는 동안 문서가 달라졌다면 오래된 준비 결과를 덮어쓰지 않도록 검사한다.
            void Validate() => editor.ValidatePreparedHistory(prepared);
            token.ThrowIfCancellationRequested();
            Validate();
            if (prepared.State.Image == editor.State.Image)
            {
                // 이미지가 같은 ROI 편집 이력은 원본을 다시 열 필요가 없다.
                editor.CommitPreparedHistory(prepared);
                return;
            }
            var source = prepared.State.Image ?? throw new InvalidOperationException("첫 이미지 이전 상태는 초기화 이력에서 제외되어야 합니다.");
            // 이미지까지 달라지면 원본을 먼저 연다. 파일이 없으면 이력 위치도 그대로 유지된다.
            await LoadAndCommitAsync(source, _ => editor.CommitPreparedHistory(prepared), token, Validate);
        });
    }

    /// <summary>이미지와 일반 ROI 계층을 함께 불러온다. 검증/원본 읽기 실패 시 이전 문서를 보존한다.</summary>
    public Task LoadDocumentAsync(ImageReference image, IEnumerable<Roi> rois) => RunAsync(async token =>
    {
        var prepared = await editor.PrepareDocumentAsync(image, rois,
            ViewportTransform.Fit(image.Width, image.Height, layout.Width, layout.Height), token);
        await LoadAndCommitAsync(image, _ => editor.CommitPrepared(prepared), token, () => editor.ValidatePrepared(prepared), info =>
        {
            // 좌표는 원본 픽셀 기준이다. 모델과 실제 이미지 크기가 다르면 임의로 늘리거나 자르지 않는다.
            if (info.Width != image.Width || info.Height != image.Height)
            {
                throw new InvalidOperationException("모델의 이미지 크기와 실제 원본 크기가 다릅니다.");
            }
        });
    });

    private async Task RunAsync(Func<CancellationToken, Task> operation)
    {
        if (IsBusy)
        {
            return;
        }

        // 취소 요청을 보낼 손잡이(Source)와 작업이 취소를 확인할 표식(Token)을 만든다.
        // using var는 이 메서드를 빠져나갈 때 Source를 정리한다.
        using var cancellation = new CancellationTokenSource();
        _loading = cancellation;
        IsBusy = true;
        Changed?.Invoke();
        try
        {
            await operation(cancellation.Token);
        }
        // 우리가 요청한 취소만 정상 취소로 처리한다. 파일 읽기 등의 다른 오류는 호출자에게 전달된다.
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

    private async Task LoadAndCommitAsync(ImageReference reference, Action<ImageInfo> commit, CancellationToken token, Action? validate = null,
        Action<ImageInfo>? validateImage = null)
    {
        // 준비 단계에서는 이 메서드가 소스를 소유한다. 표시 쪽에 넘기지 못하면 finally에서 해제한다.
        ITileSource? prepared = null;
        try
        {
            prepared = await loader.LoadAsync(reference, new Progress<string>(text => Status?.Invoke(text)), token);
            // 로더가 취소 직전에 끝났을 수도 있으므로, 결과를 받은 뒤에도 취소 여부를 다시 확인한다.
            token.ThrowIfCancellationRequested();
            validate?.Invoke();
            var info = prepared.Info;
            validateImage?.Invoke(info);
            // 기존 호스트의 라이브 소스도 새 파일 준비에 성공한 뒤에만 중지해야 실패 시 계속 표시할 수 있다.
            if (beforeSourceChange is not null)
            {
                await beforeSourceChange();
            }
            // 기존 소스 중지를 시작한 뒤에는 새 소스 설치까지 마친다.
            // 여기서 늦은 Esc로 새 소스를 버리면, 기존 소스도 이미 멈춰 화면과 문서가 어긋날 수 있다.
            validate?.Invoke();
            // SetSource 성공 후에는 표시 쪽이 새 소스의 해제를 책임진다.
            // 이전 소스는 진행 중인 타일 읽기가 끝난 뒤 해제되어, 읽는 중에 자원이 사라지지 않는다.
            Generation = display.SetSource(prepared);
            // null은 해제가 아니다. finally에서 같은 소스를 다시 해제하지 않도록 소유권 이전을 표시한다.
            prepared = null;
            Image = info;
            commit(info);
            Changed?.Invoke();
            Status?.Invoke($"{info.Name} · {info.Width:N0} × {info.Height:N0} · {info.Pixels}");
        }
        finally
        {
            // 소유권을 넘기기 전에 실패·취소했다면 이쪽에서 해제한다. 성공 시 prepared는 null이다.
            prepared?.Dispose();
        }
    }

    // 취소는 즉시 강제 종료가 아니라 요청이다. 실제 작업은 토큰을 확인한 지점에서 멈춘다.
    public void Cancel() => _loading?.Cancel();

    public void Dispose() => Cancel();
}
