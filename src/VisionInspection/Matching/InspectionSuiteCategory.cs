namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 상용 정밀 검사 스위트 상위 기능 카테고리 구분.
/// </summary>
public enum InspectionSuiteCategory
{
    All = 0,
    MetrologyAndGeometry = 1,
    SurfaceBlobAnd3D = 2,
    Ocr = 3,
    AiAndAutoTeaching = 4,
    PreprocessingAndFrequency = 5,
    Barcode1D2D = 6,
    CodeAndOcr = Ocr
}
