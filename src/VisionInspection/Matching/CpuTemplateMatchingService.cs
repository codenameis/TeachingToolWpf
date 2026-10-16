using System.Diagnostics;
using CpuMatching.Core;
using CpuMatching.DirectionalChamfer;
using CpuMatching.Line2D;
using CpuMatching.SubNcc;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Matching;

public enum CpuTemplateMethod
{
    Line2D,
    SubNcc,
    DirectionalChamfer
}

public sealed record CpuTemplate(CpuTemplateMethod Method, ITemplateMatcher Matcher, RoiGeometry TeachingRegion)
{
    public CpuTemplateParameters Parameters { get; init; } = new();
}
public sealed record CpuSearchOutput(SearchResult Result, double TotalMilliseconds, double ReadMilliseconds, double SearchMilliseconds)
{
    public long CompletedTimestamp
    {
        get; init;
    }
    public CoarseScoreDiagnostics? Acceleration
    {
        get; init;
    }
}

public interface ICpuTemplateMatchingService
{
    Task<CpuTemplate> TrainAsync(ImageReference image, bool live, RoiGeometry roi, CpuTemplateMethod method, CancellationToken token, RoiExclusion? exclusion = null, CpuTemplateParameters? parameters = null);
    Task<CpuSearchOutput> SearchAsync(ImageReference image, bool live, CpuTemplate template, RoiGeometry? region, SearchOptions options, CancellationToken token, RoiExclusion? exclusion = null, bool useCuda = false);
}

/// <summary>원본 읽기/회전 학습 ROI와 CPU 라이브러리의 픽셀 인덱스 좌표를 연결한다.</summary>
public sealed class CpuTemplateMatchingService(IImageLoader loader, Func<VideoFrame?> retainFrame,
    Func<ICoarseScoreAccelerator>? rentAccelerator = null) : ICpuTemplateMatchingService
{
    public Task<CpuTemplate> TrainAsync(ImageReference image, bool live, RoiGeometry roi, CpuTemplateMethod method,
        CancellationToken token, RoiExclusion? exclusion = null, CpuTemplateParameters? parameters = null) => Task.Run(async () =>
    {
        parameters ??= new CpuTemplateParameters();
        parameters.Validate(method);
        ShapeMatchingService.ValidateRegion(image, roi);
        if (roi.Shape.Kind != RoiShapeKind.Rectangle || exclusion is not null)
        {
            throw new NotSupportedException("세 CPU 방식의 학습은 마스크 없는 사각형 ROI를 지원합니다.");
        }
        if (roi.Width is < 4 or > 512 || roi.Height is < 4 or > 512)
        {
            throw new ArgumentException("CPU 학습 ROI의 각 변은 4~512 px이어야 합니다.");
        }
        if (!Enum.IsDefined(method))
        {
            throw new ArgumentOutOfRangeException(nameof(method));
        }
        using var reader = await MatchingImageReader.OpenAsync(image, live, loader, retainFrame, token).ConfigureAwait(false);
        reader.PreferDirectGray = true;
        var width = (int)Math.Round(roi.Width);
        var height = (int)Math.Round(roi.Height);
        // 재표본화의 배율을 숨기지 않고 1픽셀 간격으로 학습한다. 표시 경계도 이 크기다.
        var teaching = new RoiGeometry(roi.Center, width, height, roi.Angle);
        var left = Math.Max(0, (int)Math.Floor(teaching.Bounds.Left) - 1);
        var top = Math.Max(0, (int)Math.Floor(teaching.Bounds.Top) - 1);
        var right = Math.Min(image.Width, (int)Math.Ceiling(teaching.Bounds.Right) + 1);
        var bottom = Math.Min(image.Height, (int)Math.Ceiling(teaching.Bounds.Bottom) + 1);
        var crop = Read(reader, left, top, right - left, bottom - top, token);
        var pixels = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
            {
                var world = teaching.ToWorld(new(x + .5 - (width / 2d), y + .5 - (height / 2d)));
                pixels[(y * width) + x] = (byte)Math.Round(crop.Sample(
                    Math.Clamp(world.X - left - .5, 0, crop.Width - 1), Math.Clamp(world.Y - top - .5, 0, crop.Height - 1)));
            }
        }
        var gray = new GrayImage(width, height, pixels);
        ITemplateMatcher matcher = method switch
        {
            CpuTemplateMethod.Line2D => new Line2DMatcher(gray, parameters.Line2D),
            CpuTemplateMethod.SubNcc => new SubNccMatcher(gray, parameters.SubNcc),
            CpuTemplateMethod.DirectionalChamfer => new DirectionalChamferMatcher(gray, parameters.Chamfer),
            _ => throw new ArgumentOutOfRangeException(nameof(method))
        };
        return new CpuTemplate(method, matcher, teaching) { Parameters = parameters };
    }, token);

    public async Task<CpuSearchOutput> SearchAsync(ImageReference image, bool live, CpuTemplate template, RoiGeometry? region,
        SearchOptions options, CancellationToken token, RoiExclusion? exclusion = null, bool useCuda = false)
    {
        var total = Stopwatch.StartNew();
        var output = await Task.Run(async () =>
        {
            options.Validate();
            if (options.Region is not null)
            {
                throw new ArgumentException("검색 범위는 검사 ROI로 지정하세요.");
            }
            if (exclusion is not null)
            {
                throw new NotSupportedException("세 CPU 방식의 검색 마스크는 지원하지 않습니다.");
            }
            if (region is not null)
            {
                ShapeMatchingService.ValidateRegion(image, region);
            }
            var teaching = template.TeachingRegion;
            var bounds = region?.Bounds ?? new BoundsD(0, 0, image.Width, image.Height);
            // ROI는 기준점의 검색 범위다. 경계의 대상도 전체 템플릿으로 평가하도록 원본 여백을 읽는다.
            var halo = Math.Ceiling(Math.Sqrt((teaching.Width * teaching.Width) + (teaching.Height * teaching.Height)) * options.MaximumScale / 2) + 3;
            var left = Math.Max(0, (int)Math.Floor(bounds.Left - halo));
            var top = Math.Max(0, (int)Math.Floor(bounds.Top - halo));
            var right = Math.Min(image.Width, (int)Math.Ceiling(bounds.Right + halo));
            var bottom = Math.Min(image.Height, (int)Math.Ceiling(bounds.Bottom + halo));
            if ((long)(right - left) * (bottom - top) > PyramidSearch.MaximumSearchPixels)
            {
                throw new ArgumentException("검색은 템플릿 여백을 포함해 67,108,864 px(8192×8192)까지입니다. 영역을 자동 축소하지 않습니다.");
            }
            var readTimer = Stopwatch.StartNew();
            SearchResult result;
            double readMs;
            double searchMs;
            CoarseScoreDiagnostics? acceleration = null;
            using (var reader = await MatchingImageReader.OpenAsync(image, live, loader, retainFrame, token).ConfigureAwait(false))
            {
                reader.PreferDirectGray = true;
                var gray = Read(reader, left, top, right - left, bottom - top, token);
                readMs = readTimer.Elapsed.TotalMilliseconds;
                var searchTimer = Stopwatch.StartNew();
                token.ThrowIfCancellationRequested();
                using (var accelerator = useCuda
                    ? rentAccelerator?.Invoke() ?? throw new NotSupportedException("CPU+CUDA 실행기가 연결되지 않았습니다. CPU 모드를 선택하세요.")
                    : null)
                {
                    // UI 각도는 학습 ROI에 대한 상대 각도, 결과는 영상 기준 절대 각도다.
                    var middle = (options.MinimumAngle + options.MaximumAngle) / 2;
                    var offset = PyramidSearch.Wrap(middle + teaching.Angle) - middle;
                    result = template.Matcher.Find(gray, options with
                    {
                        MinimumAngle = options.MinimumAngle + offset,
                        MaximumAngle = options.MaximumAngle + offset
                    }, token, region is null ? null : (x, y) => region.Contains(new PointD(x + left + .5, y + top + .5)), accelerator);
                    acceleration = accelerator?.Diagnostics;
                }
                searchMs = searchTimer.Elapsed.TotalMilliseconds;
            }
            token.ThrowIfCancellationRequested();
            var worldMatches = result.Matches.Select(match => match with { X = match.X + left + .5, Y = match.Y + top + .5 }).ToArray();
            return new CpuSearchOutput(result with
            {
                Matches = worldMatches
            }, 0, readMs, searchMs)
            {
                Acceleration = acceleration
            };
        }, token).ConfigureAwait(false);
        return output with
        {
            TotalMilliseconds = total.Elapsed.TotalMilliseconds,
            CompletedTimestamp = Stopwatch.GetTimestamp()
        };
    }

    private static GrayImage Read(MatchingImageReader reader, int left, int top, int width, int height, CancellationToken token)
    {
        var pixels = new byte[checked(width * height)];
        // 원본은 모두 읽되 중간 읽기 버퍼만 약 2Mpx씩 제한한다. 검색 좌표/해상도는 바꾸지 않는다.
        var rowsPerRead = Math.Max(1, 2_097_152 / width);
        for (var row = 0; row < height; row += rowsPerRead)
        {
            token.ThrowIfCancellationRequested();
            var rows = Math.Min(rowsPerRead, height - row);
            var raw = reader.Read(left, top + row, width, rows, token, 2_097_152);
            for (var y = 0; y < rows; y++)
            {
                raw.Pixels.Span.Slice(y * raw.Stride, width).CopyTo(pixels.AsSpan((row + y) * width, width));
            }
        }
        return new GrayImage(width, height, pixels);
    }
}
