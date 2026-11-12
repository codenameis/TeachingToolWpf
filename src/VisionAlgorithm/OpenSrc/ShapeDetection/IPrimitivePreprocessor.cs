namespace VisionAlgorithm.OpenSrc.ShapeDetection;

/// <summary>원본과 같은 3×3 median, 복제 경계를 적용한다. 반환 버퍼는 호출자가 소유한다.</summary>
public interface IPrimitivePreprocessor : IDisposable
{
    ShapeGrayImage Median3x3(ShapeGrayImage image, CancellationToken token, Memory<byte> destination = default);

    PrimitivePreprocessingDiagnostics Diagnostics
    {
        get;
    }
}
