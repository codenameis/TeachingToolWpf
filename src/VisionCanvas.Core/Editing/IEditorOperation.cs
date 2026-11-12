namespace VisionCanvas.Core.Editing;

/// <summary>편집 세션이 실행하거나 백그라운드에서 준비하는 단일 문서 작업 계약이다.</summary>
public interface IEditorOperation
{
    string? Name { get; }

    DocumentState? PreviewBaseline => null;

    (long Before, long After)? OrderChange => null;

    bool ReplacesImage => false;

    bool RestoresViewport => false;

    DocumentState ComputeNextState(DocumentState before, CancellationToken token);
}
