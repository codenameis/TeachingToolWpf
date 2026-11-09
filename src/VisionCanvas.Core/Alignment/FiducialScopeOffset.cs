namespace VisionCanvas.Core.Alignment;

/// <summary>계층 범위(전체·모듈·컴포넌트)의 로컬 보정 입력값(픽셀 이동 및 회전 각도).</summary>
public readonly record struct FiducialScopeOffset
{
    private readonly bool _disabled;

    public double DeltaX { get; }

    public double DeltaY { get; }

    public double DeltaThetaDegrees { get; }

    /// <summary>기본값(default)에서도 보정마크가 활성화된 상태로 동작하도록 보장한다.</summary>
    public bool IsEnabled => !_disabled;

    public bool IsIdentity => !IsEnabled || (DeltaX == 0 && DeltaY == 0 && DeltaThetaDegrees == 0);

    public FiducialScopeOffset(double deltaX, double deltaY, double deltaThetaDegrees, bool isEnabled = true)
    {
        if (!double.IsFinite(deltaX))
        {
            throw new ArgumentOutOfRangeException(nameof(deltaX), "X 보정값은 유한한 픽셀 값이어야 합니다.");
        }

        if (!double.IsFinite(deltaY))
        {
            throw new ArgumentOutOfRangeException(nameof(deltaY), "Y 보정값은 유한한 픽셀 값이어야 합니다.");
        }

        if (!double.IsFinite(deltaThetaDegrees))
        {
            throw new ArgumentOutOfRangeException(nameof(deltaThetaDegrees), "회전 보정값은 유한한 각도(°)여야 합니다.");
        }

        DeltaX = deltaX;
        DeltaY = deltaY;
        DeltaThetaDegrees = deltaThetaDegrees;
        _disabled = !isEnabled;
    }

    public static FiducialScopeOffset Identity => new(0, 0, 0, isEnabled: true);

    public FiducialScopeOffset WithEnabled(bool isEnabled) => new(DeltaX, DeltaY, DeltaThetaDegrees, isEnabled);
}
