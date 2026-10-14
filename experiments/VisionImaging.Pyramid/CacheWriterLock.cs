namespace VisionImaging.Pyramid;

/// <summary>같은 캐시의 준비·게시만 직렬화한다. 열린 타일 source의 읽기는 기다리지 않는다.</summary>
internal static class CacheWriterLock
{
    public static FileStream Acquire(string path, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1);
            }
            catch (IOException error) when ((error.HResult & 0xffff) is 11 or 32 or 33)
            {
                // FileShare 충돌만 기다린다. 권한·디스크 등 다른 오류는 즉시 호출자에게 전달한다.
                if (token.WaitHandle.WaitOne(25))
                {
                    token.ThrowIfCancellationRequested();
                }
            }
        }
    }
}
