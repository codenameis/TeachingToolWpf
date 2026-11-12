namespace VisionCanvas.Rendering.Abstractions;

/// <summary>프레임별 렌더 대상 크기, 슬롯 번호, GPU 완료 후 자원 퇴출을 제공하는 표면 계약.</summary>
public interface IRenderSurface
{
    int Width { get; }

    int Height { get; }

    int FrameIndex { get; }

    void Retire(IDisposable resource);
}
