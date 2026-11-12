namespace VisionAnomaly.Monitoring;

public sealed record SessionSnapshot(MonitorMode Mode, string Message, int Width = 0, int Height = 0,
    long Sequence = 0, int TrainingFrames = 0, long SkippedFrames = 0, AnomalyPrediction? Prediction = null,
    Guid? ModelId = null, AnalysisRegion? Region = null);
