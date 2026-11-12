using System.Buffers;
using System.Runtime.CompilerServices;

namespace IndustrialVision.Filters.Core;

/// <summary>호스트 plan이 소유. 모든 메서드는 동기 완료하며 단일 호출자만 사용한다.</summary>
public interface IGpuProgram : IDisposable
{
    long WorkingBytes
    {
        get;
    }
    ReadOnlyMemory<double> StageMilliseconds
    {
        get;
    }
    long Upload(ImageView input);
    void Run(CancellationToken cancellationToken);
    long Download(HostImage output);
}
