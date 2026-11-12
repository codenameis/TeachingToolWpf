using System.Buffers;
using System.Runtime.CompilerServices;

namespace IndustrialVision.Filters.Core;

/// <summary>GPU 구현만 참조하는 중립 팩토리. 지원 불가 사유만 반환하며 구현 오류는 전파한다.</summary>
public interface IGpuBackend
{
    BackendCapabilities GetCapabilities();
    string? GetUnsupportedReason(Recipe recipe, ImageDescriptor input, ExecutionOptions options);
    IGpuProgram Compile(Recipe recipe, ImageDescriptor input, ExecutionOptions options);
}
