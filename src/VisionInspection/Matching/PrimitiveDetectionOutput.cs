using System.Collections.Immutable;
using System.Diagnostics;
using VisionAlgorithm;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionAlgorithm.OpenSrc.ShapeDetection;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Matching;

public sealed record PrimitiveDetectionOutput(ImmutableArray<PrimitiveShapeDetection> Shapes, int Windows,
    bool Incomplete, int ClippedContours, double Milliseconds)
{
    public double ReadMilliseconds
    {
        get; init;
    }
    /// <summary>각 창 작업의 시간 합. 병렬 실행과 GPU 잠금 대기를 포함하므로 실제 경과 시간이 아니다.</summary>
    public double DetectionWorkMilliseconds
    {
        get; init;
    }
    /// <summary>병렬 검출과 결과 병합의 실제 경과 시간. 원본 읽기와 장치 초기화는 제외한다.</summary>
    public double DetectionMilliseconds
    {
        get; init;
    }
    public double ComputeInitializationMilliseconds
    {
        get; init;
    }
    public PrimitivePreprocessingDiagnostics? Gpu
    {
        get; init;
    }
}
