using System.Collections.Immutable;
using System.Diagnostics;
using VisionAlgorithm.OpenSrc.Common;
using VisionImaging.Abstractions;
using VisionOcr.PpOcr;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 검사 스위트 ROI 실행 오케스트레이터.
/// 영상 열기·작업 공간 할당·타이밍 측정·보고서 요약을 담당하며,
/// 개별 알고리즘의 실행과 기하 가이드는 생성자 주입된 <see cref="InspectionAlgorithmRegistry"/>에 위임한다.
/// </summary>
public sealed class InspectionSuiteService : IInspectionSuiteService
{
    private readonly IImageLoader _loader;
    private readonly Func<VideoFrame?> _retainFrame;
    private readonly IPpOcrInferenceService? _ppOcrService;
    private readonly IPreInspectionFilterPipeline? _preFilterPipeline;
    private readonly IMaster8kSuiteRunner? _master8kSuiteRunner;

    public InspectionSuiteService(
        IImageLoader loader,
        Func<VideoFrame?> retainFrame,
        IPpOcrInferenceService? ppOcrService = null,
        IPreInspectionFilterPipeline? preFilterPipeline = null,
        IMaster8kSuiteRunner? master8kSuiteRunner = null,
        InspectionAlgorithmRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(retainFrame);

        _loader = loader;
        _retainFrame = retainFrame;
        _ppOcrService = ppOcrService;
        _preFilterPipeline = preFilterPipeline;
        _master8kSuiteRunner = master8kSuiteRunner;
        Registry = registry ?? InspectionAlgorithmRegistry.CreateDefault();
    }

    public InspectionAlgorithmRegistry Registry { get; }

    public bool IsPpOcrWired => _ppOcrService is not null;

    public string PpOcrStatusSummary => _ppOcrService?.StatusMessage ?? "PP-OCR 미연결";

    public Task<InspectionSuiteExecutionOutput> ExecuteRoiInspectionAsync(
        ImageReference image,
        bool live,
        InspectionSuiteAlgorithmKind algorithm,
        IReadOnlyList<InspectionSuiteRoiInput> targetRois,
        IReadOnlyList<InspectionSuiteRoiInput> referenceRois,
        InspectionSuiteParameters parameters,
        CancellationToken token)
        => Task.Run(async () =>
        {
            ArgumentNullException.ThrowIfNull(image);
            ArgumentNullException.ThrowIfNull(targetRois);
            ArgumentNullException.ThrowIfNull(referenceRois);
            ArgumentNullException.ThrowIfNull(parameters);
            if (targetRois.Count == 0)
            {
                throw new ArgumentException("검사할 ROI를 먼저 선택하거나 추가해 주세요.", nameof(targetRois));
            }

            IRoiInspectionAlgorithm handler = Registry.GetRequired(algorithm);
            InspectionSuiteAlgorithmKind normalizedKind = handler.Kind;

            using MatchingImageReader reader = await MatchingImageReader.OpenAsync(
                image,
                live,
                _loader,
                _retainFrame,
                token,
                _preFilterPipeline).ConfigureAwait(false);
            reader.PreferDirectGray = true;
            using var workspace = new InspectionWorkspace();

            var context = new RoiInspectionContext(
                reader,
                image.Width,
                image.Height,
                targetRois,
                referenceRois,
                parameters,
                workspace,
                _ppOcrService,
                token);

            var stopwatch = Stopwatch.StartNew();
            ImmutableArray<InspectionSuiteItemResult> items = handler.Execute(context);
            stopwatch.Stop();

            int passCount = 0;
            for (int i = 0; i < items.Length; i++)
            {
                if (items[i].IsPass)
                {
                    passCount++;
                }
            }

            string summaryTitle = items.Length == targetRois.Count
                ? $"{handler.ShortName} 완료 · 합격 {passCount}/{items.Length}개 · {stopwatch.Elapsed.TotalMilliseconds:F2} ms"
                : $"{handler.ShortName} 완료 · 합격 {passCount}/{items.Length}개 (ROI {targetRois.Count}개) · {stopwatch.Elapsed.TotalMilliseconds:F2} ms";
            string detailedReport = string.Join(
                Environment.NewLine,
                items.Select((item, idx) => $"[{idx + 1}] {item.Name}: {item.DetailLine} ({item.ElapsedMicroseconds:F1} µs)"));

            return new InspectionSuiteExecutionOutput(
                normalizedKind,
                summaryTitle,
                detailedReport,
                items,
                stopwatch.Elapsed.TotalMilliseconds);
        }, token);

    public Task<InspectionSuiteExecutionOutput> RunMaster8kSuiteAsync(CancellationToken token)
        => RunMaster8kSuiteCoreAsync(
            token,
            IMaster8kSuiteRunner.DefaultUnifiedSheetWidth,
            IMaster8kSuiteRunner.DefaultUnifiedSheetHeight);

    internal Task<InspectionSuiteExecutionOutput> RunMaster8kSuiteCoreAsync(
        CancellationToken token,
        int unifiedSheetWidth,
        int unifiedSheetHeight)
    {
        if (_master8kSuiteRunner is null)
        {
            throw new InvalidOperationException("8K 전수 통합 검증 실행기(IMaster8kSuiteRunner)가 연결되지 않았습니다.");
        }

        return _master8kSuiteRunner.RunAsync(token, unifiedSheetWidth, unifiedSheetHeight);
    }
}
