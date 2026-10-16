using System.Text.Json;
using VisionAnomaly.Learning;

namespace VisionAnomaly;

public sealed record NormalModel(int Version, Guid Id, string FeatureHash, int Width, int Height,
    AnalysisRegion Region, int TrainingFrames, float[] Memory, float AppearanceThreshold,
    float[] MotionMean, float[] MotionDeviation, int MotionSamples, DateTimeOffset CreatedAt)
{
    public void Validate()
    {
        if (FeatureHash is null || Memory is null || MotionMean is null || MotionDeviation is null)
        {
            throw new InvalidDataException("모델에 필요한 자료가 없습니다.");
        }

        Region.Validate();
        if (Version != 1 || Id == Guid.Empty || FeatureHash.Length != 64 || Width <= 0 || Height <= 0 ||
            (long)Width * Height > 8192L * 8192 || TrainingFrames < 30 || Memory.Length == 0 ||
            Memory.Length > 1024 * PatchFeatureExtractor.Dimensions || Memory.Length % PatchFeatureExtractor.Dimensions != 0 ||
            !float.IsFinite(AppearanceThreshold) || AppearanceThreshold <= 0 ||
            MotionMean.Length != FlowFeatures.Dimensions || MotionDeviation.Length != FlowFeatures.Dimensions ||
            MotionSamples < 0 || Memory.Any(value => !float.IsFinite(value)) ||
            MotionMean.Any(value => !float.IsFinite(value)) || MotionDeviation.Any(value => !float.IsFinite(value) || value < 0))
        {
            throw new InvalidDataException("정상 모델의 버전·크기·수치가 유효하지 않습니다.");
        }
    }

    public void Save(string path)
    {
        Validate();
        // 완전한 파일만 교체한다. 실패 시 원래 모델을 유지한다.
        var temporary = path + ".writing";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this));
        File.Move(temporary, path, true);
    }

    public static NormalModel Load(string path)
    {
        if (new FileInfo(path).Length > 20 * 1024 * 1024)
        {
            throw new InvalidDataException("모델 파일은20MiB를 넘을 수 없습니다.");
        }

        var model = JsonSerializer.Deserialize<NormalModel>(File.ReadAllText(path)) ?? throw new InvalidDataException("모델이 비어 있습니다.");
        model.Validate();
        return model;
    }
}
