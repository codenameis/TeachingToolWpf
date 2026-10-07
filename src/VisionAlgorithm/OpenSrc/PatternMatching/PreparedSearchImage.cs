using System.Collections.Immutable;
using System.Diagnostics;

namespace VisionAlgorithm.OpenSrc.PatternMatching;

/// <summary>완성 후 불변인 프레임 전처리. 동일 설정의 여러 모델/병렬 검색에서 공유할 수 있다.</summary>
public sealed class PreparedSearchImage
{
    public const int MaximumPixels = 2_097_152;
    public int Width => Gradients[0].Width;
    public int Height => Gradients[0].Height;
    public float MinimumGradient
    {
        get;
    }
    public int LevelCount => Gradients.Length;
    public double ElapsedMilliseconds
    {
        get;
    }
    public long EstimatedBytes => Gradients.Sum(g => g.EstimatedBytes) +
        Maps.Sum(map => map.EstimatedBytes) + (Original?.Pixels.Length ?? 0) + ExcludedPixels.Length;
    internal GrayImage? Original
    {
        get; private init;
    }
    internal ReadOnlyMemory<byte> ExcludedPixels
    {
        get; private init;
    }
    internal ImmutableArray<GradientImage> Gradients
    {
        get;
    }
    internal ImmutableArray<OrientationMaps> Maps
    {
        get;
    }

    private PreparedSearchImage(float minimumGradient, ImmutableArray<GradientImage> gradients,
        ImmutableArray<OrientationMaps> maps, double elapsed)
    {
        MinimumGradient = minimumGradient;
        Gradients = gradients;
        Maps = maps;
        ElapsedMilliseconds = elapsed;
    }

    public static PreparedSearchImage Create(GrayImage image, float minimumGradient, int levels,
        CancellationToken cancellationToken = default, bool tiled = false, int maximumPixels = MaximumPixels,
        IImagePreprocessor? preprocessor = null, bool retainOriginal = false)
        => Build(image, minimumGradient, levels, cancellationToken, tiled, maximumPixels, true, true, preprocessor,
            retainOriginal: retainOriginal);

    internal static PreparedSearchImage CreateForSearch(GrayImage image, float minimumGradient, int levels,
        bool extractEdges, CancellationToken cancellationToken, bool tiled, IImagePreprocessor? preprocessor,
        bool createLinearResponses = true, bool retainOriginal = false)
        => Build(image, minimumGradient, levels, cancellationToken, tiled, MaximumPixels, true, extractEdges, preprocessor,
            createLinearResponses, retainOriginal);

    internal static PreparedSearchImage CreateForRefinement(GrayImage image, float minimumGradient, int levels,
        CancellationToken cancellationToken, bool tiled, IImagePreprocessor? preprocessor = null, bool extractEdges = true, bool retainOriginal = false)
        => Build(image, minimumGradient, levels, cancellationToken, tiled, ShapeTrainer.MaximumPixels, false, extractEdges, preprocessor,
            retainOriginal: retainOriginal);

    private static PreparedSearchImage Build(GrayImage image, float minimumGradient, int levels,
        CancellationToken cancellationToken, bool tiled, int maximumPixels, bool responseMaps, bool extractEdges,
        IImagePreprocessor? preprocessor, bool createLinearResponses = true, bool retainOriginal = false)
    {
        if (maximumPixels > ShapeTrainer.MaximumPixels || (long)image.Width * image.Height > maximumPixels)
        {
            throw new ArgumentException("전처리 영상이 작업 메모리 예산을 초과했습니다.", nameof(image));
        }
        if (!float.IsFinite(minimumGradient) || minimumGradient <= 0 || levels is < 1 or > 6)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumGradient));
        }
        var timer = Stopwatch.StartNew();
        var gradients = ImmutableArray.CreateBuilder<GradientImage>();
        var maps = ImmutableArray.CreateBuilder<OrientationMaps>();
        var current = image;
        for (var level = 0; level < levels; level++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // 상위 레벨은 방향 검색/검증만 소비하며 실제 에지 대응은 원본 level-0에서만 수행한다.
            var levelEdges = extractEdges && level == 0;
            var gradient = tiled ? GradientImage.CreateTiled(current, minimumGradient, cancellationToken, preprocessor, levelEdges)
                : GradientImage.Create(current, minimumGradient, cancellationToken, preprocessor, levelEdges);
            gradients.Add(gradient);
            if (responseMaps)
            {
                maps.Add(OrientationMaps.Create(gradient.Bits, current.Width, current.Height, 2, 4, cancellationToken,
                    createLinearResponses));
            }
            if (level + 1 < levels)
            {
                current = GradientImage.Reduce(current);
            }
        }
        GrayImage? original = null;
        if (retainOriginal)
        {
            var pixels = new byte[image.Width * image.Height];
            for (var row = 0; row < image.Height; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                image.Pixels.Span.Slice(row * image.Stride, image.Width).CopyTo(pixels.AsSpan(row * image.Width));
            }
            original = new(pixels, image.Width, image.Height, image.Width);
        }
        // 재사용되는 전처리 결과는 호출자 입력 버퍼의 반환/변경 이후에도 마스크를 보존한다.
        var excludedPixels = image.ExcludedPixels.ToArray();
        return new(minimumGradient, gradients.ToImmutable(), maps.ToImmutable(), timer.Elapsed.TotalMilliseconds)
        {
            Original = original,
            ExcludedPixels = excludedPixels
        };
    }

    internal static void ValidateSize(GrayImage image)
    {
        if ((long)image.Width * image.Height > MaximumPixels)
        {
            throw new ArgumentException("작업 영상은 2 Mpx 이하여야 합니다. 큰 원본은 halo가 있는 검색 창으로 분할하십시오.", nameof(image));
        }
    }
}
