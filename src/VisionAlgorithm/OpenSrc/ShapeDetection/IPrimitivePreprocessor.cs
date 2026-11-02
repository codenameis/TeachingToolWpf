namespace VisionAlgorithm.OpenSrc.ShapeDetection;

/// <summary>원본과 같은 3×3 median, 복제 경계를 적용한다. 반환 버퍼는 호출자가 소유한다.</summary>
public interface IPrimitivePreprocessor : IDisposable
{
    GrayImage Median3x3(GrayImage image, CancellationToken token, Memory<byte> destination = default);

    PrimitivePreprocessingDiagnostics Diagnostics
    {
        get;
    }
}

public sealed record PrimitivePreprocessingDiagnostics(string Device, int Calls, long Pixels,
    double UploadMilliseconds, double KernelMilliseconds, double DownloadMilliseconds);
