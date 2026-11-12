using IndustrialVision.Filters.Core;

namespace IndustrialVision.Filters.Pipeline;

public sealed record TileExecutionReport(int Tiles, int Halo, int TileWidth, int TileHeight,
    long PeakHostBytes, long PeakGpuBytes, long UploadBytes, long DownloadBytes, IReadOnlyList<StagePlan> Stages);
