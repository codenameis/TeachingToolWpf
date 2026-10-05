using System.Collections.Immutable;
using System.Diagnostics;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionGeometry;
using VisionImaging;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Matching;

public sealed record ShapeTrainingOutput(ShapeModel Model, double ReadMilliseconds, double TrainingMilliseconds);
/// <summary>순차 단계의 wall time. 각 단계에는 해당 단계의 원본 읽기와 병렬 worker 대기가 포함된다.</summary>
public sealed record ShapeSearchPhases(double SourceOpenMilliseconds, double CoarseSearchMilliseconds,
    double OriginalRefinementMilliseconds, double WindowSearchMilliseconds, double FinalSuppressionMilliseconds);
public sealed record ShapeSearchOutput(ImmutableArray<ShapeMatch> Matches, int Windows, bool Truncated,
    double ReadMilliseconds, double TotalMilliseconds, ImmutableArray<SearchDiagnostics> Diagnostics, bool MatchLimitExceeded = false)
{
    public ShapeSearchPhases? Phases
    {
        get; init;
    }
    public ImagePreprocessingDiagnostics? Preprocessing
    {
        get; init;
    }

    public CandidateScoringDiagnostics? CandidateScoring
    {
        get; init;
    }
}

public interface IShapeMatchingService
{
    Task<ShapeTrainingOutput> TrainAsync(ImageReference image, bool live, RoiGeometry roi, TrainingOptions options, CancellationToken token);
    Task<ShapeSearchOutput> SearchAsync(ImageReference image, bool live, ShapeModel model, RoiGeometry? region, SearchOptions options, CancellationToken token);
}

public enum ShapeSearchPipeline
{
    Classic,
    Paper
}

public sealed class ShapeMatchingService(IImageLoader loader, Func<VideoFrame?> retainFrame,
    Func<IImagePreprocessor>? createGpuPreprocessor = null,
    Func<CancellationToken, Task>? prepareGpu = null,
    ShapeSearchPipeline pipeline = ShapeSearchPipeline.Classic) : IShapeMatchingService, IShapeComputePreparation
{
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

    public Task<ShapeTrainingOutput> TrainAsync(ImageReference image, bool live, RoiGeometry roi, TrainingOptions options, CancellationToken token)
        => Task.Run(async () =>
        {
            options.Validate();
            ValidateRegion(image, roi);
            var desiredLevels = Math.Clamp((int)Math.Floor(Math.Log2(Math.Max(roi.Width, roi.Height) / 64)) + 1, 1, 6);
            options = options with
            {
                PyramidLevels = Math.Max(options.PyramidLevels, desiredLevels),
                UseFineDetailValidation = pipeline == ShapeSearchPipeline.Paper && !options.BuildGeometricModel
            };
            var timer = Stopwatch.StartNew();
            using var reader = await MatchingImageReader.OpenAsync(image, live, loader, retainFrame, token).ConfigureAwait(false);
            var alignment = Math.Max(16, 1 << (options.PyramidLevels - 1));
            var padding = 4 * alignment;
            var left = Math.Max(0, ((int)Math.Floor(roi.Bounds.Left) - padding) / alignment * alignment);
            var top = Math.Max(0, ((int)Math.Floor(roi.Bounds.Top) - padding) / alignment * alignment);
            var right = Math.Min(image.Width, (int)Math.Ceiling(roi.Bounds.Right) + padding);
            var bottom = Math.Min(image.Height, (int)Math.Ceiling(roi.Bounds.Bottom) + padding);
            var pixels = reader.Read(left, top, right - left, bottom - top, token, ShapeTrainer.MaximumPixels);
            var mask = new byte[pixels.Width * pixels.Height];
            for (var y = 0; y < pixels.Height; y++)
            {
                token.ThrowIfCancellationRequested();
                for (var x = 0; x < pixels.Width; x++)
                {
                    mask[(y * pixels.Width) + x] = roi.Contains(new PointD(left + x + 0.5, top + y + 0.5)) ? (byte)255 : (byte)0;
                }
            }
            var readTime = timer.Elapsed.TotalMilliseconds;
            timer.Restart();
            var model = ShapeTrainer.Train(pixels, new(mask, new(roi.Center.X - left, roi.Center.Y - top), roi.Angle * Math.PI / 180), options, token);
            return new ShapeTrainingOutput(model, readTime, timer.Elapsed.TotalMilliseconds);
        }, token);

    public Task<ShapeSearchOutput> SearchAsync(ImageReference image, bool live, ShapeModel model, RoiGeometry? region,
        SearchOptions options, CancellationToken token)
        => Task.Run(async () =>
        {
            ShapeMatcher.ValidateSearchOptions(model, options);
            if (region is not null)
            {
                ValidateRegion(image, region);
            }
            var total = Stopwatch.StartNew();
            using var preprocessor = options.ComputeMode == InspectionComputeMode.GpuPreprocessing
                ? new SerializedImagePreprocessor(createGpuPreprocessor?.Invoke() ??
                    throw new InvalidOperationException("GPU 전처리 구현이 등록되지 않았습니다.")) : null;
            var paper = pipeline == ShapeSearchPipeline.Paper && !model.Options.BuildGeometricModel;
            var matcher = new ShapeMatcher(preprocessor, preprocessor?.CandidateScorer, paper);
            var readTimer = Stopwatch.StartNew();
            using var reader = await MatchingImageReader.OpenAsync(image, live, loader, retainFrame, token).ConfigureAwait(false);
            var readTime = readTimer.Elapsed.TotalMilliseconds;
            reader.PreferDirectGray = paper;
            if (model.Levels.Length >= 3 && (paper || (long)image.Width * image.Height > PreparedSearchImage.MaximumPixels))
            {
                var coarseInterior = paper && (long)image.Width * image.Height <= PreparedSearchImage.MaximumPixels ? 64 : 256;
                return ShapePyramidSearch.Run(reader, image, model, region, options, total, readTime, token, matcher, coarseInterior) with
                {
                    Preprocessing = preprocessor?.Diagnostics,
                    CandidateScoring = preprocessor?.CandidateScorer?.CandidateDiagnostics
                };
            }
            var radius = model.Bounds.Max(p => Math.Sqrt((p.X * p.X) + (p.Y * p.Y))) * model.Options.MaximumScale;
            // 모든 pyramid 필터의 지지 범위, 확산, 위치 국소 탐색, 정밀화 대응 반경을 함께 보존한다.
            var halo = (int)Math.Ceiling((radius + (12 * (1 << (model.Levels.Length - 1))) + 12) / 16) * 16;
            var maximumInterior = ((int)Math.Floor(Math.Sqrt(PreparedSearchImage.MaximumPixels)) - (2 * halo)) / 16 * 16;
            if (maximumInterior < 32)
            {
                throw new InvalidOperationException("모델 크기가 검색 창의 메모리 예산을 초과합니다. 더 작은 학습 ROI가 필요합니다.");
            }
            var interior = Math.Min(768, maximumInterior);
            var bounds = region?.Bounds ?? new BoundsD(0, 0, image.Width, image.Height);
            var all = new List<ShapeMatch>();
            var diagnostics = ImmutableArray.CreateBuilder<SearchDiagnostics>();
            var truncated = false;
            var sourceOpenTime = readTime;
            var phaseTimer = Stopwatch.StartNew();
            for (var y = Math.Max(0, (int)bounds.Top / interior * interior); y < Math.Min(image.Height, bounds.Bottom); y += interior)
            {
                for (var x = Math.Max(0, (int)bounds.Left / interior * interior); x < Math.Min(image.Width, bounds.Right); x += interior)
                {
                    token.ThrowIfCancellationRequested();
                    var left = Math.Max(0, x - halo);
                    var top = Math.Max(0, y - halo);
                    var right = Math.Min(image.Width, x + interior + halo);
                    var bottom = Math.Min(image.Height, y + interior + halo);
                    readTimer.Restart();
                    var pixels = reader.Read(left, top, right - left, bottom - top, token);
                    readTime += readTimer.Elapsed.TotalMilliseconds;
                    // 최종 포함 판정은 실제 ROI(원은 해석적 원)로 한다. 창 겹침의 복제 결과는 전역 NMS로 제거한다.
                    var localRegion = ToSearchRegion(region, left, top);
                    var result = matcher.Search(pixels, model, options with
                    {
                        Region = localRegion,
                        MaximumMatches = options.MaximumCandidates > 1000 ? 1000 : options.MaximumCandidates
                    }, token);
                    diagnostics.Add(result.Diagnostics);
                    truncated |= result.Diagnostics.CandidateBudgetExceeded || result.Diagnostics.VerificationBudgetExceeded || result.Diagnostics.MatchLimitExceeded;
                    foreach (var match in result.Matches)
                    {
                        var pose = match.Pose with
                        {
                            X = match.Pose.X + left,
                            Y = match.Pose.Y + top
                        };
                        if (pose.X >= x && pose.X < x + interior && pose.Y >= y && pose.Y < y + interior &&
                            (region?.Contains(new PointD(pose.X, pose.Y)) ?? true))
                        {
                            all.Add(match with
                            {
                                Pose = pose,
                                Refinement = match.Refinement with
                                {
                                    InitialPose = match.Refinement.InitialPose with
                                    {
                                        X = match.Refinement.InitialPose.X + left,
                                        Y = match.Refinement.InitialPose.Y + top
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

    internal static void ValidateRegion(ImageReference image, RoiGeometry region)
    {
        var bounds = region.Bounds;
        if (bounds.Left < -1e-7 || bounds.Top < -1e-7 || bounds.Right > image.Width + 1e-7 || bounds.Bottom > image.Height + 1e-7)
        {
            throw new ArgumentException("ROI가 원본 이미지 경계를 벗어납니다. 영역을 수정한 뒤 다시 실행하세요.");
        }
    }

    internal static SearchRegion? ToSearchRegion(RoiGeometry? geometry, int left, int top)
        => geometry is null ? null : geometry.Radius > 0
            ? new SearchRegion(new(geometry.Center.X - left, geometry.Center.Y - top), geometry.Radius)
            : new SearchRegion(geometry.Outline.Select(p => new ImagePoint(p.X - left, p.Y - top)));
}
