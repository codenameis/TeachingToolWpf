namespace VisionAnomaly;

public readonly record struct AnalysisRegion(double X, double Y, double Width, double Height)
{
    public static AnalysisRegion Full => new(0, 0, 1, 1);

    public void Validate()
    {
        if (!double.IsFinite(X + Y + Width + Height) || X < 0 || Y < 0 ||
            Width <= 0 || Height <= 0 || X + Width > 1.000001 || Y + Height > 1.000001)
        {
            throw new ArgumentException("관심 영역은 영상 안의 양수 크기여야 합니다.");
        }
    }
}

public enum VideoSourceKind
{
    File, Camera, Stream
}

public sealed record VideoSourceSpec(VideoSourceKind Kind, string Address)
{
    public string DisplayName => Kind switch
    {
        VideoSourceKind.File => Path.GetFileName(Address),
        VideoSourceKind.Camera => $"USB 카메라 {Address}",
        _ => Uri.TryCreate(Address, UriKind.Absolute, out var uri) ? $"{uri.Scheme}://{uri.Host}" : "네트워크 영상"
    };

    public void Validate()
    {
        if (!Enum.IsDefined(Kind) || string.IsNullOrWhiteSpace(Address))
        {
            throw new ArgumentException("영상 입력을 지정하세요.");
        }

        if (Kind == VideoSourceKind.File && !System.IO.File.Exists(Address))
        {
            throw new FileNotFoundException("영상 파일을 찾을 수 없습니다.");
        }

        if (Kind == VideoSourceKind.Camera && (!int.TryParse(Address, out var camera) || camera < 0))
        {
            throw new ArgumentException("USB 카메라 번호는0 이상의 정수입니다.");
        }

        if (Kind == VideoSourceKind.Stream && (!Uri.TryCreate(Address, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("rtsp" or "http" or "https")))
        {
            throw new ArgumentException("RTSP 또는 HTTP(S) 영상 주소를 입력하세요.");
        }
    }
}

public sealed record AnomalyArea(double X, double Y, double Width, double Height, float Ratio);
public sealed record AnomalyPrediction(float AppearanceRatio, float MotionRatio, bool MotionAvailable,
    IReadOnlyList<AnomalyArea> Areas, double ProcessingMilliseconds)
{
    public IReadOnlyList<AnomalyArea> MotionAreas { get; init; } = [];
}
public enum AnomalyKind
{
    Appearance, Motion
}
public enum ReviewStatus
{
    Pending, Acknowledged, Normal
}

public sealed record AlarmRecord(Guid Id, string CameraName, Guid ModelId, long Sequence,
    DateTimeOffset OccurredAt, double MediaSeconds, AnomalyKind Kind, float Ratio,
    IReadOnlyList<AnomalyArea> Areas, string EvidenceFile, ReviewStatus Review = ReviewStatus.Pending);
