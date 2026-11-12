namespace CpuMatching.Core;

public sealed record CoarseScoreDiagnostics(string Device, double InitializationMilliseconds, int Calls,
    long Evaluations, double UploadMilliseconds, double ComputeMilliseconds, double DownloadMilliseconds);
