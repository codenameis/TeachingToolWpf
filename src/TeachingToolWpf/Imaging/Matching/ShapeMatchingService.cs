using System.Collections.Immutable;
using System.Diagnostics;
using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Geometry;
using TeachingToolWpf.Core.Imaging;
using VisionAlgorithm.OpenSrc.PatternMatching;

namespace TeachingToolWpf.Imaging.Matching;

public sealed record ShapeTrainingOutput(ShapeModel Model, double ReadMilliseconds, double TrainingMilliseconds);
public sealed record ShapeSearchOutput(ImmutableArray<ShapeMatch> Matches, int Windows, bool Truncated,
    double ReadMilliseconds, double TotalMilliseconds, ImmutableArray<SearchDiagnostics> Diagnostics);

public interface IShapeMatchingService
{
    Task<ShapeTrainingOutput> TrainAsync(ImageReference image, bool live, RoiGeometry roi, TrainingOptions options, CancellationToken token);
    Task<ShapeSearchOutput> SearchAsync(ImageReference image, bool live, ShapeModel model, RoiGeometry? region, SearchOptions options, CancellationToken token);
}

public sealed class ShapeMatchingService(IImageLoader loader, Func<VideoFrame?> retainFrame) : IShapeMatchingService
{
    public Task<ShapeTrainingOutput> TrainAsync(ImageReference image, bool live, RoiGeometry roi, TrainingOptions options, CancellationToken token)
        => Task.Run(async () =>
        {
            options.Validate();
            ValidateRegion(image, roi);
            var timer = Stopwatch.StartNew();
            using var reader = await MatchingImageReader.OpenAsync(image, live, loader, retainFrame, token).ConfigureAwait(false);
            var padding = 8 * (1 << (options.PyramidLevels - 1));
            var left = Math.Max(0, ((int)Math.Floor(roi.Bounds.Left) - padding) / 16 * 16);
            var top = Math.Max(0, ((int)Math.Floor(roi.Bounds.Top) - padding) / 16 * 16);
            var right = Math.Min(image.Width, (int)Math.Ceiling(roi.Bounds.Right) + padding);
            var bottom = Math.Min(image.Height, (int)Math.Ceiling(roi.Bounds.Bottom) + padding);
            var pixels = reader.Read(left, top, right - left, bottom - top, token);
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
            options.Validate();
            if (region is not null)
            {
                ValidateRegion(image, region);
            }
            var total = Stopwatch.StartNew();
            var readTimer = Stopwatch.StartNew();
            using var reader = await MatchingImageReader.OpenAsync(image, live, loader, retainFrame, token).ConfigureAwait(false);
            var readTime = readTimer.Elapsed.TotalMilliseconds;
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
            var matcher = new ShapeMatcher();
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
                    truncated |= result.Diagnostics.CandidateBudgetExceeded || result.Diagnostics.VerificationBudgetExceeded;
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
            var output = MatchSuppression.Apply(model, all, options.MaximumOverlap, options.MaximumMatches);
            return new ShapeSearchOutput(output, diagnostics.Count, truncated, readTime, total.Elapsed.TotalMilliseconds, diagnostics.ToImmutable());
        }, token);

    private static void ValidateRegion(ImageReference image, RoiGeometry region)
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
