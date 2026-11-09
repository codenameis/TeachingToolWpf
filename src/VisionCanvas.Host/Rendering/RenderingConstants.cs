using System.Numerics;

namespace VisionCanvas.Rendering;

/// <summary>
/// 화면 렌더링, 뷰포트 레이아웃, 오버레이 및 텍스처 패킹에 사용되는 도메인 상수 모음.
/// </summary>
internal static class RenderingConstants
{
    // ==========================================
    // 색상 팔레트 및 투명도 (RGBA)
    // ==========================================

    /// <summary>표준 어두운 오버레이/패널 배경색 (R:0.13, G:0.16, B:0.22, A:0.90).</summary>
    public static readonly Vector4 DarkPanelBackground = new(0.13f, 0.16f, 0.22f, 0.90f);

    /// <summary>텍스트 배너용 불투명 어두운 배경색 (R:0.13, G:0.16, B:0.22, A:0.94).</summary>
    public static readonly Vector4 DarkBannerBackground = new(0.13f, 0.16f, 0.22f, 0.94f);

    /// <summary>밝은 배경 위의 어두운 고대비 전경색 (R:0.05, G:0.07, B:0.09, A:1.0).</summary>
    public static readonly Vector4 DarkHighContrastForeground = new(0.05f, 0.07f, 0.09f, 1.0f);

    /// <summary>보정마크 기준(Reference) 십자선 색상 (황금색 계열).</summary>
    public static readonly Vector4 FiducialReferenceColor = new(1.0f, 0.75f, 0.12f, 0.95f);

    /// <summary>보정마크 검출(Detected) 십자선/벡터 색상 (밝은 에메랄드 녹색 계열).</summary>
    public static readonly Vector4 FiducialDetectedColor = new(0.12f, 0.92f, 0.45f, 1.0f);

    /// <summary>보정마크 비활성화 색상 (은회색 계열).</summary>
    public static readonly Vector4 FiducialDisabledColor = new(0.65f, 0.68f, 0.72f, 0.80f);

    /// <summary>미니맵 강조(Accent) 색상 (스카이블루 계열).</summary>
    public static readonly Vector4 MinimapAccentColor = new(0.22f, 0.52f, 1.0f, 1.0f);

    /// <summary>미니맵 패널 배경색 (R:0.10, G:0.13, B:0.18, A:0.98).</summary>
    public static readonly Vector4 MinimapPanelBackground = new(0.10f, 0.13f, 0.18f, 0.98f);

    /// <summary>미니맵 테두리 색상 (R:0.38, G:0.44, B:0.55, A:1.0).</summary>
    public static readonly Vector4 MinimapBorderColor = new(0.38f, 0.44f, 0.55f, 1.0f);

    /// <summary>미니맵 이미지 영역 배경색 (R:0.04, G:0.05, B:0.07, A:1.0).</summary>
    public static readonly Vector4 MinimapImageBackground = new(0.04f, 0.05f, 0.07f, 1.0f);

    /// <summary>드래그 채우기 기본 알파 (8% 투명도).</summary>
    public const float DragFillAlpha = 0.08f;

    /// <summary>그림자 기본 알파 (22% 투명도).</summary>
    public const float ShadowAlpha = 0.22f;

    /// <summary>비선택 요약 ROI 투명도 (50%).</summary>
    public const float UnselectedSummaryOpacity = 0.5f;

    // ==========================================
    // ITU-R BT.601 휘도 가중치 (Luminance)
    // ==========================================

    /// <summary>BT.601 빨간색 휘도 가중치 (0.299).</summary>
    public const float Bt601LuminanceRed = 0.299f;

    /// <summary>BT.601 녹색 휘도 가중치 (0.587).</summary>
    public const float Bt601LuminanceGreen = 0.587f;

    /// <summary>BT.601 파란색 휘도 가중치 (0.114).</summary>
    public const float Bt601LuminanceBlue = 0.114f;

    /// <summary>밝은 배경 판정 임계치 (휘도 0.55 초과 시 어두운 텍스트 사용).</summary>
    public const float LightBackgroundThreshold = 0.55f;

    // ==========================================
    // 뷰포트 레이아웃 및 여백 (단위: DIP 또는 픽셀)
    // ==========================================

    /// <summary>화면 밖 렌더링 컬링 여유 버퍼 (64px).</summary>
    public const double ViewportCullingMarginPixels = 64.0;

    /// <summary>뷰포트 화면 패닝 시 라벨 재구축을 지연시키는 패닝 이동 임계치 (64px).</summary>
    public const double LabelPanInvalidationThresholdPixels = 64.0;

    /// <summary>화면에 한 번에 표시할 수 있는 최대 라벨 수 (256개).</summary>
    public const int MaxScreenLabelCount = 256;

    /// <summary>라벨 점유 격자 셀 너비 (128px).</summary>
    public const double LabelOccupancyCellWidth = 128.0;

    /// <summary>라벨 점유 격자 셀 높이 (32px).</summary>
    public const double LabelOccupancyCellHeight = 32.0;

    /// <summary>단일 라벨 최대 표시 너비 (430px).</summary>
    public const double MaxLabelWidth = 430.0;

    /// <summary>라벨 상단 앵커 오프셋 (30px 위).</summary>
    public const double LabelAnchorTopOffset = 30.0;

    /// <summary>화면 경계 라벨 최소 좌측 여백 (4px).</summary>
    public const double LabelScreenMarginLeft = 4.0;

    /// <summary>화면 경계 라벨 최소 우측 안전 여백 (100px).</summary>
    public const double LabelScreenSafetyMarginRight = 100.0;

    /// <summary>보정마크 십자선 팔 길이 (8px).</summary>
    public const double FiducialArmLength = 8.0;

    /// <summary>보정마크 기준점 원 반경 (4px).</summary>
    public const double FiducialReferenceRadius = 4.0;

    /// <summary>보정마크 검출점 원 반경 (4.5px).</summary>
    public const double FiducialDetectedRadius = 4.5;

    /// <summary>보정마크 유의미한 변위 선 표시 최소 길이 (0.5px).</summary>
    public const double FiducialMinDisplacementDistance = 0.5;

    /// <summary>선택 핸들 외곽 기본 크기 (4px).</summary>
    public const double HandleOuterRadius = 4.0;

    /// <summary>선택 핸들 내부 기본 크기 (3px).</summary>
    public const double HandleInnerRadius = 3.0;

    /// <summary>회전 핸들 외곽 기본 크기 (5px).</summary>
    public const double RotationHandleOuterRadius = 5.0;

    /// <summary>회전 핸들 내부 기본 크기 (3.5px).</summary>
    public const double RotationHandleInnerRadius = 3.5;

    // ==========================================
    // 검사 결과 오버레이 시각화 (ResultOverlayPainter)
    // ==========================================

    /// <summary>검출 기준점 십자선 마크의 화면 팔 길이 (7px).</summary>
    public const double ResultMarkerCrossArmLength = 7.0;

    /// <summary>검출 방향 화살표 본체 길이 (26px).</summary>
    public const double ResultMarkerArrowLength = 26.0;

    /// <summary>검출 방향 화살표 머리 날개 길이 (6px).</summary>
    public const double ResultMarkerArrowHeadLength = 6.0;

    /// <summary>검출 방향 화살표 머리 폭 (4px).</summary>
    public const double ResultMarkerArrowHeadWidth = 4.0;

    /// <summary>결과 라벨 화면 상단 최소 안전 여백 (48px).</summary>
    public const double ResultLabelMinTopMargin = 48.0;

    /// <summary>결과 라벨 외곽 패딩 (좌우 6px, 상하 4px).</summary>
    public const double ResultLabelPaddingHorizontal = 6.0;

    /// <summary>결과 라벨 외곽 패딩 (상하 4px).</summary>
    public const double ResultLabelPaddingVertical = 4.0;
}
