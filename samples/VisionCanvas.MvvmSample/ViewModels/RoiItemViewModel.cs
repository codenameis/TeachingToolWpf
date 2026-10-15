using VisionCanvas.MvvmSample.Models;

namespace VisionCanvas.MvvmSample.ViewModels;

// 목록 한 줄에 필요한 값만 제공한다. 설정이 없는 새 ROI를 검사 완료로 표시하지 않는다.
public sealed record RoiItemViewModel(Guid Id, string Name, string Shape, AlgorithmSettings? Algorithm)
{
    public string AlgorithmName => Algorithm?.Name ?? (Shape == "그룹" ? "자식 ROI 묶음" : "알고리즘 미지정");
    public string Parameters => Algorithm?.Parameters ?? "";
}
