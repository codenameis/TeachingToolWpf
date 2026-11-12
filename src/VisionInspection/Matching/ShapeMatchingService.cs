using System.Collections.Immutable;
using System.Diagnostics;
using VisionAlgorithm;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Matching;

public sealed class ShapeMatchingService(IImageLoader loader, Func<VideoFrame?> retainFrame,
    Func<IImagePreprocessor>? createGpuPreprocessor = null,
    Func<CancellationToken, Task>? prepareGpu = null,
    ShapeSearchPipeline pipeline = ShapeSearchPipeline.Classic,
    IPreInspectionFilterPipeline? preFilterPipeline = null) : IShapeMatchingService, IShapeComputePreparation
{
    private const double BasePyramidSpanPixels = 64.0;
    private const int MinPyramidLevels = 1;
    private const int MaxPyramidLevels = 6;
    private const int MinCropAlignmentPixels = 16;
    private const int TrainingPaddingAlignmentMultiplier = 4;
    private const int PreviewCropAlignmentPixels = 16;
    private const int PreviewCropPaddingPixels = 24;
    private const double PixelCenterOffset = 0.5;
    private const double RectangleContainsEpsilon = 1e-10;
    private const byte ActiveMaskValue = 255;
    private const int PyramidSearchMinLevels = 3;
    private const int PaperCoarseInteriorPixels = 64;
    private const int DefaultCoarseInteriorPixels = 256;
    private const int PyramidFilterSupportPixels = 12; // [Math Why]: 최상위 레벨 가우시안/확산/국소 정밀화 지지 반경(12px)을 halo에 반영한다.
    private const int MinWindowInteriorPixels = 32;
    private const int DefaultWindowInteriorPixels = 768;
    private const int MaxCpuWindowBatchCount = 12;
    private const int MaxMatchesPerSearchWindow = 1000;

    private readonly MatchingPreviewCache _previewCache = new();

    public async Task<double> PrepareGpuAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (prepareGpu is null)
        {
            return 0;
        }
        var timer = Stopwatch.StartNew();
        await prepareGpu(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return timer.Elapsed.TotalMilliseconds;
    }

    public Task<ShapeTrainingOutput> TrainAsync(ImageReference image, bool live, RoiGeometry roi, TrainingOptions options, CancellationToken token, RoiExclusion? exclusion = null)
        => Task.Run(async () =>
        {
            options.Validate();
            MatchingRegionValidator.ValidateTeachingRegion(image, roi);
            var desiredLevels = Math.Clamp((int)Math.Floor(Math.Log2(Math.Max(roi.Width, roi.Height) / BasePyramidSpanPixels)) + 1, MinPyramidLevels, MaxPyramidLevels);
            options = options with
            {
                PyramidLevels = Math.Max(options.PyramidLevels, desiredLevels),
                UseFineDetailValidation = options.UseFineDetailValidation
            };
            var timer = Stopwatch.StartNew();
            using var reader = await MatchingImageReader.OpenAsync(image, live, loader, retainFrame, token, preFilterPipeline).ConfigureAwait(false);
            var alignment = Math.Max(MinCropAlignmentPixels, 1 << (options.PyramidLevels - 1));
            var padding = TrainingPaddingAlignmentMultiplier * alignment;
            var crop = MatchingCropWindow.Expand(image, roi.Bounds, padding, alignment);
            var left = crop.Left;
            var top = crop.Top;
            var pixels = reader.Read(left, top, crop.Width, crop.Height, token, ShapeTrainer.MaximumPixels);
            var mask = new byte[pixels.Width * pixels.Height];
            for (var y = 0; y < pixels.Height; y++)
            {
                token.ThrowIfCancellationRequested();
                for (var x = 0; x < pixels.Width; x++)
                {
                    var point = new PointD(left + x + PixelCenterOffset, top + y + PixelCenterOffset);
                    mask[(y * pixels.Width) + x] = roi.Contains(point) && exclusion?.Contains(point) != true ? ActiveMaskValue : (byte)0;
                }
            }
            var readTime = timer.Elapsed.TotalMilliseconds;
            timer.Restart();
            var model = ShapeTrainer.Train(pixels, new(mask, new(roi.Center.X - left, roi.Center.Y - top), roi.Angle * Math.PI / GeometryConstants.HalfCircleDegrees), options, token);
            var previewPatch = RoiPreviewPatchExtractor.ExtractRectifiedPatch(
                pixels.Pixels.Span,
                pixels.Width,
                pixels.Height,
                pixels.Stride,
                left,
                top,
                roi,
                exclusion);
            return new ShapeTrainingOutput(
                model,
                readTime,
                timer.Elapsed.TotalMilliseconds,
                previewPatch.Pixels,
                previewPatch.Width,
                previewPatch.Height);
        }, token);

    public Task<ShapeTrainingOutput?> PreviewAsync(ImageReference image, bool live, RoiGeometry roi, TrainingOptions options, CancellationToken token, RoiExclusion? exclusion = null)
        => Task.Run<ShapeTrainingOutput?>(async () =>
        {
            options.Validate();
            MatchingRegionValidator.ValidateTeachingRegion(image, roi);
            var desiredLevels = Math.Clamp((int)Math.Floor(Math.Log2(Math.Max(roi.Width, roi.Height) / BasePyramidSpanPixels)) + 1, MinPyramidLevels, MaxPyramidLevels);
            options = options with
            {
                PyramidLevels = Math.Max(options.PyramidLevels, desiredLevels),
                UseFineDetailValidation = options.UseFineDetailValidation
            };
            var timer = Stopwatch.StartNew();
            var crop = MatchingCropWindow.Expand(image, roi.Bounds, PreviewCropPaddingPixels, PreviewCropAlignmentPixels);
            var left = crop.Left;
            var top = crop.Top;
            var pixels = await _previewCache.ReadAsync(image, live, loader, retainFrame, left, top, crop.Width, crop.Height, null, token, ShapeTrainer.MaximumPixels, preFilterPipeline: preFilterPipeline).ConfigureAwait(false);
            var mask = BuildRoiMask(pixels.Width, pixels.Height, left, top, roi, exclusion, token);
            var readTime = timer.Elapsed.TotalMilliseconds;
            timer.Restart();
            var model = ShapeTrainer.TrainPreview(pixels, new(mask, new(roi.Center.X - left, roi.Center.Y - top), roi.Angle * Math.PI / GeometryConstants.HalfCircleDegrees), options, token);
            return new ShapeTrainingOutput(model, readTime, timer.Elapsed.TotalMilliseconds);
        }, token);

    private static byte[] BuildRoiMask(int width, int height, int left, int top, RoiGeometry roi, RoiExclusion? exclusion, CancellationToken token)
    {
        var mask = new byte[width * height];
        var roiBounds = roi.Bounds;
        var minX = Math.Max(0, (int)Math.Floor(roiBounds.Left) - left);
        var maxX = Math.Min(width - 1, (int)Math.Ceiling(roiBounds.Right) - left);
        var minY = Math.Max(0, (int)Math.Floor(roiBounds.Top) - top);
        var maxY = Math.Min(height - 1, (int)Math.Ceiling(roiBounds.Bottom) - top);
        if (minX > maxX || minY > maxY)
        {
            return mask;
        }
        var (sin, cos) = Math.SinCos(-roi.Angle * GeometryConstants.DegreesToRadians);
        var centerX = roi.Center.X;
        var centerY = roi.Center.Y;
        var roiWidth = roi.Width;
        var roiHeight = roi.Height;
        var isRectangle = roi.Shape.Kind == RoiShapeKind.Rectangle;
        var halfWidth = (roiWidth / 2.0) + RectangleContainsEpsilon;
        var halfHeight = (roiHeight / 2.0) + RectangleContainsEpsilon;
        var hasExclusion = exclusion is not null && !exclusion.Regions.IsDefaultOrEmpty;
        for (var y = minY; y <= maxY; y++)
        {
            token.ThrowIfCancellationRequested();
            var worldY = top + y + PixelCenterOffset;
            var dy = worldY - centerY;
            var dySin = dy * sin;
            var dyCos = dy * cos;
            var rowOffset = y * width;
            for (var x = minX; x <= maxX; x++)
            {
                var worldX = left + x + PixelCenterOffset;
                var dx = worldX - centerX;
                var localX = (dx * cos) - dySin;
                var localY = (dx * sin) + dyCos;
                var inside = isRectangle
                    ? Math.Abs(localX) <= halfWidth && Math.Abs(localY) <= halfHeight
                    : roi.Shape.ContainsLocal(new(localX, localY), roiWidth, roiHeight, 0);
                if (inside && (!hasExclusion || !exclusion!.Contains(new(worldX, worldY))))
                {
                    mask[rowOffset + x] = ActiveMaskValue;
                }
            }
        }
        return mask;
    }

    public Task<ShapeSearchOutput> SearchAsync(ImageReference image, bool live, ShapeModel model, RoiGeometry? region,
        ShapeSearchOptions options, CancellationToken token, RoiExclusion? exclusion = null)
        => Task.Run(async () =>
        {
            ShapeMatcher.ValidateSearchOptions(model, options);
            if (region is not null)
            {
                MatchingRegionValidator.ValidateSearchRegion(image, region);
            }
            var total = Stopwatch.StartNew();
            using var preprocessor = options.ComputeMode == InspectionComputeMode.GpuPreprocessing
                ? new SerializedImagePreprocessor(createGpuPreprocessor?.Invoke() ??
                    throw new InvalidOperationException("GPU 전처리 구현이 등록되지 않았습니다.")) : null;
            var paper = pipeline == ShapeSearchPipeline.Paper;
            var matcher = new ShapeMatcher(preprocessor, preprocessor?.CandidateScorer, paper);
            var readTimer = Stopwatch.StartNew();
            using var reader = await MatchingImageReader.OpenAsync(image, live, loader, retainFrame, token, preFilterPipeline).ConfigureAwait(false);
            var readTime = readTimer.Elapsed.TotalMilliseconds;
            reader.PreferDirectGray = paper;
            reader.Exclusion = exclusion;
            if (model.Levels.Length >= PyramidSearchMinLevels && (paper || (long)image.Width * image.Height > PreparedSearchImage.MaximumPixels))
            {
                var coarseInterior = paper && (long)image.Width * image.Height <= PreparedSearchImage.MaximumPixels
                    ? PaperCoarseInteriorPixels
                    : DefaultCoarseInteriorPixels;
                return ShapePyramidSearch.Run(reader, image, model, region, options, total, readTime, token, matcher, coarseInterior) with
                {
                    Preprocessing = preprocessor?.Diagnostics,
                    CandidateScoring = preprocessor?.CandidateScorer?.CandidateDiagnostics
                };
            }
            var radius = model.Bounds.Max(p => Math.Sqrt((p.X * p.X) + (p.Y * p.Y))) * model.Options.MaximumScale;
            // 모든 pyramid 필터의 지지 범위, 확산, 위치 국소 탐색, 정밀화 대응 반경을 함께 보존한다.
            var halo = (int)Math.Ceiling((radius + (PyramidFilterSupportPixels * (1 << (model.Levels.Length - 1))) + PyramidFilterSupportPixels) / MinCropAlignmentPixels) * MinCropAlignmentPixels;
            var maximumInterior = ((int)Math.Floor(Math.Sqrt(PreparedSearchImage.MaximumPixels)) - (2 * halo)) / MinCropAlignmentPixels * MinCropAlignmentPixels;
            if (maximumInterior < MinWindowInteriorPixels)
            {
                throw new InvalidOperationException("모델 크기가 검색 창의 메모리 예산을 초과합니다. 더 작은 학습 ROI가 필요합니다.");
            }
            var interior = Math.Min(DefaultWindowInteriorPixels, maximumInterior);
            var bounds = region?.Bounds ?? new BoundsD(0, 0, image.Width, image.Height);
            var all = new List<ShapeMatch>();
            var diagnostics = ImmutableArray.CreateBuilder<ShapeSearchDiagnostics>();
            var truncated = false;
            var sourceOpenTime = readTime;
            var phaseTimer = Stopwatch.StartNew();
            var windowSpecs = new List<(int X, int Y, int Left, int Top, int Right, int Bottom)>();
            for (var y = Math.Max(0, (int)bounds.Top / interior * interior); y < Math.Min(image.Height, bounds.Bottom); y += interior)
            {
                for (var x = Math.Max(0, (int)bounds.Left / interior * interior); x < Math.Min(image.Width, bounds.Right); x += interior)
                {
                    var left = Math.Max(0, x - halo);
                    var top = Math.Max(0, y - halo);
                    var right = Math.Min(image.Width, x + interior + halo);
                    var bottom = Math.Min(image.Height, y + interior + halo);
                    windowSpecs.Add((x, y, left, top, right, bottom));
                }
            }
            var maxWindowBatch = preprocessor is null && options.MaximumParallelism > 1 ? Math.Min(MaxCpuWindowBatchCount, options.MaximumParallelism) : 1;
            for (var batchStart = 0; batchStart < windowSpecs.Count; batchStart += maxWindowBatch)
            {
                var batchCount = Math.Min(maxWindowBatch, windowSpecs.Count - batchStart);
                var batchPixels = new ShapeGrayImage[batchCount];
                for (var i = 0; i < batchCount; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var spec = windowSpecs[batchStart + i];
                    readTimer.Restart();
                    batchPixels[i] = reader.Read(spec.Left, spec.Top, spec.Right - spec.Left, spec.Bottom - spec.Top, token);
                    readTime += readTimer.Elapsed.TotalMilliseconds;
                }
                var batchResults = new MatchResult[batchCount];
                if (batchCount == 1)
                {
                    var spec = windowSpecs[batchStart];
                    var localRegion = ToSearchRegion(region, spec.Left, spec.Top);
                    batchResults[0] = matcher.Search(batchPixels[0], model, options with
                    {
                        Region = localRegion,
                        MaximumMatches = options.MaximumCandidates > MaxMatchesPerSearchWindow ? MaxMatchesPerSearchWindow : options.MaximumCandidates
                    }, token);
                }
                else
                {
                    Parallel.For(0, batchCount, new ParallelOptions
                    {
                        MaxDegreeOfParallelism = batchCount,
                        CancellationToken = token
                    }, i =>
                    {
                        var spec = windowSpecs[batchStart + i];
                        var localRegion = ToSearchRegion(region, spec.Left, spec.Top);
                        batchResults[i] = matcher.Search(batchPixels[i], model, options with
                        {
                            // 바깥 창 병렬과 안쪽 후보 병렬이 스레드 풀을 두고 경합하지 않게 안쪽은 코어를 나눠 쓴다.
                            MaximumParallelism = Math.Max(1, options.MaximumParallelism / batchCount),
                            Region = localRegion,
                            MaximumMatches = options.MaximumCandidates > MaxMatchesPerSearchWindow ? MaxMatchesPerSearchWindow : options.MaximumCandidates
                        }, token);
                    });
                }
                for (var i = 0; i < batchCount; i++)
                {
                    var spec = windowSpecs[batchStart + i];
                    var result = batchResults[i];
                    diagnostics.Add(result.Diagnostics);
                    truncated |= result.Diagnostics.CandidateBudgetExceeded || result.Diagnostics.VerificationBudgetExceeded || result.Diagnostics.MatchLimitExceeded;
                    foreach (var match in result.Matches)
                    {
                        var pose = match.Pose with
                        {
                            X = match.Pose.X + spec.Left,
                            Y = match.Pose.Y + spec.Top
                        };
                        if (pose.X >= spec.X && pose.X < spec.X + interior && pose.Y >= spec.Y && pose.Y < spec.Y + interior &&
                            (region?.Contains(new PointD(pose.X, pose.Y)) ?? true))
                        {
                            all.Add(match with
                            {
                                Pose = pose,
                                UnmatchedFeatures = match.UnmatchedFeatures.IsDefaultOrEmpty
                                    ? []
                                    : match.UnmatchedFeatures.Select(p => new ImagePoint(p.X + spec.Left, p.Y + spec.Top)).ToImmutableArray(),
                                KeyMatchedFeatures = match.KeyMatchedFeatures.IsDefaultOrEmpty
                                    ? []
                                    : match.KeyMatchedFeatures.Select(p => new ImagePoint(p.X + spec.Left, p.Y + spec.Top)).ToImmutableArray(),
                                Refinement = match.Refinement with
                                {
                                    InitialPose = match.Refinement.InitialPose with
                                    {
                                        X = match.Refinement.InitialPose.X + spec.Left,
                                        Y = match.Refinement.InitialPose.Y + spec.Top
                                    }
                                }
                            });
                        }
                    }
                }
            }
            var windowTime = phaseTimer.Elapsed.TotalMilliseconds;
            phaseTimer.Restart();
            var output = MatchSuppression.Apply(model, all, options.MaximumOverlap, options.MaximumMatches + 1);
            return new ShapeSearchOutput(output.Take(options.MaximumMatches).ToImmutableArray(), diagnostics.Count, truncated,
                readTime, total.Elapsed.TotalMilliseconds, diagnostics.ToImmutable(), output.Length > options.MaximumMatches)
            {
                Phases = new(sourceOpenTime, 0, 0, windowTime, phaseTimer.Elapsed.TotalMilliseconds),
                Preprocessing = preprocessor?.Diagnostics,
                CandidateScoring = preprocessor?.CandidateScorer?.CandidateDiagnostics
            };
        }, token);

    internal static ShapeSearchRegion? ToSearchRegion(RoiGeometry? geometry, int left, int top)
        => geometry is null ? null : geometry.Radius > 0
            ? new ShapeSearchRegion(new(geometry.Center.X - left, geometry.Center.Y - top), geometry.Radius)
            : new ShapeSearchRegion(geometry.Outline.Select(p => new ImagePoint(p.X - left, p.Y - top)));
}
