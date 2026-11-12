using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using VisionAlgorithm.OpenSrc.IndustrialOcrOcv;

namespace VisionOcr.PpOcr;

/// <summary>
/// Owns ONNX Runtime native session lifetimes (<see cref="InferenceSession"/> and <see cref="SessionOptions"/>),
/// deterministic partial-initialization rollback, and CTC vocabulary-to-model-dimension invariant verification.
/// </summary>
internal sealed class PpOcrSessionHost : IDisposable
{
    private const int ChannelCount = 3;
    private const int RecTensorRank = 4;
    private const int CtcBlankIndex = 0;
    private const int MinIntraOpThreads = 1;
    private const int MaxIntraOpThreads = 16;
    private const int BlankOnlyExtraClasses = 1;
    private const int BlankPlusTrailingSpaceExtraClasses = 2;

    private SessionOptions? _sessionOptions;
    private InferenceSession? _detSession;
    private RecognizerSlot? _englishSlot;
    private RecognizerSlot? _koreanSlot;
    private bool _disposed;

    public PpOcrModelStatus Status { get; private set; }

    public string StatusMessage { get; private set; } = string.Empty;

    public PpOcrExecutionDevice RequestedDevice { get; }

    public PpOcrExecutionDevice ActiveDevice { get; private set; }

    public string? CudaFallbackReason { get; private set; }

    public int SessionCreationCount { get; private set; }

    public InferenceSession? DetSession => _detSession;

    public string DetInputName { get; private set; } = string.Empty;

    public RecognizerSlot? EnglishSlot => _englishSlot;

    public RecognizerSlot? KoreanSlot => _koreanSlot;

    public bool HasKoreanModel => _koreanSlot is not null;

    public bool IsDisposed => _disposed;

    public PpOcrSessionHost(PpOcrModelOptions options, Action<string>? logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        RequestedDevice = options.Device;
        ActiveDevice = PpOcrExecutionDevice.Cpu;

        List<string> missing = CollectMissingRequiredFiles(options);
        if (missing.Count > 0)
        {
            Status = PpOcrModelStatus.MissingModel;
            StatusMessage = $"모델 미설치 ({string.Join(", ", missing)})";
            return;
        }

        bool includeKorean = !string.IsNullOrWhiteSpace(options.KoreanRecModelPath)
            || !string.IsNullOrWhiteSpace(options.KoreanDictionaryPath);

        if (includeKorean &&
            (string.IsNullOrWhiteSpace(options.KoreanRecModelPath) ||
             string.IsNullOrWhiteSpace(options.KoreanDictionaryPath) ||
             !File.Exists(options.KoreanRecModelPath) ||
             !File.Exists(options.KoreanDictionaryPath)))
        {
            Status = PpOcrModelStatus.MissingModel;
            StatusMessage = "모델 미설치 (한국어 PP-OCR 모델 또는 사전 파일 누락)";
            return;
        }

        if (options.Device == PpOcrExecutionDevice.Cuda)
        {
            if (TryInitializeSessions(options, includeKorean, useCuda: true, out Exception? cudaError))
            {
                ActiveDevice = PpOcrExecutionDevice.Cuda;
                Status = PpOcrModelStatus.Ready;
                StatusMessage = $"PP-OCRv4 ONNX 준비 완료 (CUDA:{options.CudaDeviceId} 실험용 · 단일 소형 ROI는 CPU 권장)";
                return;
            }

            CudaFallbackReason = cudaError?.Message ?? "알 수 없는 CUDA 초기화 오류";
            logger?.Invoke($"[PP-OCR CUDA 폴백] CUDA:{options.CudaDeviceId} 초기화 실패로 CPU 세션으로 전환합니다: {CudaFallbackReason}");
            ReleaseSessionsOnly();
        }

        try
        {
            InitializeSessionsOrThrow(options, includeKorean, useCuda: false);
            ActiveDevice = PpOcrExecutionDevice.Cpu;
            Status = PpOcrModelStatus.Ready;
            StatusMessage = CudaFallbackReason is null
                ? "PP-OCRv4 ONNX 준비 완료 (CPU)"
                : $"PP-OCRv4 ONNX 준비 완료 (CPU 폴백: {CudaFallbackReason})";
        }
        catch (Exception ex)
        {
            ReleaseSessionsOnly();
            Status = PpOcrModelStatus.LoadFailed;
            StatusMessage = $"PP-OCRv4 ONNX 모델 로드 실패: {ex.Message}";
            throw new InvalidDataException(StatusMessage, ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReleaseSessionsOnly();
        Status = PpOcrModelStatus.Disposed;
        StatusMessage = "PP-OCRv4 ONNX 세션 해제됨";
    }

    internal static char[] LoadAndValidateCtcVocabulary(string dictPath, int modelOutputClasses)
    {
        string rawContent = File.ReadAllText(dictPath, Encoding.UTF8);
        if (rawContent.Length > 0 && rawContent[0] == '\uFEFF')
        {
            rawContent = rawContent.Substring(1);
        }

        string[] splitLines = rawContent.Split('\n');
        int lineCount = splitLines.Length;
        if (lineCount > 0 && splitLines[lineCount - 1].Length == 0)
        {
            // Ignore only the single trailing empty segment after a terminal '\n'.
            lineCount--;
        }

        if (lineCount <= 0)
        {
            throw new InvalidDataException($"PP-OCR 사전 파일이 비어 있습니다: {dictPath}");
        }

        bool appendTrailingSpace = true;
        if (modelOutputClasses > 0)
        {
            if (modelOutputClasses == lineCount + BlankPlusTrailingSpaceExtraClasses)
            {
                appendTrailingSpace = true;
            }
            else if (modelOutputClasses == lineCount + BlankOnlyExtraClasses)
            {
                appendTrailingSpace = false;
            }
            else
            {
                throw new InvalidDataException(
                    $"PP-OCR 사전 줄 수({lineCount})와 ONNX 출력 클래스 차원({modelOutputClasses})이 일치하지 않습니다: {Path.GetFileName(dictPath)}");
            }
        }

        var vocabulary = new char[appendTrailingSpace ? lineCount + 1 : lineCount];
        for (int i = 0; i < lineCount; i++)
        {
            string line = splitLines[i];
            if (line.EndsWith('\r'))
            {
                line = line.Substring(0, line.Length - 1);
            }

            if (line.Length == 0)
            {
                throw new InvalidDataException(
                    $"PP-OCR 사전 파일 {i + 1}번째 줄이 비어 있어 클래스 인덱스가 어긋납니다: {Path.GetFileName(dictPath)}");
            }

            vocabulary[i] = line[0];
        }

        if (appendTrailingSpace)
        {
            vocabulary[lineCount] = ' ';
        }

        return vocabulary;
    }

    private bool TryInitializeSessions(
        PpOcrModelOptions options,
        bool includeKorean,
        bool useCuda,
        out Exception? error)
    {
        try
        {
            InitializeSessionsOrThrow(options, includeKorean, useCuda);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            ReleaseSessionsOnly();
            error = ex;
            return false;
        }
    }

    private void InitializeSessionsOrThrow(
        PpOcrModelOptions options,
        bool includeKorean,
        bool useCuda)
    {
        SessionOptions? localOpts = null;
        InferenceSession? localDet = null;
        InferenceSession? localEn = null;
        InferenceSession? localKo = null;
        RecognizerSlot? localEnSlot = null;
        RecognizerSlot? localKoSlot = null;

        try
        {
            localOpts = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                EnableCpuMemArena = true,
                EnableMemoryPattern = true,
                IntraOpNumThreads = Math.Clamp(options.IntraOpThreads, MinIntraOpThreads, MaxIntraOpThreads),
                InterOpNumThreads = 1,
            };

            if (useCuda)
            {
                localOpts.AppendExecutionProvider_CUDA(options.CudaDeviceId);
            }

            localDet = CreateSessionTracked(options.DetectionModelPath, localOpts);
            string detInputName = localDet.InputMetadata.Keys.First();

            localEn = CreateSessionTracked(options.EnglishRecModelPath, localOpts);
            int enOutputClasses = ReadModelOutputClasses(localEn);
            char[] enVocabulary = LoadAndValidateCtcVocabulary(options.EnglishDictionaryPath, enOutputClasses);
            localEnSlot = new RecognizerSlot(localEn, enVocabulary);
            localEn = null; // Ownership transferred to localEnSlot

            if (includeKorean &&
                !string.IsNullOrWhiteSpace(options.KoreanRecModelPath) &&
                !string.IsNullOrWhiteSpace(options.KoreanDictionaryPath))
            {
                localKo = CreateSessionTracked(options.KoreanRecModelPath, localOpts);
                int koOutputClasses = ReadModelOutputClasses(localKo);
                char[] koVocabulary = LoadAndValidateCtcVocabulary(options.KoreanDictionaryPath, koOutputClasses);
                localKoSlot = new RecognizerSlot(localKo, koVocabulary);
                localKo = null; // Ownership transferred to localKoSlot
            }

            _sessionOptions = localOpts;
            _detSession = localDet;
            DetInputName = detInputName;
            _englishSlot = localEnSlot;
            _koreanSlot = localKoSlot;

            localOpts = null;
            localDet = null;
            localEnSlot = null;
            localKoSlot = null;
        }
        finally
        {
            localKoSlot?.Dispose();
            localKo?.Dispose();
            localEnSlot?.Dispose();
            localEn?.Dispose();
            localDet?.Dispose();
            localOpts?.Dispose();
        }
    }

    private InferenceSession CreateSessionTracked(string modelPath, SessionOptions sessionOptions)
    {
        var session = new InferenceSession(modelPath, sessionOptions);
        SessionCreationCount++;
        return session;
    }

    private static int ReadModelOutputClasses(InferenceSession recSession)
    {
        string outKey = recSession.OutputMetadata.Keys.First();
        int[] dims = recSession.OutputMetadata[outKey].Dimensions;
        return dims.Length > 0 ? dims[dims.Length - 1] : -1;
    }

    private static List<string> CollectMissingRequiredFiles(PpOcrModelOptions options)
    {
        var missing = new List<string>(ChannelCount);
        if (string.IsNullOrWhiteSpace(options.DetectionModelPath) || !File.Exists(options.DetectionModelPath))
        {
            missing.Add("ch_PP-OCRv4_det");
        }

        if (string.IsNullOrWhiteSpace(options.EnglishRecModelPath) || !File.Exists(options.EnglishRecModelPath))
        {
            missing.Add("en_PP-OCRv4_rec");
        }

        if (string.IsNullOrWhiteSpace(options.EnglishDictionaryPath) || !File.Exists(options.EnglishDictionaryPath))
        {
            missing.Add("en_dict.txt");
        }

        return missing;
    }

    private void ReleaseSessionsOnly()
    {
        _koreanSlot?.Dispose();
        _koreanSlot = null;
        _englishSlot?.Dispose();
        _englishSlot = null;
        _detSession?.Dispose();
        _detSession = null;
        _sessionOptions?.Dispose();
        _sessionOptions = null;
    }

    internal sealed class RecognizerSlot : IDisposable
    {
        private readonly char[] _vocabulary;
        private readonly Dictionary<(OcrAllowedCharacterSet, int), bool[]> _maskCache = [];
        private readonly object _maskLock = new();

        public InferenceSession Session { get; }

        public string InputName { get; }

        public bool SupportsDynamicBatch { get; }

        public RecognizerSlot(InferenceSession session, char[] vocabulary)
        {
            Session = session;
            InputName = session.InputMetadata.Keys.First();
            int[] dims = session.InputMetadata[InputName].Dimensions;
            SupportsDynamicBatch = dims.Length == RecTensorRank && dims[0] <= 0;
            _vocabulary = vocabulary;
        }

        public bool TryGetCharacter(int ctcClassIndex, out char ch)
        {
            if (ctcClassIndex <= CtcBlankIndex)
            {
                ch = '\0';
                return false;
            }

            int vocabIdx = ctcClassIndex - 1;
            if ((uint)vocabIdx < (uint)_vocabulary.Length)
            {
                ch = _vocabulary[vocabIdx];
                return true;
            }

            ch = '\0';
            return false;
        }

        public ReadOnlySpan<bool> GetAllowedMask(OcrAllowedCharacterSet allowedSet, int numClasses)
        {
            lock (_maskLock)
            {
                if (_maskCache.TryGetValue((allowedSet, numClasses), out bool[]? cached))
                {
                    return cached;
                }

                var mask = new bool[numClasses];
                mask[CtcBlankIndex] = true;
                for (int k = 1; k < numClasses; k++)
                {
                    if (TryGetCharacter(k, out char ch))
                    {
                        mask[k] = ch == ' ' || OcrCharacterSetFilter.Allows(allowedSet, ch);
                    }
                }

                _maskCache[(allowedSet, numClasses)] = mask;
                return mask;
            }
        }

        public void Dispose() => Session.Dispose();
    }
}
