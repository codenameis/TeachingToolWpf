using System.Collections.Immutable;
using System.Numerics;
using VisionGeometry;

namespace VisionCanvas.Rendering;

/// <summary>해당 overlay의 필수 라벨 준비·Present 제출·GPU fence 완료. 물리 화면 scan-out은 아니다.</summary>
public sealed record OverlayPresentation(Guid PresentationId, long CompletedTimestamp);
