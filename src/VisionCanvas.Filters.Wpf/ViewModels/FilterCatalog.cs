using VisionCanvas.Core.Editing;

namespace VisionCanvas.Filters.Wpf.ViewModels;

/// <summary>필터 미리보기 UI에서 공용으로 참조하는 필터 목록, 실행 장치 목록, 임계값 캡션 카탈로그.</summary>
public static class FilterCatalog
{
    public static IReadOnlyList<FilterOption> Options { get; } =
    [
        new(RoiFilterKind.None, "없음 · 원본"),
        new(RoiFilterKind.Binarize, "Binarize · 이진화 (OpenCV)"),
        new(RoiFilterKind.Edge, "Edge · Canny (OpenCV)"),
        new(RoiFilterKind.Gaussian, "Gaussian · 평활화"),
        new(RoiFilterKind.Erosion, "Erosion · 침식"),
        new(RoiFilterKind.Dilation, "Dilation · 팽창"),
        new(RoiFilterKind.Opening, "Opening · 열기"),
        new(RoiFilterKind.Closing, "Closing · 닫기"),
        new(RoiFilterKind.WhiteTopHat, "White Top-hat · 밝은 결함"),
        new(RoiFilterKind.BlackHat, "Black-hat · 어두운 결함"),
        new(RoiFilterKind.NativeThreshold, "Threshold · 원본 명암 임계값"),
        new(RoiFilterKind.GaussianBlackHatThreshold, "Gaussian → Black-hat → Threshold"),
        new(RoiFilterKind.Clahe, "[전처리] CLAHE · 적응형 히스토그램 대비 향상"),
        new(RoiFilterKind.GuidedFilter, "[전처리] Guided Filter · 엣지 보존 평활화"),
        new(RoiFilterKind.Bilateral, "[전처리] Bilateral · 양방향 노이즈 제거"),
        new(RoiFilterKind.Median, "[전처리] Median · 임펄스 점 노이즈 제거"),
        new(RoiFilterKind.AdaptiveThreshold, "[전처리] Adaptive Sauvola · 적분영상 지역 이진화"),
        new(RoiFilterKind.UnsharpMask, "[전처리] Unsharp Mask · 미세 엣지 선명화"),
        new(RoiFilterKind.SobelGradient, "[전처리] Sobel Gradient · 방향 경계 강도"),
        new(RoiFilterKind.WarpAffineShear, "[워핑] Affine & Shear · 회전/전단 보정"),
        new(RoiFilterKind.WarpPerspectiveKeystone, "[워핑] Perspective Keystone · 원근 사다리꼴 보정"),
        new(RoiFilterKind.WarpPolarUnwrap, "[워핑] Polar Ring Unwrap · 원형 링 직선 전개"),
        new(RoiFilterKind.WarpLensUndistort, "[워핑] Lens Undistort · 방사/접선 렌즈 왜곡 보정"),
        new(RoiFilterKind.FftNotch, "[주파수] 2D FFT Periodic Notch · 모아레/메쉬 격자 제거"),
        new(RoiFilterKind.Homomorphic, "[조명분리] Homomorphic · 로그 조명·반사율 분리"),
        new(RoiFilterKind.GaborTexture, "[텍스처] 2D Gabor Filter · 방향성 스크래치/크랙 강조")
    ];

    public static IReadOnlyList<RoiFilterExecution> ExecutionOptions { get; } = Enum.GetValues<RoiFilterExecution>();

    public static string GetThresholdCaption(RoiFilterKind kind) => kind switch
    {
        RoiFilterKind.Binarize => "이진화 임계값 · 0–255",
        RoiFilterKind.WarpAffineShear => "어파인 회전 제어 · 0–255 (128 = 0°)",
        RoiFilterKind.WarpPerspectiveKeystone => "키스톤 수평 원근 제어 · 0–255 (128 = 대칭)",
        RoiFilterKind.WarpPolarUnwrap => "극좌표 링 내경 비율 · 0–255",
        RoiFilterKind.WarpLensUndistort => "렌즈 방사 왜곡 계수 k1 · 0–255 (128 = 0)",
        RoiFilterKind.FftNotch => "FFT 피크 감도 스케일 · 0–255 (128 = 4.5배)",
        RoiFilterKind.Homomorphic => "저주파 조명 감쇠율 γL · 0–255 (90 = 0.35)",
        RoiFilterKind.GaborTexture => "Gabor 방향 각도 θ · 0–255 (0°–180°)",
        _ => "원본 명암 임계값 · Gray8 0–255 / Gray16 0–65535"
    };
}
