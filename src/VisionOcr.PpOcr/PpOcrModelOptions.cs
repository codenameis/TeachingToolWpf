using System.IO;

namespace VisionOcr.PpOcr;

/// <summary>
/// Externally injected model paths and runtime configuration for <see cref="PpOcrInferenceService"/>.
/// Contains zero hardcoded file paths; callers supply explicit locations at composition root.
/// </summary>
public sealed record PpOcrModelOptions(
    string DetectionModelPath,
    string EnglishRecModelPath,
    string EnglishDictionaryPath,
    string? KoreanRecModelPath = null,
    string? KoreanDictionaryPath = null,
    PpOcrExecutionDevice Device = PpOcrModelOptions.DefaultDevice,
    int CudaDeviceId = PpOcrModelOptions.DefaultCudaDeviceId,
    int IntraOpThreads = PpOcrModelOptions.DefaultIntraOpThreads)
{
    public const PpOcrExecutionDevice DefaultDevice = PpOcrExecutionDevice.Cpu;
    public const int DefaultCudaDeviceId = 0;
    public const int DefaultIntraOpThreads = 4;

    public const string DefaultDetModelFileName = "ch_PP-OCRv4_det_mobile.onnx";
    public const string DefaultEnglishRecModelFileName = "en_PP-OCRv4_rec_mobile.onnx";
    public const string DefaultEnglishDictFileName = "en_dict.txt";
    public const string DefaultKoreanRecModelFileName = "korean_PP-OCRv4_rec_mobile.onnx";
    public const string DefaultKoreanDictFileName = "korean_dict.txt";

    public static PpOcrModelOptions FromDirectory(
        string modelDirectory,
        PpOcrExecutionDevice device = DefaultDevice,
        int cudaDeviceId = DefaultCudaDeviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);
        string koreanModel = Path.Combine(modelDirectory, DefaultKoreanRecModelFileName);
        string koreanDict = Path.Combine(modelDirectory, DefaultKoreanDictFileName);
        bool hasKoreanFiles = File.Exists(koreanModel) && File.Exists(koreanDict);

        return new PpOcrModelOptions(
            DetectionModelPath: Path.Combine(modelDirectory, DefaultDetModelFileName),
            EnglishRecModelPath: Path.Combine(modelDirectory, DefaultEnglishRecModelFileName),
            EnglishDictionaryPath: Path.Combine(modelDirectory, DefaultEnglishDictFileName),
            KoreanRecModelPath: hasKoreanFiles ? koreanModel : null,
            KoreanDictionaryPath: hasKoreanFiles ? koreanDict : null,
            Device: device,
            CudaDeviceId: cudaDeviceId);
    }
}
