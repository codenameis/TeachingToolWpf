namespace VisionCanvas.Rendering.Abstractions;

/// <summary>백엔드별 의도된 프레임 루프 동작 차이(타일 예산, 캡처 가시 타일 대기, 첫 프레임 메트릭 통지, 캡처 시점)를 명시한다.</summary>
public readonly record struct FrameLoopOptions(
    long TileBudgetBytes,
    bool RequireVisibleTilesBeforeCapture,
    bool NotifyFirstFrame,
    bool CaptureBeforePresent = true);
