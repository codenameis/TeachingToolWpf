namespace VisionAlgorithm.OpenSrc.ShapeDetection;

public sealed record PrimitivePreprocessingDiagnostics(string Device, int Calls, long Pixels,
    double UploadMilliseconds, double KernelMilliseconds, double DownloadMilliseconds);
