namespace VisionCanvas.Rendering.Abstractions;

/// <summary>스왑체인 waitable object 등 선택적 프레임 페이싱 훅.</summary>
public interface IFramePacing
{
    FramePacingResult WaitForNextFrame();
}
