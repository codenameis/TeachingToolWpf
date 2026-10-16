using AnomalyMonitor.Infrastructure;
using VisionAnomaly;

namespace AnomalyMonitor.ViewModels;

public sealed class AlarmViewModel(AlarmRecord record) : ObservableObject
{
    public AlarmRecord Record { get; private set; } = record;
    public string Time => Record.OccurredAt.ToLocalTime().ToString("HH:mm:ss");
    public string ShortTitle => Record.Kind == AnomalyKind.Appearance ? "외관 변화" : "움직임 변화";
    public string Title => Record.Kind == AnomalyKind.Appearance ? "정상과 다른 외관" : "평소와 다른 움직임";
    public string CameraName => Record.CameraName;
    public string Description => Record.Kind == AnomalyKind.Appearance
        ? "정상 패치와의 특징 거리가 기준을 넘었습니다. 단일 분석 프레임도 기록합니다."
        : "이동 속도·방향의 정상 통계에서 벗어난 상태가0.6초 이상 이어졌습니다.";
    public string Score => $"{Record.Ratio:F2} × 정상 기준";
    public string EvidenceInfo => $"프레임 #{Record.Sequence} · 영상{Record.MediaSeconds:F1}초 · 모델{Record.ModelId.ToString()[..8]}";
    public string Status => Record.Review switch { ReviewStatus.Pending => "미확인", ReviewStatus.Normal => "정상으로 기록", _ => "확인 완료" };
    public bool IsPending => Record.Review == ReviewStatus.Pending;
    public string EvidenceFile => Record.EvidenceFile;
    public void SetReview(ReviewStatus status)
    {
        Record = Record with
        {
            Review = status
        };
        Changed(nameof(Status));
        Changed(nameof(IsPending));
    }
}
