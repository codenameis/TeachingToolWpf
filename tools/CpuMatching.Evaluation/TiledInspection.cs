using System.Diagnostics;
using CpuMatching.Core;

namespace CpuMatching.Evaluation;

// 위치 보정이 끝난 이미지의 분할 실험. 제품의 기본 전체 검색 경로는 바꾸지 않는다.
internal static class TiledInspection
{
    internal sealed record Tile(SearchRegion Core, SearchRegion Read, SearchRegion Accept);
    internal sealed record TileResult(Tile Tile, SearchResult Search, double Milliseconds);
    internal sealed record RunResult(SearchResult Search, TileResult[] Tiles, double MergeMilliseconds);

    public static Tile[] Plan(GrayImage image, GrayImage template, SearchOptions options, int count, bool boundaryPadding)
    {
        options.Validate();
        if (count is not (8 or 16) || options.Region is not null)
        {
            throw new ArgumentException("Split experiment requires the full image and 8 or 16 tiles.");
        }
        const int Columns = 4;
        var rows = count / Columns;
        var phase = 1 << (options.PyramidLevels - 1);
        var radius = Math.Sqrt((template.Width * template.Width) + (template.Height * template.Height)) * options.MaximumScale / 2;
        // 축소 단계의 거리 맵(32), 미분 여백 및 위치 정밀화 이동을 원본 픽셀로 확보한다.
        var halo = boundaryPadding ? (int)Math.Ceiling(radius + (48 * phase) + options.SuppressionRadius) : 0;
        var overlap = boundaryPadding ? (int)Math.Ceiling(options.SuppressionRadius) : 0;
        var result = new Tile[count];
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < Columns; column++)
            {
                var left = column * image.Width / Columns;
                var top = row * image.Height / rows;
                var right = (column + 1) * image.Width / Columns;
                var bottom = (row + 1) * image.Height / rows;
                var core = new SearchRegion(left, top, right - left, bottom - top);
                var readLeft = Math.Max(0, left - halo) / phase * phase;
                var readTop = Math.Max(0, top - halo) / phase * phase;
                var crop = new SearchRegion(readLeft, readTop,
                    0, 0);
                crop = crop with
                {
                    Width = Math.Min(image.Width, (int)Math.Ceiling((right + halo) / (double)phase) * phase) - crop.X,
                    Height = Math.Min(image.Height, (int)Math.Ceiling((bottom + halo) / (double)phase) * phase) - crop.Y
                };
                crop.Validate(image);
                // 경계에 걸친 중심은 양쪽에서 검증하고 마지막에 같은 NMS로 합친다.
                var accept = new SearchRegion(Math.Max(0, left - overlap), Math.Max(0, top - overlap), 0, 0);
                accept = accept with
                {
                    Width = Math.Min(image.Width, right + overlap) - accept.X,
                    Height = Math.Min(image.Height, bottom + overlap) - accept.Y
                };
                result[(row * Columns) + column] = new(core, crop, accept);
            }
        }
        return result;
    }

    public static RunResult Find(GrayImage image, GrayImage template, ITemplateMatcher[] matchers,
        SearchOptions options, int workers, bool boundaryPadding, CancellationToken token)
    {
        if (workers is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(workers));
        }
        var tiles = Plan(image, template, options, matchers.Length, boundaryPadding);
        var results = new TileResult[tiles.Length];
        Parallel.For(0, tiles.Length, new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = token }, index =>
        {
            var tile = tiles[index];
            var watch = Stopwatch.StartNew();
            // Find가 실제 픽셀 crop/피라미드/맵을 준비한다. 중첩 병렬화 없이 바깥 작업만 병렬이다.
            var result = matchers[index].Find(image, options with
            {
                Region = tile.Read,
                MaximumDegreeOfParallelism = 1
            }, token,
                (x, y) => Contains(tile.Accept, x, y));
            results[index] = new(tile, result, watch.Elapsed.TotalMilliseconds);
        });
        token.ThrowIfCancellationRequested();
        var merge = Stopwatch.StartNew();
        var matches = new List<Match>();
        foreach (var match in results.SelectMany(tile => tile.Search.Matches)
            .OrderByDescending(match => match.Score).ThenBy(match => match.Y).ThenBy(match => match.X))
        {
            token.ThrowIfCancellationRequested();
            if (!matches.Any(other => ((other.X - match.X) * (other.X - match.X)) +
                ((other.Y - match.Y) * (other.Y - match.Y)) < options.SuppressionRadius * options.SuppressionRadius))
            {
                matches.Add(match);
            }
        }
        var search = new SearchResult(matches.Take(options.MaximumMatches).ToArray(),
            results.Any(tile => tile.Search.CandidatesTruncated),
            results.Any(tile => tile.Search.MatchLimitExceeded) || matches.Count > options.MaximumMatches,
            results.Sum(tile => tile.Search.Evaluations))
        {
            CandidateCount = results.Sum(tile => tile.Search.CandidateCount),
            CandidateBatches = results.Sum(tile => tile.Search.CandidateBatches)
        };
        return new(search, results, merge.Elapsed.TotalMilliseconds);
    }

    internal static bool Contains(SearchRegion region, double x, double y)
        => x >= region.X && y >= region.Y && x < region.X + region.Width && y < region.Y + region.Height;
}
