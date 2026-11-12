namespace TeachingToolWpf.ViewModels;

/// <summary>
/// 실시간 OCR 프리뷰 목록의 ROI별 요약 항목.
/// </summary>
public sealed record OcrLiveRoiSummaryItem(
    Guid RoiId,
    string RoiName,
    string DecodedText,
    string ConfidenceBadge,
    string VerdictBadge,
    string VerdictAccent,
    string CharacterCountBadge);
