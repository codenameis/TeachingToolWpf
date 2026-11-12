namespace CpuMatching.Cuda;

/// <summary>ILGPU의 동적 launcher가 접근하는 값 타입. 픽셀 격자와 CPU SIMD 구간을 전달한다.</summary>
public readonly record struct CudaTemplateScoreGrid(int Width, int Height, int Offset, int Left, int VectorEnd);
