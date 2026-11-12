namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 상용 정밀 검사 스위트 20종 서브 알고리즘 구분.
/// </summary>
public enum InspectionSuiteAlgorithmKind
{
    Metrology = 0,
    GoldenVariation = 1,
    BlobRle = 2,
    Curvilinear = 3,
    FewShot = 4,
    PatchAnomaly = 5,
    Calibration = 6,
    CornerAlignment = 7,
    // [Backward Compatibility]: 제거된 고전 OCR 엔진 식별자(기존 레시피·문서 역직렬화 호환용이며 실행 시 PpOcrOnnx로 정규화됨).
    [Obsolete("제거된 고전 OCR 엔진입니다. 기존 레시피 역직렬화 하위 호환 전용이며 실행 시 PpOcrOnnx로 정규화됩니다.")]
    IndustrialOcrOcv = 8,
    // [Backward Compatibility]: 제거된 경량 OCR 엔진 식별자(기존 레시피·문서 역직렬화 호환용이며 실행 시 PpOcrOnnx로 정규화됨).
    [Obsolete("제거된 경량 OCR 엔진입니다. 기존 레시피 역직렬화 하위 호환 전용이며 실행 시 PpOcrOnnx로 정규화됩니다.")]
    LightweightOcr = 9,
    AutoTeaching = 10,
    // [Backward Compatibility]: 제거된 CPU 딥러닝 OCR 식별자(기존 레시피·문서 역직렬화 호환용이며 실행 시 PpOcrOnnx로 정규화됨).
    [Obsolete("제거된 CPU 딥러닝 OCR 엔진입니다. 기존 레시피 역직렬화 하위 호환 전용이며 실행 시 PpOcrOnnx로 정규화됩니다.")]
    CpuDeepOcr = 11,
    PreprocessingShadingAndRecon = 12,
    AdvancedMetrologyEllipse = 13,
    AdvancedBlobWatershed = 14,
    DataMatrix2DReader = 15,
    PreprocessingFftAndGabor = 16,
    Barcode1DReader = 17,
    PhotometricStereo = 18,
    GeometricTolerancing = 19,
    PpOcrOnnx = 20,
    QrCode2DReader = 21
}
