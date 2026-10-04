using System.Collections.Immutable;

namespace VisionAlgorithm.OpenSrc.PatternMatching;

/// <summary>원본 Gray8 템플릿과 이진 마스크. 회전·배율을 고정한 평행이동 검색에 사용한다.</summary>
public sealed class CorrelationTemplate
{
    public const int MaximumTemplatePixels = 8_388_608;
    public const int MaximumSearchPixels = 16_777_216;
    public Guid Id { get; } = Guid.NewGuid();
    public int Width
    {
        get;
    }
    public int Height
    {
        get;
    }
    public ImagePoint ReferencePoint
    {
        get;
    }
    public double ReferenceAngle
    {
        get;
    }
    public long EstimatedBytes => Pixels.LongLength + Mask.LongLength;
    internal byte[] Pixels
    {
        get;
    }
    internal byte[] Mask
    {
        get;
    }

    private CorrelationTemplate(byte[] pixels, byte[] mask, int width, int height, TrainingRegion region)
    {
        Pixels = pixels;
        Mask = mask;
        Width = width;
        Height = height;
        ReferencePoint = region.ReferencePoint;
        ReferenceAngle = region.ReferenceAngle;
    }

    public static CorrelationTemplate Train(GrayImage image, TrainingRegion region, CancellationToken token = default)
    {
        if ((long)image.Width * image.Height > MaximumTemplatePixels ||
            (!region.Mask.IsEmpty && region.Mask.Length != checked(image.Width * image.Height)) ||
            !double.IsFinite(region.ReferencePoint.X) || !double.IsFinite(region.ReferencePoint.Y) ||
            !double.IsFinite(region.ReferenceAngle) || region.ReferencePoint.X < 0 || region.ReferencePoint.Y < 0 ||
            region.ReferencePoint.X > image.Width || region.ReferencePoint.Y > image.Height)
        {
            throw new ArgumentException("템플릿 크기·마스크·기준점이 유효하지 않습니다.");
        }
        var pixels = new byte[checked(image.Width * image.Height)];
        var mask = region.Mask.IsEmpty ? [] : region.Mask.ToArray();
        long sum = 0;
        long squared = 0;
        long count = 0;
        for (var y = 0; y < image.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            image.Pixels.Span.Slice(y * image.Stride, image.Width).CopyTo(pixels.AsSpan(y * image.Width));
            for (var x = 0; x < image.Width; x++)
            {
                var index = (y * image.Width) + x;
                if (mask.Length != 0 && mask[index] == 0)
                {
                    continue;
                }
                var value = pixels[index];
                sum += value;
                squared += value * value;
                count++;
            }
        }
        if (count < 4 || (squared - ((double)sum * sum / count)) / count < 1)
        {
            throw new ArgumentException("템플릿의 유효 픽셀에 충분한 명암 변화가 없습니다.");
        }
        if (count == pixels.Length)
        {
            mask = [];
        }
        return new(pixels, mask, image.Width, image.Height, region);
    }
}

public sealed record CorrelationMatch(ImagePoint ReferencePoint, double Score);
public sealed record CorrelationResult(ImmutableArray<CorrelationMatch> Matches, bool Truncated, double Milliseconds,
    bool MatchLimitExceeded = false);
public sealed record CorrelationSuppressionResult(ImmutableArray<CorrelationMatch> Matches, bool MatchLimitExceeded);

public static class CorrelationMatcher
{
    public static CorrelationResult Search(GrayImage image, CorrelationTemplate template, SearchOptions options, CancellationToken token = default)
    {
        var peaks = FindCandidates(image, template, options, token);
        var result = SuppressWithDiagnostics(template, peaks.Matches, options);
        return peaks with
        {
            Matches = result.Matches,
            MatchLimitExceeded = result.MatchLimitExceeded
        };
    }

    /// <summary>창 분할 호출용. 모든 창의 원본 좌표 후보를 모은 후 Suppress를 한 번 적용한다.</summary>
    public static CorrelationResult FindCandidates(GrayImage image, CorrelationTemplate template, SearchOptions options, CancellationToken token = default)
        => Imaging.OpenCvTemplateMatcher.Search(image, template, options, token);

    public static ImmutableArray<CorrelationMatch> Suppress(CorrelationTemplate template, IEnumerable<CorrelationMatch> matches, SearchOptions options)
        => SuppressWithDiagnostics(template, matches, options).Matches;

    /// <summary>출력 상한 뒤의 독립 검출 한 개까지 확인한다. 후보 예산 잘림과 출력 생략을 구별한다.</summary>
    public static CorrelationSuppressionResult SuppressWithDiagnostics(CorrelationTemplate template, IEnumerable<CorrelationMatch> matches, SearchOptions options)
    {
        options.Validate();
        var selected = new List<CorrelationMatch>();
        foreach (var match in matches.OrderByDescending(value => value.Score).ThenBy(value => value.ReferencePoint.Y).ThenBy(value => value.ReferencePoint.X))
        {
            if (selected.Any(previous => Imaging.OpenCvTemplateMatcher.Overlap(previous.ReferencePoint, match.ReferencePoint, template) > options.MaximumOverlap))
            {
                continue;
            }
            if (selected.Count == options.MaximumMatches)
            {
                return new(selected.ToImmutableArray(), true);
            }
            selected.Add(match);
        }
        return new(selected.ToImmutableArray(), false);
    }
}
