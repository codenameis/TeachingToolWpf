namespace CpuMatching.Core;

public interface ITemplateMatcher
{
    /// <summary>모델 중심(0,0) 기준의 상대 특징점/표본 좌표 목록. 시각적 미리보기에 사용한다.</summary>
    IReadOnlyList<(double X, double Y)> PreviewPoints => [];

    /// <summary>전체 원본을 작은 창으로 읽으며 전역 후보와 최종 중복 제거를 유지한다.</summary>
    SearchResult FindWindowed(IGrayImageSource image, CpuSearchOptions? options = null, CancellationToken cancellationToken = default,
        Func<double, double, bool>? acceptCenter = null, ICoarseScoreAccelerator? accelerator = null)
        => throw new NotSupportedException("This matcher does not support windowed image input.");

    /// <summary>
    /// acceptCenter는 입력 영상의 픽셀 인덱스 좌표로 최종 중심을 검사하며, 최종 NMS/출력 제한 전에 적용한다.
    /// accelerator의 수명은 호출자가 소유한다. 생략하면 기존 CPU 경로이며 미지원 구현은 명시적으로 거부한다.
    /// </summary>
    SearchResult Find(CpuGrayImage image, CpuSearchOptions? options = null, CancellationToken cancellationToken = default,
        Func<double, double, bool>? acceptCenter = null, ICoarseScoreAccelerator? accelerator = null);
}
