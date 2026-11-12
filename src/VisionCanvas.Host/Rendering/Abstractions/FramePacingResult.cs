namespace VisionCanvas.Rendering.Abstractions;

/// <summary>백엔드 프레임 페이싱 결과.</summary>
public readonly record struct FramePacingResult(bool ShouldContinue, double WaitMilliseconds);
