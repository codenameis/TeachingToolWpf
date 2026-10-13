namespace VisionCanvas.Core.Editing;

/// <summary>소유 스레드의 검증·확정을 기다리는 불변 결과. 이미지 픽셀이나 GPU 저장소는 복사하지 않는다.</summary>
public sealed class PreparedHistoryStep
{
    internal HistoryStep Step
    {
        get;
    }

    internal DocumentState Source
    {
        get;
    }

    public DocumentState Target
    {
        get;
    }

    internal bool CanReuseSnapshotPair
    {
        get;
    }

    internal PreparedHistoryStep(HistoryStep step, DocumentState source, DocumentState target, bool canReuseSnapshotPair)
    {
        Step = step;
        Source = source;
        Target = target;
        CanReuseSnapshotPair = canReuseSnapshotPair;
    }
}
