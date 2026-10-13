namespace VisionCanvas.Core.Editing;

/// <summary>소유 스레드에서 잡아 둔 불변 Undo/Redo 요청. 준비 중에는 실제 이력을 변경하지 않는다.</summary>
public sealed class HistoryStep
{
    private readonly DocumentState? _cachedSource;
    private readonly DocumentState? _cachedTarget;

    internal object HistoryIdentity
    {
        get;
    }

    internal long Revision
    {
        get;
    }

    internal int Cursor
    {
        get;
    }

    internal DocumentEdit Edit
    {
        get;
    }

    internal bool Forward
    {
        get;
    }

    public string Name => Edit.Name;

    internal HistoryStep(object historyIdentity, long revision, int cursor, DocumentEdit edit, bool forward,
        DocumentState? cachedSource = null, DocumentState? cachedTarget = null)
    {
        HistoryIdentity = historyIdentity;
        Revision = revision;
        Cursor = cursor;
        Edit = edit;
        Forward = forward;
        _cachedSource = cachedSource;
        _cachedTarget = cachedTarget;
    }

    public PreparedHistoryStep Prepare(DocumentState source, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cached = _cachedSource is { } cachedSource && _cachedTarget is not null &&
            cachedSource.SameContent(source) && cachedSource.SameSelection(source) && cachedSource.Image == source.Image;
        var target = cached
            ? Edit.RestoreCachedMetadata(_cachedTarget!, source, Forward)
            : Edit.Apply(source, Forward, cancellationToken);
        // 차이를 적용할 수 있어도 그 시작 상태가 기록된 편집의 역변환 대상과 같다는 보장은 없다.
        // 캐시를 쓰지 못한 경우만 확인한다. 이미 검증한 상태 쌍은 다시 전체를 검사하지 않는다.
        var canReuseSnapshotPair = cached || Edit.MatchesRecordedSource(source, Forward, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        // 새 결과가 요청 객체를 통해 더 오래된 상태 쌍까지 붙잡아 메모리에 남기지 않게 한다.
        var step = _cachedSource is null ? this : new HistoryStep(HistoryIdentity, Revision, Cursor, Edit, Forward);
        return new(step, source, target, canReuseSnapshotPair);
    }
}
