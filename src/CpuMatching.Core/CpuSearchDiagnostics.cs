using System.Diagnostics;

namespace CpuMatching.Core;

// 외부 벤치마크가 구독할 때만 전체 후보 동일성과 맵 준비 비용을 기록한다.
internal static class CpuSearchDiagnostics
{
    internal static DiagnosticListener Listener { get; } = new("CpuMatching.Search");

    internal static void Write(string name, object value)
    {
        if (Listener.IsEnabled(name))
        {
            Listener.Write(name, value);
        }
    }
}
