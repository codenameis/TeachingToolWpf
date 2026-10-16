namespace CpuMatching.Core;

/// <summary>전체 후보 탐색 이후의 원본/중간 점수 맵만 필요한 주변에 준비한다.</summary>
internal sealed class LocalScoreLevel(GrayImage image, int margin, Func<GrayImage, IScoreLevel> prepare, CancellationToken token,
    bool reuseIdenticalTiles = false) : IScoreLevel
{
    internal const int CellSize = 256;
    private const int MaximumCacheBytes = 512 * 1024 * 1024;
    private const int MaximumCachedTiles = 32;
    // 방향 맵16개(float)와 원본 gradient 두 성분(double)의 대략적인 크기다.
    private const int EstimatedBytesPerPixel = (16 * sizeof(float)) + (2 * sizeof(double));
    private readonly Dictionary<(int X, int Y), Tile> _tiles = new();
    private readonly Queue<(int X, int Y)> _order = new();
    private readonly Queue<Tile> _contentMaps = new();
    private readonly int _capacity = Math.Clamp(MaximumCacheBytes /
        checked((CellSize + (2 * margin)) * (CellSize + (2 * margin)) * EstimatedBytesPerPixel), 1, MaximumCachedTiles);

    public bool SupportsParallelEvaluation => true;

    public IPoseScore AtPose(double angleDegrees, double scale) => new Pose(this, angleDegrees, scale);

    public double Verify(Match match)
    {
        var tile = GetTile(match.X, match.Y);
        var local = match with
        {
            X = match.X - tile.Left,
            Y = match.Y - tile.Top
        };
        if (tile.Level.SupportsParallelEvaluation)
        {
            return tile.Level.Verify(local);
        }
        lock (_tiles)
        {
            return tile.Level.Verify(local);
        }
    }

    private Tile GetTile(double x, double y)
    {
        var key = (X: Math.Clamp((int)x, 0, image.Width - 1) / CellSize, Y: Math.Clamp((int)y, 0, image.Height - 1) / CellSize);
        lock (_tiles)
        {
            token.ThrowIfCancellationRequested();
            if (_tiles.TryGetValue(key, out var cached))
            {
                return cached;
            }
            var left = Math.Max(0, (key.X * CellSize) - margin);
            var top = Math.Max(0, (key.Y * CellSize) - margin);
            var right = Math.Min(image.Width, ((key.X + 1) * CellSize) + margin);
            var bottom = Math.Min(image.Height, ((key.Y + 1) * CellSize) + margin);
            var pixels = image.Crop(new(left, top, right - left, bottom - top));
            var hash = 0;
            Tile? identical = null;
            if (reuseIdenticalTiles)
            {
                var fingerprint = new HashCode();
                fingerprint.AddBytes(pixels.Pixels.Span);
                hash = fingerprint.ToHashCode();
                // 해시는 비교 대상을 줄일 뿐이다. 크기와 모든 픽셀이 같아야 맵을 공유한다.
                identical = _contentMaps.FirstOrDefault(tile => tile.Level.SupportsParallelEvaluation &&
                    tile.Hash == hash && tile.Pixels.Width == pixels.Width && tile.Pixels.Height == pixels.Height &&
                    tile.Pixels.Pixels.Span.SequenceEqual(pixels.Pixels.Span));
            }
            var tile = new Tile(left, top, identical?.Level ?? prepare(pixels), identical?.Pixels ?? pixels, hash);
            if (reuseIdenticalTiles && identical is null)
            {
                if (_contentMaps.Count == _capacity)
                {
                    _contentMaps.Dequeue();
                    // 위치 별칭이 퇴출된 맵을 붙잡아 실제 맵 예산을 넘지 않도록 비운다.
                    _tiles.Clear();
                    _order.Clear();
                }
                // 같은 맵을 가리키는 여러 위치가 서로 다른 맵의 자리를 차지하지 않는다.
                _contentMaps.Enqueue(tile);
            }
            if (_tiles.Count == _capacity)
            {
                _tiles.Remove(_order.Dequeue());
            }
            _tiles.Add(key, tile);
            _order.Enqueue(key);
            return tile;
        }
    }

    private sealed record Tile(int Left, int Top, IScoreLevel Level, GrayImage Pixels, int Hash);

    private sealed class Pose(LocalScoreLevel owner, double angle, double scale) : IPoseScore
    {
        public double Score(double x, double y)
        {
            var tile = owner.GetTile(x, y);
            if (tile.Level.SupportsParallelEvaluation)
            {
                return tile.Level.AtPose(angle, scale).Score(x - tile.Left, y - tile.Top);
            }
            // 외부 점수 전략의 기본 순차 계약도 감싼 레벨 안에서 유지한다.
            lock (owner._tiles)
            {
                return tile.Level.AtPose(angle, scale).Score(x - tile.Left, y - tile.Top);
            }
        }
    }
}
