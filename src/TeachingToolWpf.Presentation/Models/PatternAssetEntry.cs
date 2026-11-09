namespace TeachingToolWpf.Models;

/// <summary>
/// 모델 파일 비대화를 방지하기 위해 픽셀 본문은 개별 바이너리 파일(.pimg)로 분리하고,
/// 매니페스트(manifest.json)에는 가벼운 메타데이터와 외부 이미지 파일 참조만 기록하는 엔트리.
/// </summary>
public sealed record PatternAssetEntry
{
    public required Guid Id { get; init; }
    public required int Index { get; init; }
    public required string Name { get; init; }
    public required PatternMatchingMethod Method { get; init; }
    public required PatternRegistrationRole Role { get; init; }
    public required bool IsEnabled { get; init; }
    public Guid? TeachingRoiId { get; init; }
    public string? TeachingRoiName { get; init; }
    public required double RoiCenterX { get; init; }
    public required double RoiCenterY { get; init; }
    public required double RoiWidth { get; init; }
    public required double RoiHeight { get; init; }
    public required double RoiAngleDegrees { get; init; }
    public required string Summary { get; init; }
    public required string ImageFileName { get; init; }
    public required int PreviewWidth { get; init; }
    public required int PreviewHeight { get; init; }
    public required long ImageByteSize { get; init; }
}
