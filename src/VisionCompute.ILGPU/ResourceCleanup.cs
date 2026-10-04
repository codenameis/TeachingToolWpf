using System.Runtime.ExceptionServices;

namespace VisionCompute.ILGPU;

/// <summary>일부 native 해제가 실패해도 남은 소유 자원과 풀 대여물을 모두 정리한다.</summary>
internal static class ResourceCleanup
{
    public static void Complete(Exception? originalFailure, params Action[] cleanupActions)
    {
        List<Exception>? failures = originalFailure is null ? null : [originalFailure];
        foreach (var cleanup in cleanupActions)
        {
            try
            {
                cleanup();
            }
            catch (Exception failure)
            {
                (failures ??= []).Add(failure);
            }
        }
        if (failures is { Count: 1 })
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }
        if (failures is { Count: > 1 })
        {
            throw new AggregateException("자원 정리 중 하나 이상의 오류가 발생했습니다.", failures);
        }
    }
}
