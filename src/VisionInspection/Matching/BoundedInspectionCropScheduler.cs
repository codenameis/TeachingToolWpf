using System.Diagnostics;
using VisionAlgorithm;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>한 소유자가 원본 crop을 읽고 독립 계산만 제한된 슬롯·픽셀 예산으로 실행한다.</summary>
internal static class BoundedInspectionCropScheduler
{
    internal readonly record struct Limits(int MaximumParallelism, int ParallelPixels, int SingleCropPixels);
    private sealed record Work<TResult>(int Index, long Pixels, Task<TResult> Task);

    public static double Run<TJob, TResult>(IReadOnlyList<TJob> jobs, Func<TJob, long> pixelCount,
        Func<TJob, CancellationToken, GrayImage> read,
        Func<TJob, GrayImage, CancellationToken, TResult> execute, Action<int, TResult> publish,
        Limits limits, CancellationToken token)
    {
        if (limits.MaximumParallelism < 1 || limits.ParallelPixels < 1 || limits.SingleCropPixels < limits.ParallelPixels)
        {
            throw new ArgumentOutOfRangeException(nameof(limits));
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var active = new List<Work<TResult>>();
        var ready = new SortedDictionary<int, TResult>();
        var maximumOutstanding = 2L * limits.MaximumParallelism;
        var activePixels = 0L;
        var nextPublish = 0;
        var readMilliseconds = 0d;
        var readTimer = new Stopwatch();
        try
        {
            for (var index = 0; index < jobs.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var job = jobs[index];
                var pixels = pixelCount(job);
                if (pixels < 1 || pixels > limits.SingleCropPixels)
                {
                    throw new ArgumentException($"검사 crop이 {limits.SingleCropPixels:N0} pixel 작업 예산을 초과했거나 비어 있습니다.", nameof(jobs));
                }
                // 큰 원본 crop 한 개의 예외는 다른 작업이 모두 끝난 경우에만 허용한다.
                // 완료 대기 결과도 슬롯에 포함해 느린 첫 작업 뒤로 결과가 무한히 쌓이지 않게 한다.
                while (active.Count > 0 && (active.Count >= limits.MaximumParallelism ||
                    activePixels + pixels > limits.ParallelPixels || ready.Count + active.Count >= maximumOutstanding))
                {
                    DrainCompleted(true);
                }
                readTimer.Restart();
                var image = read(job, token);
                readMilliseconds += readTimer.Elapsed.TotalMilliseconds;
                if (limits.MaximumParallelism == 1 || jobs.Count == 1)
                {
                    publish(index, execute(job, image, token));
                    nextPublish++;
                }
                else
                {
                    active.Add(new(index, pixels, Task.Run(() => execute(job, image, cancellation.Token), cancellation.Token)));
                    activePixels += pixels;
                    DrainCompleted(false);
                }
            }
            while (active.Count > 0)
            {
                DrainCompleted(true);
            }
            token.ThrowIfCancellationRequested();
            return readMilliseconds;
        }
        catch (Exception failure)
        {
            cancellation.Cancel();
            // 예외·취소에서도 모든 worker의 종료를 관찰한 뒤 reader/device 소유자에게 제어를 돌린다.
            var settled = Task.WhenAll(active.Select(work => work.Task)).ContinueWith(task => task,
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).GetAwaiter().GetResult();
            var additionalFailures = settled.Exception?.Flatten().InnerExceptions
                .Where(error => !ReferenceEquals(error, failure) && error is not OperationCanceledException).ToArray() ?? [];
            if (additionalFailures.Length > 0)
            {
                throw new AggregateException("검사 crop 처리 실패 후 다른 작업에서도 오류가 발생했습니다.", [failure, .. additionalFailures]);
            }
            throw;
        }

        void DrainCompleted(bool wait)
        {
            if (wait && !active.Any(work => work.Task.IsCompleted))
            {
                Task.WaitAny(active.Select(work => work.Task).ToArray(), cancellation.Token);
            }
            for (var index = active.Count - 1; index >= 0; index--)
            {
                var work = active[index];
                if (!work.Task.IsCompleted)
                {
                    continue;
                }
                var result = work.Task.GetAwaiter().GetResult();
                ready.Add(work.Index, result);
                activePixels -= work.Pixels;
                active.RemoveAt(index);
            }
            while (ready.Remove(nextPublish, out var completion))
            {
                publish(nextPublish, completion);
                nextPublish++;
            }
        }
    }
}
