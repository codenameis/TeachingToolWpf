using System.Diagnostics;
using TeachingToolWpf.Imaging.Matching;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionGeometry;
using VisionImaging;
using VisionImaging.Abstractions;

namespace VisionInspection.Cli;

/// <summary>레시피 한 개를 원본 파일에서 실행한다. 결과 파일과 콘솔·UI 수명은 소유하지 않는다.</summary>
public sealed class RecipeRunner(string? cacheDirectory = null, Func<IImagePreprocessor>? createGpuPreprocessor = null)
{
    private readonly string _cacheDirectory = Path.GetFullPath(cacheDirectory ?? Path.Combine(Path.GetTempPath(), "VisionInspection", "image-cache"));

    public async Task<InspectionRunResult> RunAsync(InspectionRecipe recipe, string recipeDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        var timer = Stopwatch.StartNew();
        using var deadline = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        ImageReference? inspectionImage = null;
        ImageReference? trainingImage = null;
        try
        {
            Validate(recipe);
            if (recipe.TimeoutSeconds > 0)
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(recipe.TimeoutSeconds));
            }
            linked.Token.ThrowIfCancellationRequested();
            var searchRoi = recipe.SearchRoi?.ToGeometry();
            var teachingRoi = recipe.TeachingRoi?.ToGeometry();
            var loader = ImageLoaderHost.Create(_cacheDirectory);
            inspectionImage = await ReadImageAsync(recipe.InspectionImage, recipeDirectory, loader, linked.Token).ConfigureAwait(false);
            InspectionRunResult result;
            if (recipe.Method == "primitives")
            {
                result = await RunPrimitivesAsync(recipe, inspectionImage, searchRoi, loader, linked.Token).ConfigureAwait(false);
            }
            else
            {
                trainingImage = await ReadImageAsync(recipe.TrainingImage!, recipeDirectory, loader, linked.Token).ConfigureAwait(false);
                result = recipe.Method == "template"
                    ? await RunTemplateAsync(recipe, trainingImage, inspectionImage, teachingRoi!, searchRoi, loader, linked.Token).ConfigureAwait(false)
                    : await RunShapeAsync(recipe, trainingImage, inspectionImage, teachingRoi!, searchRoi, loader, linked.Token).ConfigureAwait(false);
            }
            linked.Token.ThrowIfCancellationRequested();
            return result with
            {
                Method = recipe.Method,
                InspectionImage = Summarize(inspectionImage),
                TrainingImage = Summarize(trainingImage),
                Diagnostics = result.Diagnostics with
                {
                    TotalMilliseconds = timer.Elapsed.TotalMilliseconds
                }
            };
        }
        catch (Exception exception)
        {
            // 네이티브 호출은 취소 후에 반환할 수 있다. 완료를 기다린 뒤 오래된 결과를 게시하지 않는다.
            var cancelled = linked.IsCancellationRequested;
            return new()
            {
                Status = cancelled ? "cancelled" : "failed",
                Method = recipe.Method,
                Incomplete = cancelled,
                CancellationReason = cancelled ? cancellationToken.IsCancellationRequested ? "requested" : "timeout" : null,
                Error = cancelled ? null : exception.Message,
                InspectionImage = Summarize(inspectionImage),
                TrainingImage = Summarize(trainingImage),
                Diagnostics = new()
                {
                    TotalMilliseconds = timer.Elapsed.TotalMilliseconds
                }
            };
        }
    }

    private void Validate(InspectionRecipe recipe)
    {
        if (recipe.SchemaVersion != 1 || recipe.Method is not ("paper" or "template" or "primitives"))
        {
            throw new ArgumentException("schemaVersion은 1, method는 paper/template/primitives 중 하나여야 합니다.");
        }
        if (string.IsNullOrWhiteSpace(recipe.InspectionImage) || !double.IsFinite(recipe.TimeoutSeconds) || recipe.TimeoutSeconds is < 0 or > 600)
        {
            throw new ArgumentException("inspectionImage와 0~600초의 timeoutSeconds가 필요합니다.");
        }
        ArgumentNullException.ThrowIfNull(recipe.SearchOptions);
        recipe.SearchOptions.Validate();
        if (recipe.SearchOptions.Region is not null)
        {
            throw new ArgumentException("검색 영역은 searchOptions.region 대신 원본 좌표 searchRoi로 지정하세요.");
        }
        if (recipe.Method == "primitives")
        {
            if (recipe.TrainingImage is not null || recipe.TeachingRoi is not null)
            {
                throw new ArgumentException("primitives 방식은 학습 영상과 ROI를 사용하지 않습니다.");
            }
            ArgumentNullException.ThrowIfNull(recipe.PrimitiveOptions);
            recipe.PrimitiveOptions.Validate();
        }
        else if (string.IsNullOrWhiteSpace(recipe.TrainingImage) || recipe.TeachingRoi is null)
        {
            throw new ArgumentException("패턴 방식에는 trainingImage와 teachingRoi가 필요합니다.");
        }
        if (recipe.Method is "paper")
        {
            ArgumentNullException.ThrowIfNull(recipe.TrainingOptions);
            recipe.TrainingOptions.Validate();
            if (recipe.SearchOptions.ComputeMode == InspectionComputeMode.GpuPreprocessing && createGpuPreprocessor is null)
            {
                throw new InvalidOperationException("GPU 전처리 구현이 등록되지 않았습니다.");
            }
        }
        else if (recipe.SearchOptions.ComputeMode != InspectionComputeMode.Cpu)
        {
            throw new ArgumentException("template/primitives 방식은 현재 CPU만 지원합니다.");
        }
    }

    private static async Task<ImageReference> ReadImageAsync(string imagePath, string recipeDirectory,
        IImageLoader loader, CancellationToken token)
    {
        var path = Path.GetFullPath(imagePath, Path.GetFullPath(recipeDirectory));
        var version = ImageFileVersion.Read(path);
        var reference = new ImageReference(path, Path.GetFileName(path), 1, 1, version);
        using var source = await loader.LoadAsync(reference, new Progress<string>(), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return reference with
        {
            Width = source.Info.Width,
            Height = source.Info.Height
        };
    }

    private static ImageSummary? Summarize(ImageReference? image)
        => image is null ? null : new(image.Key, image.Width, image.Height, image.Version);

    private static async Task<InspectionRunResult> RunPrimitivesAsync(InspectionRecipe recipe, ImageReference image,
        RoiGeometry? region, IImageLoader loader, CancellationToken token)
    {
        var output = await new PrimitiveShapeDetectionService(loader, () => null)
            .DetectAsync(image, false, region, recipe.PrimitiveOptions, token).ConfigureAwait(false);
        return new()
        {
            Status = output.Incomplete ? "incomplete" : "completed",
            Incomplete = output.Incomplete,
            Matches = output.Shapes.Select(shape => new DetectionResult
            {
                Kind = shape.Kind.ToString().ToLowerInvariant(),
                Center = shape.Center,
                AngleRadians = shape.AngleRadians,
                Score = shape.Score,
                AreaPixels = shape.AreaPixels,
                ContourSource = "detectedBinaryContour",
                Contour = shape.Contour.ToArray()
            }).ToArray(),
            Diagnostics = new()
            {
                Windows = output.Windows,
                ClippedContours = output.ClippedContours,
                SearchMilliseconds = output.Milliseconds
            }
        };
    }

    private static async Task<InspectionRunResult> RunTemplateAsync(InspectionRecipe recipe, ImageReference training,
        ImageReference inspection, RoiGeometry teachingRoi, RoiGeometry? region, IImageLoader loader, CancellationToken token)
    {
        var service = new TemplateMatchingService(loader, () => null);
        var model = await service.TrainAsync(training, false, teachingRoi, token).ConfigureAwait(false);
        var output = await service.SearchAsync(inspection, false, model.Model, region, recipe.SearchOptions, token).ConfigureAwait(false);
        var incomplete = output.Truncated || output.MatchLimitExceeded;
        return new()
        {
            Status = incomplete ? "incomplete" : "completed",
            Incomplete = incomplete,
            Matches = output.Matches.Select(match => new DetectionResult
            {
                Center = match.ReferencePoint,
                AngleRadians = teachingRoi.Angle * Math.PI / 180,
                Score = match.Score,
                Contour = ResultContours.Transform(teachingRoi, match.ReferencePoint, teachingRoi.Angle, 1)
            }).ToArray(),
            Diagnostics = new()
            {
                TrainingMilliseconds = model.Milliseconds,
                SearchMilliseconds = output.TotalMilliseconds,
                ReadMilliseconds = output.ReadMilliseconds,
                Windows = output.Windows,
                Truncated = output.Truncated,
                MatchLimitExceeded = output.MatchLimitExceeded
            }
        };
    }

    private async Task<InspectionRunResult> RunShapeAsync(InspectionRecipe recipe, ImageReference training,
        ImageReference inspection, RoiGeometry teachingRoi, RoiGeometry? region, IImageLoader loader, CancellationToken token)
    {
        var service = new ShapeMatchingService(loader, () => null, createGpuPreprocessor,
            pipeline: ShapeSearchPipeline.Paper);
        var options = recipe.TrainingOptions with
        {
            BuildClutterModel = true
        };
        var model = await service.TrainAsync(training, false, teachingRoi, options, token).ConfigureAwait(false);
        var search = recipe.SearchOptions;
        var output = await service.SearchAsync(inspection, false, model.Model, region, search, token).ConfigureAwait(false);
        var incomplete = output.Truncated || output.MatchLimitExceeded;
        return new()
        {
            Status = incomplete ? "incomplete" : "completed",
            Incomplete = incomplete,
            Matches = output.Matches.Select(match => ResultContours.FromShape(teachingRoi, match)).ToArray(),
            Diagnostics = new()
            {
                TrainingMilliseconds = model.ReadMilliseconds + model.TrainingMilliseconds,
                SearchMilliseconds = output.TotalMilliseconds,
                ReadMilliseconds = model.ReadMilliseconds + output.ReadMilliseconds,
                Windows = output.Windows,
                Truncated = output.Truncated,
                MatchLimitExceeded = output.MatchLimitExceeded,
                Preprocessing = output.Preprocessing,
                CandidateScoring = output.CandidateScoring,
                Search = output.Diagnostics.ToArray()
            }
        };
    }
}
